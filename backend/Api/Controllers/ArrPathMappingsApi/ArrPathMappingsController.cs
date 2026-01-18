using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.ArrPathMappingsApi;

[ApiController]
[Route("api/arr-path-mappings")]
public class ArrPathMappingsController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    protected override async Task<IActionResult> HandleRequest()
    {
        if (HttpContext.Request.Method == HttpMethods.Get)
        {
            return await HandleGet();
        }
        else if (HttpContext.Request.Method == HttpMethods.Post)
        {
            return await HandlePost();
        }
        return BadRequest(new { status = false, error = "Method not allowed" });
    }

    private Task<IActionResult> HandleGet()
    {
        var allMappings = configManager.GetAllArrPathMappings();
        // Return with explicit casing to match frontend expectations
        var result = new Dictionary<string, List<object>>();
        foreach (var kvp in allMappings)
        {
            result[kvp.Key] = kvp.Value.Mappings
                .Select(m => (object)new { NzbdavPrefix = m.NzbdavPrefix, ArrPrefix = m.ArrPrefix })
                .ToList();
        }
        return Task.FromResult<IActionResult>(Ok(new { status = true, mappings = result }));
    }

    private async Task<IActionResult> HandlePost()
    {
        var form = await HttpContext.Request.ReadFormAsync();
        var hostUrl = form["host"].ToString();
        var mappingsJson = form["mappings"].ToString();

        if (string.IsNullOrEmpty(hostUrl))
        {
            return BadRequest(new { status = false, error = "Host URL is required" });
        }

        // Parse the mappings JSON (case-insensitive)
        List<PathMapping> mappings;
        try
        {
            mappings = JsonSerializer.Deserialize<List<PathMapping>>(mappingsJson, CamelCaseOptions)
                ?? new List<PathMapping>();
        }
        catch (Exception ex)
        {
            return BadRequest(new { status = false, error = $"Invalid mappings JSON: {ex.Message}" });
        }

        // Create the config key
        var configKey = GetPathMappingKey(hostUrl);
        var arrPathMappings = new ArrPathMappings { Mappings = mappings };
        var configValue = JsonSerializer.Serialize(arrPathMappings);

        // Save to database
        var existingItem = await dbClient.Ctx.ConfigItems
            .FirstOrDefaultAsync(c => c.ConfigName == configKey, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (existingItem != null)
        {
            existingItem.ConfigValue = configValue;
            dbClient.Ctx.ConfigItems.Update(existingItem);
        }
        else
        {
            dbClient.Ctx.ConfigItems.Add(new ConfigItem
            {
                ConfigName = configKey,
                ConfigValue = configValue
            });
        }

        await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        // Update ConfigManager
        configManager.UpdateValues([new ConfigItem { ConfigName = configKey, ConfigValue = configValue }]);

        return Ok(new { status = true });
    }

    private static string GetPathMappingKey(string instanceHost)
    {
        var normalized = instanceHost
            .Replace("http://", "")
            .Replace("https://", "")
            .Replace(":", "-")
            .Replace("/", "")
            .ToLowerInvariant();
        return $"arr.path-mappings.{normalized}";
    }
}

[ApiController]
[Route("api/arr-path-mappings/test")]
public class ArrPathMappingsTestController(ConfigManager configManager) : BaseApiController
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task<IActionResult> HandleRequest()
    {
        if (HttpContext.Request.Method != HttpMethods.Post)
        {
            return BadRequest(new { status = false, error = "Method not allowed" });
        }

        var form = await HttpContext.Request.ReadFormAsync();
        var hostUrl = form["host"].ToString();
        var apiKey = form["apiKey"].ToString();
        var nzbdavPrefix = form["nzbdavPrefix"].ToString().Trim().TrimEnd('/') + "/";
        var arrPrefix = form["arrPrefix"].ToString().Trim().TrimEnd('/') + "/";

        if (string.IsNullOrEmpty(hostUrl) || string.IsNullOrEmpty(apiKey))
        {
            return BadRequest(new { status = false, error = "Host and API key required" });
        }

        if (string.IsNullOrEmpty(nzbdavPrefix) || string.IsNullOrEmpty(arrPrefix))
        {
            return BadRequest(new { status = false, error = "Both path prefixes required" });
        }

        try
        {
            // Step 1: Check if NZBDav prefix directory exists
            if (!System.IO.Directory.Exists(nzbdavPrefix))
            {
                return Ok(new {
                    status = true,
                    success = false,
                    message = $"NZBDav path not found: {nzbdavPrefix}",
                    testResults = new List<object>()
                });
            }

            // Step 2: Get a sample folder from NZBDav (skip system folders like @Recycle)
            var sampleFolder = GetSampleFolder(nzbdavPrefix);

            if (sampleFolder == null)
            {
                // NZBDav is empty - just verify Arr prefix is accessible
                var arrAccessible = await CheckArrPathAccessible(hostUrl, apiKey, arrPrefix);
                return Ok(new {
                    status = true,
                    success = arrAccessible,
                    message = arrAccessible
                        ? "Paths accessible but no NZBDav content to verify"
                        : $"Arr path not accessible: {arrPrefix}",
                    testResults = new List<object>()
                });
            }

            // Step 3: Check if this folder exists in Arr's filesystem view
            var existsInArr = await CheckFolderExistsInArr(hostUrl, apiKey, arrPrefix, sampleFolder);
            var nzbdavPath = nzbdavPrefix + sampleFolder;
            var arrPath = arrPrefix + sampleFolder;

            return Ok(new
            {
                status = true,
                success = existsInArr,
                message = existsInArr
                    ? "Mapping verified"
                    : "Folder not found in Arr filesystem",
                testResults = new List<object>
                {
                    new
                    {
                        nzbdavPath = nzbdavPath,
                        arrPath = arrPath,
                        existsInArr = existsInArr
                    }
                }
            });
        }
        catch (Exception ex)
        {
            return Ok(new { status = true, success = false, message = $"Error: {ex.Message}" });
        }
    }

    private string? GetSampleFolder(string prefix)
    {
        try
        {
            var dirs = System.IO.Directory.GetDirectories(prefix)
                .Select(d => System.IO.Path.GetFileName(d))
                .Where(name => !name.StartsWith("@") && !name.StartsWith("."))
                .Take(1)
                .ToList();

            return dirs.Count > 0 ? dirs[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> CheckArrPathAccessible(string host, string apiKey, string path)
    {
        try
        {
            var encodedPath = Uri.EscapeDataString(path);
            var response = await _httpClient.GetAsync($"{host.TrimEnd('/')}/api/v3/filesystem?path={encodedPath}&apikey={apiKey}");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckFolderExistsInArr(string host, string apiKey, string arrPrefix, string folderName)
    {
        try
        {
            var encodedPath = Uri.EscapeDataString(arrPrefix);
            var response = await _httpClient.GetAsync($"{host.TrimEnd('/')}/api/v3/filesystem?path={encodedPath}&apikey={apiKey}");
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<FilesystemResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return result?.Directories?.Any(d => d.Name == folderName) == true;
        }
        catch
        {
            return false;
        }
    }

    private record FilesystemResult(List<DirectoryEntry>? Directories);
    private record DirectoryEntry(string? Name, string? Path);
}

[ApiController]
[Route("api/arr-path-mappings/root-folders")]
public class ArrRootFoldersController : BaseApiController
{
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task<IActionResult> HandleRequest()
    {
        if (HttpContext.Request.Method != HttpMethods.Post)
        {
            return BadRequest(new { status = false, error = "Method not allowed" });
        }

        var form = await HttpContext.Request.ReadFormAsync();
        var hostUrl = form["host"].ToString();
        var apiKey = form["apiKey"].ToString();

        if (string.IsNullOrEmpty(hostUrl) || string.IsNullOrEmpty(apiKey))
        {
            return BadRequest(new { status = false, error = "Host and API key required" });
        }

        try
        {
            var response = await _httpClient.GetAsync($"{hostUrl.TrimEnd('/')}/api/v3/rootfolder?apikey={apiKey}");
            if (!response.IsSuccessStatusCode)
            {
                return Ok(new { status = false, error = $"Failed to fetch root folders: {response.StatusCode}" });
            }

            var json = await response.Content.ReadAsStringAsync();
            var rootFolders = JsonSerializer.Deserialize<List<RootFolder>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return Ok(new
            {
                status = true,
                rootFolders = rootFolders?.Select(r => new { path = r.Path, freeSpace = r.FreeSpace }).ToList()
            });
        }
        catch (Exception ex)
        {
            return Ok(new { status = false, error = $"Error fetching root folders: {ex.Message}" });
        }
    }

    private record RootFolder(string? Path, long? FreeSpace);
}
