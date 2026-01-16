using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Queue.PostProcessors;

public class CreateStrmFilesPostProcessor(ConfigManager configManager, DavDatabaseClient dbClient)
{
    /// <summary>
    /// Create strm files using the default completed downloads directory (for Arr import)
    /// </summary>
    public async Task CreateStrmFilesAsync()
    {
        await CreateStrmFilesAsync(configManager.GetStrmCompletedDownloadDir()).ConfigureAwait(false);
    }

    /// <summary>
    /// Create strm files in a specific target directory (for dual output to Emby library)
    /// </summary>
    public async Task CreateStrmFilesAsync(string targetDirectory)
    {
        // Add strm files to the target directory
        var videoItems = dbClient.Ctx.ChangeTracker.Entries<DavItem>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .Where(x => x.Type != DavItem.ItemType.Directory)
            .Where(x => FilenameUtil.IsVideoFile(x.Name));
        foreach (var videoItem in videoItems)
        {
            await CreateStrmFileAsync(videoItem, targetDirectory).ConfigureAwait(false);
            // Only update cache for default strm path (Arr imports)
            if (targetDirectory == configManager.GetStrmCompletedDownloadDir())
            {
                OrganizedLinksUtil.UpdateCacheEntry(videoItem.Id, GetStrmFilePath(videoItem, targetDirectory));
            }
        }
    }

    private async Task CreateStrmFileAsync(DavItem davItem, string targetDirectory)
    {
        // create necessary directories if they don't already exist
        var strmFilePath = GetStrmFilePath(davItem, targetDirectory);
        var directoryName = Path.GetDirectoryName(strmFilePath);
        if (directoryName != null)
            await Task.Run(() => Directory.CreateDirectory(directoryName)).ConfigureAwait(false);

        // create the strm file
        var targetUrl = GetStrmTargetUrl(davItem);
        await File.WriteAllTextAsync(strmFilePath, targetUrl).ConfigureAwait(false);
    }

    private string GetStrmFilePath(DavItem davItem, string targetDirectory)
    {
        var path = davItem.Path + ".strm";
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Join(targetDirectory, Path.Join(parts[2..]));
    }

    private string GetStrmTargetUrl(DavItem davItem)
    {
        var baseUrl = configManager.GetBaseUrl();
        if (baseUrl.EndsWith('/')) baseUrl = baseUrl.TrimEnd('/');
        var pathUrl = DatabaseStoreSymlinkFile.GetTargetPath(davItem.Id, "", '/');
        if (pathUrl.StartsWith('/')) pathUrl = pathUrl.TrimStart('/');
        var strmKey = configManager.GetStrmKey();
        var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(strmKey, pathUrl);
        var extension = Path.GetExtension(davItem.Name).ToLower().TrimStart('.');
        return $"{baseUrl}/view/{pathUrl}?downloadKey={downloadKey}&extension={extension}";
    }
}
