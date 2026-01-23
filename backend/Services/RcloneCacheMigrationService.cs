using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Service for migrating rclone VFS cache files between instances when shard routing is configured.
/// Moves cached files from one instance's cache to another based on shard prefix assignment.
///
/// With mergerfs, all instances use the same legacy /.ids path structure:
///   {VfsCachePath}/vfs/{remoteName}/.ids/{p1}/{p2}/{p3}/{p4}/{p5}/{guid}
///
/// This migration moves files between instances' caches based on which instance
/// should handle each file according to shard configuration.
/// </summary>
public class RcloneCacheMigrationService(DavDatabaseContext db)
{
    /// <summary>
    /// Check if all enabled rclone instances are available and running.
    /// </summary>
    public async Task<InstanceAvailabilityResult> CheckAllInstancesAvailableAsync(CancellationToken ct = default)
    {
        var result = new InstanceAvailabilityResult();

        var instances = await db.RcloneInstances
            .Where(x => x.IsEnabled)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            result.AllAvailable = true;
            result.Message = "No enabled instances configured";
            return result;
        }

        Log.Information("[CacheMigration] Checking availability of {Count} rclone instances...", instances.Count);

        foreach (var instance in instances)
        {
            try
            {
                using var client = new RcloneClient(instance);
                var testResult = await client.TestConnectionAsync(ct).ConfigureAwait(false);

                if (testResult.Success)
                {
                    result.AvailableInstances.Add(instance.Id);
                    Log.Information("[CacheMigration] Instance {Name} is available (version: {Version})",
                        instance.Name, testResult.Version);
                }
                else
                {
                    result.UnavailableInstances[instance.Id] = testResult.Message;
                    Log.Warning("[CacheMigration] Instance {Name} is NOT available: {Error}",
                        instance.Name, testResult.Message);
                }
            }
            catch (Exception ex)
            {
                result.UnavailableInstances[instance.Id] = ex.Message;
                Log.Warning(ex, "[CacheMigration] Failed to check instance {Name}", instance.Name);
            }
        }

        result.AllAvailable = result.UnavailableInstances.Count == 0;
        result.Message = result.AllAvailable
            ? $"All {instances.Count} instances are available"
            : $"{result.UnavailableInstances.Count} of {instances.Count} instances are unavailable";

        return result;
    }

    /// <summary>
    /// Build a mapping of prefix character to the instance that should handle it.
    /// </summary>
    public async Task<Dictionary<char, RcloneInstance>> BuildPrefixToInstanceMapAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<char, RcloneInstance>();

        var instances = await db.RcloneInstances
            .Where(x => x.IsEnabled && x.IsShardEnabled && !string.IsNullOrEmpty(x.ShardPrefixes))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var instance in instances)
        {
            var prefixes = ShardRoutingUtil.ParseShardPrefixes(instance.ShardPrefixes!);
            foreach (var prefix in prefixes)
            {
                if (!map.ContainsKey(prefix))
                {
                    map[prefix] = instance;
                }
                else
                {
                    Log.Warning("[CacheMigration] Prefix '{Prefix}' is assigned to multiple instances: {Instance1} and {Instance2}",
                        prefix, map[prefix].Name, instance.Name);
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Preview what files would be migrated without actually moving them.
    /// Returns a summary of files per source/destination instance pair.
    /// </summary>
    public async Task<CacheMigrationPreview> PreviewMigrationAsync(CancellationToken ct = default)
    {
        var preview = new CacheMigrationPreview();

        var instances = await db.RcloneInstances
            .Where(x => x.IsEnabled && !string.IsNullOrEmpty(x.VfsCachePath))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (instances.Count < 2)
        {
            preview.Message = "Need at least 2 instances with VFS cache paths configured for migration";
            return preview;
        }

        var prefixMap = await BuildPrefixToInstanceMapAsync(ct).ConfigureAwait(false);
        if (prefixMap.Count == 0)
        {
            preview.Message = "No shard prefixes configured - enable sharding on instances first";
            return preview;
        }

        Log.Information("[CacheMigration] Previewing migration across {Count} instances...", instances.Count);

        foreach (var sourceInstance in instances)
        {
            var idsPath = GetIdsPath(sourceInstance);
            if (!Directory.Exists(idsPath))
            {
                Log.Information("[CacheMigration] No cache directory for {Name}: {Path}", sourceInstance.Name, idsPath);
                continue;
            }

            // Scan first-level prefix directories
            foreach (var prefixDir in Directory.GetDirectories(idsPath))
            {
                var prefixChar = Path.GetFileName(prefixDir).ToLower()[0];

                // Check if this prefix belongs to a different instance
                if (!prefixMap.TryGetValue(prefixChar, out var targetInstance))
                    continue; // Prefix not assigned to any shard

                if (targetInstance.Id == sourceInstance.Id)
                    continue; // Already in the right place

                // Count files and size that would be moved
                var (fileCount, totalSize) = CountFilesRecursive(prefixDir, ct);

                if (fileCount > 0)
                {
                    var key = (sourceInstance.Id, targetInstance.Id);
                    if (!preview.Transfers.ContainsKey(key))
                    {
                        preview.Transfers[key] = new TransferSummary
                        {
                            SourceInstanceName = sourceInstance.Name,
                            TargetInstanceName = targetInstance.Name
                        };
                    }

                    preview.Transfers[key].FileCount += fileCount;
                    preview.Transfers[key].TotalBytes += totalSize;
                    preview.Transfers[key].Prefixes.Add(prefixChar);
                }
            }
        }

        preview.TotalFiles = preview.Transfers.Values.Sum(x => x.FileCount);
        preview.TotalBytes = preview.Transfers.Values.Sum(x => x.TotalBytes);
        preview.Message = preview.TotalFiles > 0
            ? $"Found {preview.TotalFiles} files ({FormatBytes(preview.TotalBytes)}) to migrate across {preview.Transfers.Count} instance pairs"
            : "No files need migration - all caches are correctly sharded";

        return preview;
    }

    /// <summary>
    /// Migrate cache files between all instances based on shard configuration.
    /// Files are moved from source instance caches to target instance caches.
    /// </summary>
    public async Task<CacheMigrationResult> MigrateAllAsync(CancellationToken ct = default)
    {
        var result = new CacheMigrationResult();

        // Pre-flight: ensure all instances are available
        var availabilityCheck = await CheckAllInstancesAvailableAsync(ct).ConfigureAwait(false);
        if (!availabilityCheck.AllAvailable)
        {
            result.Success = false;
            result.Error = $"Cannot migrate: {availabilityCheck.Message}. All rclone instances must be running.";
            Log.Warning("[CacheMigration] Migration aborted: {Error}", result.Error);
            MigrationStatus.Fail(result.Error);
            return result;
        }

        var instances = await db.RcloneInstances
            .Where(x => x.IsEnabled && !string.IsNullOrEmpty(x.VfsCachePath))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (instances.Count < 2)
        {
            result.Success = false;
            result.Error = "Need at least 2 instances with VFS cache paths configured for migration";
            MigrationStatus.Fail(result.Error);
            return result;
        }

        var prefixMap = await BuildPrefixToInstanceMapAsync(ct).ConfigureAwait(false);
        if (prefixMap.Count == 0)
        {
            result.Success = false;
            result.Error = "No shard prefixes configured - enable sharding on instances first";
            MigrationStatus.Fail(result.Error);
            return result;
        }

        // Build instance lookup by ID
        var instancesById = instances.ToDictionary(x => x.Id);

        Log.Information("[CacheMigration] Starting cross-instance cache migration...");
        Log.Information("[CacheMigration] Prefix mapping: {Mapping}",
            string.Join(", ", prefixMap.GroupBy(x => x.Value.Name).Select(g => $"{g.Key}=[{string.Join("", g.Select(x => x.Key))}]")));

        // Get estimate for progress tracking
        var preview = await PreviewMigrationAsync(ct).ConfigureAwait(false);
        MigrationStatus.Start(preview.TotalFiles, preview.TotalBytes);

        try
        {
            foreach (var sourceInstance in instances)
            {
                var sourceIdsPath = GetIdsPath(sourceInstance);
                if (!Directory.Exists(sourceIdsPath))
                {
                    Log.Debug("[CacheMigration] No cache directory for {Name}: {Path}", sourceInstance.Name, sourceIdsPath);
                    continue;
                }

                Log.Information("[CacheMigration] Scanning cache for instance {Name}...", sourceInstance.Name);
                MigrationStatus.UpdatePhase($"Scanning {sourceInstance.Name}");

                // Scan first-level prefix directories
                foreach (var prefixDir in Directory.GetDirectories(sourceIdsPath))
                {
                    ct.ThrowIfCancellationRequested();

                    var prefixChar = Path.GetFileName(prefixDir).ToLower()[0];

                    // Check if this prefix belongs to a different instance
                    if (!prefixMap.TryGetValue(prefixChar, out var targetInstance))
                        continue;

                    if (targetInstance.Id == sourceInstance.Id)
                        continue; // Already in the right place

                    if (!instancesById.ContainsKey(targetInstance.Id))
                    {
                        Log.Warning("[CacheMigration] Target instance {Name} not in active set, skipping prefix {Prefix}",
                            targetInstance.Name, prefixChar);
                        continue;
                    }

                    var targetIdsPath = GetIdsPath(targetInstance);
                    var targetPrefixDir = Path.Combine(targetIdsPath, prefixChar.ToString());

                    Log.Information("[CacheMigration] Moving prefix '{Prefix}' from {Source} to {Target}",
                        prefixChar, sourceInstance.Name, targetInstance.Name);

                    MigrationStatus.UpdatePhase($"Moving prefix '{prefixChar}'");
                    MigrationStatus.UpdateInstances(sourceInstance.Name, targetInstance.Name);

                    // Move the entire prefix directory tree
                    var movedCount = await MoveDirectoryContentsAsync(
                        prefixDir,
                        targetPrefixDir,
                        result,
                        ct
                    ).ConfigureAwait(false);

                    Log.Information("[CacheMigration] Moved {Count} files for prefix '{Prefix}'",
                        movedCount, prefixChar);
                }
            }

            result.Success = true;
            result.Message = $"Migrated {result.FilesMoved} files ({FormatBytes(result.BytesMoved)})";

            if (result.Errors.Count > 0)
            {
                result.Message += $" with {result.Errors.Count} errors";
            }

            Log.Information("[CacheMigration] Migration complete: {FilesMoved} files, {BytesMoved}",
                result.FilesMoved, FormatBytes(result.BytesMoved));
            MigrationStatus.Complete();
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "Migration was cancelled";
            Log.Warning("[CacheMigration] Migration cancelled after moving {Files} files", result.FilesMoved);
            MigrationStatus.Fail("Cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CacheMigration] Migration failed");
            result.Success = false;
            result.Error = ex.Message;
            MigrationStatus.Fail(ex.Message);
        }

        return result;
    }

    /// <summary>
    /// Migrate cache files for a specific source instance only.
    /// Moves files that belong to other instances out of this instance's cache.
    /// </summary>
    public async Task<CacheMigrationResult> MigrateFromInstanceAsync(
        Guid sourceInstanceId,
        CancellationToken ct = default)
    {
        var result = new CacheMigrationResult();

        // Pre-flight: ensure all instances are available
        var availabilityCheck = await CheckAllInstancesAvailableAsync(ct).ConfigureAwait(false);
        if (!availabilityCheck.AllAvailable)
        {
            result.Success = false;
            result.Error = $"Cannot migrate: {availabilityCheck.Message}. All rclone instances must be running.";
            return result;
        }

        var sourceInstance = await db.RcloneInstances
            .FirstOrDefaultAsync(x => x.Id == sourceInstanceId, ct)
            .ConfigureAwait(false);

        if (sourceInstance == null)
        {
            result.Success = false;
            result.Error = "Source instance not found";
            return result;
        }

        if (string.IsNullOrEmpty(sourceInstance.VfsCachePath))
        {
            result.Success = false;
            result.Error = "VFS cache path not configured for source instance";
            return result;
        }

        var prefixMap = await BuildPrefixToInstanceMapAsync(ct).ConfigureAwait(false);
        if (prefixMap.Count == 0)
        {
            result.Success = false;
            result.Error = "No shard prefixes configured";
            return result;
        }

        var sourceIdsPath = GetIdsPath(sourceInstance);
        if (!Directory.Exists(sourceIdsPath))
        {
            result.Success = true;
            result.Message = "No cache directory exists for this instance";
            return result;
        }

        Log.Information("[CacheMigration] Migrating files OUT of instance {Name}...", sourceInstance.Name);

        // Count files for progress estimation
        var estimatedFiles = 0;
        long estimatedBytes = 0;
        foreach (var prefixDir in Directory.GetDirectories(sourceIdsPath))
        {
            var prefixChar = Path.GetFileName(prefixDir).ToLower()[0];
            if (prefixMap.TryGetValue(prefixChar, out var target) && target.Id != sourceInstance.Id)
            {
                var (count, bytes) = CountFilesRecursive(prefixDir, ct);
                estimatedFiles += count;
                estimatedBytes += bytes;
            }
        }
        MigrationStatus.Start(estimatedFiles, estimatedBytes);

        try
        {
            foreach (var prefixDir in Directory.GetDirectories(sourceIdsPath))
            {
                ct.ThrowIfCancellationRequested();

                var prefixChar = Path.GetFileName(prefixDir).ToLower()[0];

                if (!prefixMap.TryGetValue(prefixChar, out var targetInstance))
                    continue;

                if (targetInstance.Id == sourceInstance.Id)
                    continue;

                if (string.IsNullOrEmpty(targetInstance.VfsCachePath))
                {
                    Log.Warning("[CacheMigration] Target instance {Name} has no VFS cache path, skipping prefix {Prefix}",
                        targetInstance.Name, prefixChar);
                    result.Errors.Add($"Target {targetInstance.Name} has no VFS cache path");
                    continue;
                }

                var targetIdsPath = GetIdsPath(targetInstance);
                var targetPrefixDir = Path.Combine(targetIdsPath, prefixChar.ToString());

                Log.Information("[CacheMigration] Moving prefix '{Prefix}' to {Target}", prefixChar, targetInstance.Name);
                MigrationStatus.UpdatePhase($"Moving prefix '{prefixChar}'");
                MigrationStatus.UpdateInstances(sourceInstance.Name, targetInstance.Name);

                var movedCount = await MoveDirectoryContentsAsync(prefixDir, targetPrefixDir, result, ct)
                    .ConfigureAwait(false);

                Log.Information("[CacheMigration] Moved {Count} files for prefix '{Prefix}'", movedCount, prefixChar);
            }

            result.Success = true;
            result.Message = $"Migrated {result.FilesMoved} files ({FormatBytes(result.BytesMoved)}) from {sourceInstance.Name}";
            MigrationStatus.Complete();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CacheMigration] Migration from {Name} failed", sourceInstance.Name);
            result.Success = false;
            result.Error = ex.Message;
            MigrationStatus.Fail(ex.Message);
        }

        return result;
    }

    /// <summary>
    /// Get the .ids cache path for an instance.
    /// Structure: {VfsCachePath}/vfs/{remoteName}/.ids
    /// </summary>
    private static string GetIdsPath(RcloneInstance instance)
    {
        var remoteName = instance.RemoteName.TrimEnd(':');
        return Path.Combine(instance.VfsCachePath!, "vfs", remoteName, ".ids");
    }

    private static (int fileCount, long totalSize) CountFilesRecursive(string directory, CancellationToken ct)
    {
        var fileCount = 0;
        long totalSize = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                fileCount++;
                try
                {
                    totalSize += new FileInfo(file).Length;
                }
                catch
                {
                    // Ignore inaccessible files
                }
            }
        }
        catch (Exception ex)
        {
            Log.Information(ex, "[CacheMigration] Error counting files in {Dir}", directory);
        }

        return (fileCount, totalSize);
    }

    private async Task<int> MoveDirectoryContentsAsync(
        string sourceDir,
        string destDir,
        CacheMigrationResult result,
        CancellationToken ct)
    {
        var movedCount = 0;

        if (!Directory.Exists(sourceDir))
            return 0;

        // Create destination directory
        Directory.CreateDirectory(destDir);

        // Move all files
        foreach (var sourceFile in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(sourceFile);
            var destFile = Path.Combine(destDir, fileName);

            try
            {
                var fileInfo = new FileInfo(sourceFile);
                var fileSize = fileInfo.Length;

                // Remove destination if it exists
                if (File.Exists(destFile))
                {
                    Log.Information("[CacheMigration] Destination exists, removing: {Path}", destFile);
                    File.Delete(destFile);
                }

                File.Move(sourceFile, destFile);

                result.FilesMoved++;
                result.BytesMoved += fileSize;
                movedCount++;

                MigrationStatus.UpdateFile(fileName, fileSize);
                Log.Information("[CacheMigration] Moved: {Source} -> {Dest} ({Size})",
                    sourceFile, destFile, FormatBytes(fileSize));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[CacheMigration] Failed to move file: {Path}", sourceFile);
                var errorMsg = $"Failed to move {fileName}: {ex.Message}";
                result.Errors.Add(errorMsg);
                MigrationStatus.AddError(errorMsg);
            }
        }

        // Recursively process subdirectories
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var subDirName = Path.GetFileName(subDir);
            var destSubDir = Path.Combine(destDir, subDirName);

            movedCount += await MoveDirectoryContentsAsync(subDir, destSubDir, result, ct)
                .ConfigureAwait(false);
        }

        // Remove empty source directory
        try
        {
            if (Directory.Exists(sourceDir) && !Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                Directory.Delete(sourceDir);
                Log.Information("[CacheMigration] Removed empty directory: {Path}", sourceDir);
            }
        }
        catch (Exception ex)
        {
            Log.Information(ex, "[CacheMigration] Could not remove directory: {Path}", sourceDir);
        }

        return movedCount;
    }

    /// <summary>
    /// Get recommended shard prefixes based on total number of shards and this shard's index.
    /// </summary>
    public static string GetRecommendedPrefixes(int shardIndex, int totalShards)
    {
        return ShardRoutingUtil.GetDefaultPrefixesForShard(shardIndex, totalShards);
    }

    /// <summary>
    /// Calculate the recommended shard configuration for all instances.
    /// </summary>
    public async Task<Dictionary<Guid, ShardRecommendation>> GetShardRecommendationsAsync(
        CancellationToken ct = default)
    {
        var instances = await db.RcloneInstances
            .AsNoTracking()
            .OrderBy(x => x.ShardIndex ?? 0)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new Dictionary<Guid, ShardRecommendation>();
        var totalShards = instances.Count;

        if (totalShards == 0)
            return result;

        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            var recommendedPrefixes = GetRecommendedPrefixes(i, totalShards);

            result[instance.Id] = new ShardRecommendation
            {
                ShardIndex = i,
                RecommendedPrefixes = recommendedPrefixes,
                TotalShards = totalShards,
                CurrentPrefixes = instance.ShardPrefixes,
                IsShardEnabled = instance.IsShardEnabled
            };
        }

        return result;
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}

public class CacheMigrationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public int FilesMoved { get; set; }
    public long BytesMoved { get; set; }
    public List<string> Errors { get; set; } = [];
}

public class CacheMigrationPreview
{
    public string? Message { get; set; }
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public Dictionary<(Guid SourceId, Guid TargetId), TransferSummary> Transfers { get; set; } = [];
}

public class TransferSummary
{
    public string SourceInstanceName { get; set; } = string.Empty;
    public string TargetInstanceName { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public HashSet<char> Prefixes { get; set; } = [];
}

public class ShardRecommendation
{
    public int ShardIndex { get; set; }
    public string RecommendedPrefixes { get; set; } = string.Empty;
    public int TotalShards { get; set; }
    public string? CurrentPrefixes { get; set; }
    public bool IsShardEnabled { get; set; }
}

public class InstanceAvailabilityResult
{
    public bool AllAvailable { get; set; }
    public string? Message { get; set; }
    public List<Guid> AvailableInstances { get; set; } = [];
    public Dictionary<Guid, string> UnavailableInstances { get; set; } = [];
}

/// <summary>
/// Tracks current migration progress for status polling.
/// </summary>
public static class MigrationStatus
{
    private static readonly object _lock = new();

    public static bool IsRunning { get; private set; }
    public static string? CurrentPhase { get; private set; }
    public static string? CurrentFile { get; private set; }
    public static int FilesMoved { get; private set; }
    public static long BytesMoved { get; private set; }
    public static int TotalFilesEstimate { get; private set; }
    public static long TotalBytesEstimate { get; private set; }
    public static string? SourceInstance { get; private set; }
    public static string? TargetInstance { get; private set; }
    public static DateTimeOffset? StartedAt { get; private set; }
    public static List<string> RecentErrors { get; private set; } = [];

    public static void Start(int totalFiles, long totalBytes)
    {
        lock (_lock)
        {
            IsRunning = true;
            CurrentPhase = "Starting migration";
            CurrentFile = null;
            FilesMoved = 0;
            BytesMoved = 0;
            TotalFilesEstimate = totalFiles;
            TotalBytesEstimate = totalBytes;
            SourceInstance = null;
            TargetInstance = null;
            StartedAt = DateTimeOffset.UtcNow;
            RecentErrors = [];
        }
    }

    public static void UpdatePhase(string phase)
    {
        lock (_lock) { CurrentPhase = phase; }
    }

    public static void UpdateInstances(string source, string target)
    {
        lock (_lock)
        {
            SourceInstance = source;
            TargetInstance = target;
        }
    }

    public static void UpdateFile(string filename, long fileSize)
    {
        lock (_lock)
        {
            CurrentFile = filename;
            FilesMoved++;
            BytesMoved += fileSize;
        }
    }

    public static void AddError(string error)
    {
        lock (_lock)
        {
            RecentErrors.Add(error);
            if (RecentErrors.Count > 10)
                RecentErrors.RemoveAt(0);
        }
    }

    public static void Complete()
    {
        lock (_lock)
        {
            IsRunning = false;
            CurrentPhase = "Completed";
            CurrentFile = null;
        }
    }

    public static void Fail(string error)
    {
        lock (_lock)
        {
            IsRunning = false;
            CurrentPhase = $"Failed: {error}";
        }
    }

    public static object GetStatus()
    {
        lock (_lock)
        {
            return new
            {
                isRunning = IsRunning,
                currentPhase = CurrentPhase,
                currentFile = CurrentFile,
                filesMoved = FilesMoved,
                bytesMoved = BytesMoved,
                totalFilesEstimate = TotalFilesEstimate,
                totalBytesEstimate = TotalBytesEstimate,
                sourceInstance = SourceInstance,
                targetInstance = TargetInstance,
                startedAt = StartedAt,
                recentErrors = RecentErrors.ToList(),
                progressPercent = TotalBytesEstimate > 0 ? (int)(BytesMoved * 100 / TotalBytesEstimate) : 0
            };
        }
    }
}
