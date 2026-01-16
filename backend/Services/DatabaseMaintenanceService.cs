using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public class DatabaseMaintenanceService(IServiceScopeFactory scopeFactory, ConfigManager configManager) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("[DatabaseMaintenance] Service started. Scheduled to run every 24 hours.");

        // Wait a bit on startup to let other heavy tasks finish
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformMaintenanceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DatabaseMaintenance] Error occurred during scheduled maintenance.");
            }

            // Run daily
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    public async Task PerformMaintenanceAsync(CancellationToken stoppingToken)
    {
        Log.Information("[DatabaseMaintenance] Starting daily database maintenance...");

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

        // 1. Prune BandwidthSamples (> 30 days)
        var bandwidthCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var bandwidthDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"BandwidthSamples\" WHERE \"Timestamp\" < {bandwidthCutoff}", 
            stoppingToken);
        if (bandwidthDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from BandwidthSamples.", bandwidthDeleted);

        // 2. Prune HealthCheckResults (> 30 days)
        // Keep Deleted items longer? For now, treat all same as 30 days history.
        var healthCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var healthDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"HealthCheckResults\" WHERE \"CreatedAt\" < {healthCutoff}", 
            stoppingToken);
        if (healthDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from HealthCheckResults.", healthDeleted);

        // 3. Prune MissingArticleEvents (> 14 days)
        // These can grow huge, so aggressive pruning is good.
        var eventsCutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
        var eventsDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"MissingArticleEvents\" WHERE \"Timestamp\" < {eventsCutoff}", 
            stoppingToken);
        if (eventsDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from MissingArticleEvents.", eventsDeleted);

        // 4. Prune MissingArticleSummaries (> 14 days last seen)
        var summaryCutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
        var summariesDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"MissingArticleSummaries\" WHERE \"LastSeen\" < {summaryCutoff}",
            stoppingToken);
        if (summariesDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from MissingArticleSummaries.", summariesDeleted);

        // 5. Cleanup old hidden history items (> 30 days)
        var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        try
        {
            await dbClient.CleanupOldHiddenHistoryItemsAsync(30, stoppingToken);
            Log.Information("[DatabaseMaintenance] Cleaned up old hidden history items.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DatabaseMaintenance] Error cleaning up old hidden history items.");
        }

        // 6. Cleanup orphan STRM files (dual output mode)
        // When using symlinks for Plex + STRM for Emby, remove .strm files whose symlinks no longer exist
        if (configManager.GetAlsoCreateStrm())
        {
            try
            {
                var cleanedCount = await CleanupOrphanStrmFilesAsync(stoppingToken);
                if (cleanedCount > 0)
                    Log.Information("[DatabaseMaintenance] Cleaned up {Count} orphan STRM files.", cleanedCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DatabaseMaintenance] Error cleaning up orphan STRM files.");
            }
        }

        // 7. Optimize WAL (Checkpoint)
        // This merges the WAL file into the main DB and truncates it, keeping disk usage low.
        Log.Information("[DatabaseMaintenance] Checkpointing WAL file...");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", stoppingToken);

        Log.Information("[DatabaseMaintenance] Maintenance completed successfully.");
    }

    /// <summary>
    /// Cleans up orphan .strm files in the STRM library directory.
    /// An orphan is a .strm file whose corresponding symlink no longer exists in the mount directory.
    /// </summary>
    private async Task<int> CleanupOrphanStrmFilesAsync(CancellationToken stoppingToken)
    {
        var strmLibraryDir = configManager.GetStrmLibraryDir();
        var mountDir = configManager.GetRcloneMountDir();

        if (string.IsNullOrEmpty(strmLibraryDir) || string.IsNullOrEmpty(mountDir))
        {
            Log.Debug("[DatabaseMaintenance] STRM cleanup skipped: library or mount dir not configured.");
            return 0;
        }

        if (!Directory.Exists(strmLibraryDir))
        {
            Log.Debug("[DatabaseMaintenance] STRM cleanup skipped: library dir does not exist: {Dir}", strmLibraryDir);
            return 0;
        }

        var cleanedCount = 0;
        var strmFiles = Directory.EnumerateFiles(strmLibraryDir, "*.strm", SearchOption.AllDirectories);

        foreach (var strmFile in strmFiles)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                // Get relative path from strm library dir
                var relativePath = Path.GetRelativePath(strmLibraryDir, strmFile);
                // Remove .strm extension to get the media file path
                var mediaRelativePath = relativePath[..^5]; // Remove ".strm"
                // Check if corresponding file exists in mount directory
                var mediaPath = Path.Combine(mountDir, mediaRelativePath);

                if (!File.Exists(mediaPath) && !IsSymlink(mediaPath))
                {
                    Log.Debug("[DatabaseMaintenance] Removing orphan STRM file: {File}", strmFile);
                    await Task.Run(() => File.Delete(strmFile), stoppingToken);
                    cleanedCount++;

                    // Also remove empty parent directories
                    await CleanupEmptyDirectoriesAsync(Path.GetDirectoryName(strmFile)!, strmLibraryDir, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DatabaseMaintenance] Error processing STRM file: {File}", strmFile);
            }
        }

        return cleanedCount;
    }

    /// <summary>
    /// Checks if a path is a symlink (even if broken).
    /// </summary>
    private static bool IsSymlink(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes empty directories up to the root directory.
    /// </summary>
    private static async Task CleanupEmptyDirectoriesAsync(string directory, string rootDirectory, CancellationToken stoppingToken)
    {
        while (!string.IsNullOrEmpty(directory) && directory != rootDirectory)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                var entries = Directory.EnumerateFileSystemEntries(directory);
                if (!entries.Any())
                {
                    await Task.Run(() => Directory.Delete(directory), stoppingToken);
                    Log.Debug("[DatabaseMaintenance] Removed empty directory: {Dir}", directory);
                    directory = Path.GetDirectoryName(directory)!;
                }
                else
                {
                    break; // Directory not empty, stop cleanup
                }
            }
            catch
            {
                break;
            }
        }
    }
}
