using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public class RcloneRcService(ConfigManager configManager, IHttpClientFactory httpClientFactory, IServiceScopeFactory serviceScopeFactory)
{
    private const string RefreshEndpoint = "vfs/refresh";
    private const string ForgetEndpoint = "vfs/forget";

    public async Task<bool> RefreshAsync(string? dir = null)
    {
        var config = configManager.GetRcloneRcConfig();
        if (!config.Enabled || string.IsNullOrEmpty(config.Url)) return false;

        var parameters = new Dictionary<string, object>
        {
            ["recursive"] = "true"
        };
        
        if (!string.IsNullOrEmpty(dir))
        {
            parameters["dir"] = dir;
        }

        return await SendRequestAsync(config, RefreshEndpoint, parameters).ConfigureAwait(false);
    }

    public async Task<bool> ForgetAsync(string[] files)
    {
        var config = configManager.GetRcloneRcConfig();
        if (!config.Enabled || string.IsNullOrEmpty(config.Url)) return false;

        if (files.Length == 0) return true;

        var allSuccess = true;
        foreach (var file in files)
        {
            Log.Information("[RcloneRc] Flushing cache for file: {FilePath}", file);
            var parameters = new Dictionary<string, object>
            {
                ["file"] = file
            };

            if (!await SendRequestAsync(config, ForgetEndpoint, parameters).ConfigureAwait(false))
            {
                allSuccess = false;
            }

            // Also delete from disk cache if configured
            DeleteFromDiskCache(config.CachePath, file);
        }

        return allSuccess;
    }

    /// <summary>
    /// Flushes Rclone VFS cache for a file across ALL configured Rclone instances.
    /// This should be called after repairs to ensure all clients see the updated state.
    /// </summary>
    /// <param name="davItemId">The DavItem ID (used to construct .ids path)</param>
    /// <param name="contentPath">The content path (e.g., /content/movies/Movie/file.mkv)</param>
    public async Task ForgetAllInstancesAsync(Guid davItemId, string contentPath)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            // Get all enabled Rclone instances with dir refresh enabled
            var instances = await dbContext.RcloneInstances
                .AsNoTracking()
                .Where(i => i.IsEnabled && i.EnableDirRefresh)
                .ToListAsync()
                .ConfigureAwait(false);

            if (instances.Count == 0)
            {
                Log.Debug("[RcloneRc] No Rclone instances configured for dir refresh, skipping cache flush");
                return;
            }

            // Build paths to flush
            var idStr = davItemId.ToString();
            var idsPath = ".ids/" + string.Join("/", idStr.Take(5).Select(c => c.ToString())) + "/" + idStr;
            var normalizedContentPath = contentPath.TrimStart('/');

            Log.Information("[RcloneRc] Flushing cache for {ItemId} across {Count} Rclone instances", davItemId, instances.Count);

            var tasks = instances.Select(async instance =>
            {
                try
                {
                    using var client = new RcloneClient(instance);

                    // Forget both the .ids path and content path
                    await client.VfsForgetAsync(idsPath).ConfigureAwait(false);
                    await client.VfsForgetAsync(normalizedContentPath).ConfigureAwait(false);

                    // Also delete from disk cache
                    client.DeleteFromDiskCache(idsPath);
                    client.DeleteFromDiskCache(normalizedContentPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[RcloneRc] Failed to flush cache on instance {Instance}", instance.Name);
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            Log.Information("[RcloneRc] Cache flush completed for {ItemId}", davItemId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RcloneRc] Failed to flush cache across instances for {ItemId}", davItemId);
        }
    }

    /// <summary>
    /// Deletes a file from the rclone VFS disk cache.
    /// Rclone VFS cache mirrors the WebDAV path structure directly:
    /// e.g., /content/movies/Movie/Movie.mkv becomes:
    /// {CachePath}/vfs/{remote}/content/movies/Movie/Movie.mkv
    /// </summary>
    private void DeleteFromDiskCache(string? cachePath, string file)
    {
        if (string.IsNullOrEmpty(cachePath))
        {
            Log.Debug("[RcloneRc] CachePath not configured, skipping disk cache deletion");
            return;
        }

        try
        {
            // Normalize the file path - remove leading slashes for Path.Combine
            var relativePath = file.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relativePath))
            {
                Log.Debug("[RcloneRc] Empty file path, skipping cache deletion");
                return;
            }

            var cacheDir = cachePath.TrimEnd(Path.DirectorySeparatorChar);

            // Look for vfs subdirectory
            var vfsPath = Path.Combine(cacheDir, "vfs");
            if (!Directory.Exists(vfsPath))
            {
                Log.Debug("[RcloneRc] VFS cache directory not found: {Path}", vfsPath);
                return;
            }

            // Search all remote directories under vfs
            var remoteDirectories = Directory.GetDirectories(vfsPath);
            Log.Information("[RcloneRc] Deleting from disk cache: {RelativePath} (searching {Count} remotes)",
                relativePath, remoteDirectories.Length);

            foreach (var remoteDir in remoteDirectories)
            {
                // Rclone VFS cache mirrors the path directly (no nested structure)
                var fullCachePath = Path.Combine(remoteDir, relativePath);
                Log.Debug("[RcloneRc] Checking cache path: {Path}", fullCachePath);

                if (File.Exists(fullCachePath))
                {
                    Log.Information("[RcloneRc] Deleting cached file: {Path}", fullCachePath);
                    File.Delete(fullCachePath);
                }

                // Also check vfsMeta for metadata files
                var vfsMetaPath = Path.Combine(cacheDir, "vfsMeta", Path.GetFileName(remoteDir), relativePath);
                if (File.Exists(vfsMetaPath))
                {
                    Log.Information("[RcloneRc] Deleting cached metadata: {Path}", vfsMetaPath);
                    File.Delete(vfsMetaPath);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RcloneRc] Failed to delete cache file for: {File}", file);
        }
    }

    private async Task<bool> SendRequestAsync(RcloneRcConfig config, string command, Dictionary<string, object> parameters)
    {
        try
        {
            var client = httpClientFactory.CreateClient("RcloneRc");
            var url = config.Url!.TrimEnd('/') + "/" + command;

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            
            // Set Authentication if provided
            if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
            {
                var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{config.Username}:{config.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
            }

            var json = JsonSerializer.Serialize(parameters);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            Log.Information("[RcloneRc] Sending command {Command} to {Url} with parameters: {Json}", command, url, json);

            var response = await client.SendAsync(request).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Log.Information("[RcloneRc] Command {Command} successful. Response: {Response}", command, responseBody);
                return true;
            }
            else
            {
                Log.Warning("[RcloneRc] Command {Command} failed with status {Status}. Response: {Response}", command, response.StatusCode, responseBody);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RcloneRc] Failed to send command {Command}", command);
            return false;
        }
    }
}
