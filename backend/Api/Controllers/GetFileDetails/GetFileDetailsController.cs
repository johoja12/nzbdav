using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Clients;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetFileDetails;

[ApiController]
[Route("api/file-details/{davItemId}")]
public class GetFileDetailsController(
    DavDatabaseClient dbClient,
    NzbProviderAffinityService affinityService,
    ConfigManager configManager,
    DavDatabaseContext db) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var davItemId = RouteData.Values["davItemId"]?.ToString();
        if (string.IsNullOrEmpty(davItemId) || !Guid.TryParse(davItemId, out var itemGuid))
        {
            return BadRequest(new { error = "Invalid DavItemId" });
        }

        var davItem = await dbClient.Ctx.Items
            .FirstOrDefaultAsync(x => x.Id == itemGuid)
            .ConfigureAwait(false);

        if (davItem == null)
        {
            return NotFound(new { error = "File not found" });
        }

        // Get the associated NZB file if it exists
        var nzbFile = await dbClient.Ctx.NzbFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == itemGuid)
            .ConfigureAwait(false);

        // JobName in NzbProviderStats is the full file path (davItem.Path)
        // JobName in QueueItems/HistoryItems is the directory name
        // We need to check both because Queue downloads key by JobName, while Streaming keys by Path.
        var providerStatsJobName = davItem.Path;
        var queueJobName = Path.GetFileName(Path.GetDirectoryName(davItem.Path));

        Serilog.Log.Debug("[GetFileDetails] Fetching stats for Path='{Path}' and Job='{Job}'", providerStatsJobName, queueJobName);

        // Get provider stats from database (check both keys)
        var dbProviderStats = await dbClient.Ctx.NzbProviderStats
            .AsNoTracking()
            .Where(x => x.JobName == providerStatsJobName || (queueJobName != null && x.JobName == queueJobName))
            .ToListAsync()
            .ConfigureAwait(false);

        // Get live stats from affinity service (check both keys)
        var liveProviderStats = affinityService.GetJobStats(providerStatsJobName);
        if (queueJobName != null)
        {
            var jobStats = affinityService.GetJobStats(queueJobName);
            // Serilog.Log.Information("[GetFileDetails] Live Stats - Path: {PathCount}, Job: {JobCount}", liveProviderStats.Count, jobStats.Count);
            foreach (var kvp in jobStats)
            {
                if (!liveProviderStats.ContainsKey(kvp.Key))
                {
                    liveProviderStats[kvp.Key] = kvp.Value;
                }
                else
                {
                    // If we have stats for both, merge them (summing up counts/bytes)
                    // Note: This is a simplification. Ideally we'd want weighted averages for speeds.
                    // For now, let's trust the Path-specific stats (Streaming) over Job stats if both exist?
                    // Actually, usually only one is active/populated significantly.
                    // Let's just accumulate counts to show total activity.
                    var existing = liveProviderStats[kvp.Key];
                    var incoming = kvp.Value;
                    existing.SuccessfulSegments += incoming.SuccessfulSegments;
                    existing.FailedSegments += incoming.FailedSegments;
                    existing.TotalBytes += incoming.TotalBytes;
                    existing.TotalTimeMs += incoming.TotalTimeMs;
                    // Keep the most recent LastUsed
                    if (incoming.LastUsed > existing.LastUsed) existing.LastUsed = incoming.LastUsed;
                    // Speed: Use the higher one? Or average? Let's use the one from the most recent activity.
                    if (incoming.LastUsed > existing.LastUsed) existing.RecentAverageSpeedBps = incoming.RecentAverageSpeedBps;
                }
            }
        }

        // Merge database and live stats (live stats take precedence)
        var mergedStats = new Dictionary<int, NzbProviderStats>();
        
        // First merge DB stats (accumulating if multiple entries for same provider exist due to different JobNames)
        foreach (var stat in dbProviderStats) 
        {
            if (mergedStats.TryGetValue(stat.ProviderIndex, out var existing))
            {
                existing.SuccessfulSegments += stat.SuccessfulSegments;
                existing.FailedSegments += stat.FailedSegments;
                existing.TotalBytes += stat.TotalBytes;
                existing.TotalTimeMs += stat.TotalTimeMs;
                if (stat.LastUsed > existing.LastUsed) existing.LastUsed = stat.LastUsed;
            }
            else
            {
                mergedStats[stat.ProviderIndex] = stat;
            }
        }
        
        // Then overwrite/update with live stats
        foreach (var kvp in liveProviderStats) mergedStats[kvp.Key] = kvp.Value;

        Serilog.Log.Debug("[GetFileDetails] Merged Stats Summary: {Count} providers. TotalBytes: {TotalBytes}",
            mergedStats.Count, mergedStats.Values.Sum(s => s.TotalBytes));

        // Get provider configuration to map provider index to host
        var usenetConfig = configManager.GetUsenetProviderConfig();
        var providerHosts = usenetConfig.Providers
            .Select((p, index) => new { Index = index, Host = p.Host })
            .ToDictionary(x => x.Index, x => x.Host);

        // Get missing article count from summaries (events aren't stored to save space)
        // There could be multiple summaries for the same DavItemId (different filenames/paths)
        var missingArticleSummaries = await dbClient.Ctx.MissingArticleSummaries
            .AsNoTracking()
            .Where(x => x.DavItemId == itemGuid)
            .ToListAsync()
            .ConfigureAwait(false);

        // Only show count when HasBlockingMissingArticles is true
        // This means the same segment was confirmed missing on ALL providers
        // (ProviderErrorService sets both HasBlockingMissingArticles and IsCorrupted together)
        var missingArticleCount = 0;
        var hasBlocking = missingArticleSummaries.Any(s => s.HasBlockingMissingArticles);

        if (hasBlocking)
        {
            var totalEvents = missingArticleSummaries.Where(s => s.HasBlockingMissingArticles).Sum(s => s.TotalEvents);
            var providerCount = usenetConfig.Providers.Count;
            // Estimate unique blocking segments: TotalEvents / providerCount (each blocking segment generates one error per provider)
            missingArticleCount = providerCount > 0
                ? (int)Math.Ceiling((double)totalEvents / providerCount)
                : totalEvents;
        }

        // Get latest health check result
        var latestHealthCheck = await dbClient.Ctx.HealthCheckResults
            .AsNoTracking()
            .Where(x => x.DavItemId == itemGuid)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        // Get mapped path from LocalLinks
        var mappedLink = await dbClient.Ctx.LocalLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.DavItemId == itemGuid)
            .ConfigureAwait(false);

        // Generate download URL with token
        // Normalize path to match GetWebdavItemRequest normalization (strip leading slash)
        var normalizedPath = davItem.Path.TrimStart('/');
        var apiKey = EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
        var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(apiKey, normalizedPath);
        var downloadUrl = $"/view{davItem.Path}?downloadKey={downloadKey}";

        // Check if NZB download is available
        string? nzbDownloadUrl = null;
        HistoryItem? historyItem = null;

        var queueItem = await dbClient.Ctx.QueueItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.JobName == queueJobName)
            .ConfigureAwait(false);

        // Always try to fetch HistoryItem for ArrResolutionInfo (even if in queue)
        historyItem = await dbClient.Ctx.HistoryItems
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.JobName == queueJobName)
            .ConfigureAwait(false);

        if (queueItem != null)
        {
            // If in queue, check QueueNzbContents
            var nzbExists = await dbClient.Ctx.QueueNzbContents
                .AsNoTracking()
                .AnyAsync(x => x.Id == queueItem.Id)
                .ConfigureAwait(false);

            if (nzbExists) nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
        }
        else if (historyItem != null && !string.IsNullOrEmpty(historyItem.NzbContents))
        {
            nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
        }

        // Fallback: If NZB not in Queue/History, check if we have any file metadata (Nzb, Rar, or Multipart) to generate from
        if (nzbDownloadUrl == null)
        {
            if (nzbFile != null)
            {
                nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
            }
            else
            {
                // Check Rar/Multipart
                var isRar = await dbClient.Ctx.RarFiles.AsNoTracking().AnyAsync(x => x.Id == itemGuid).ConfigureAwait(false);
                if (isRar)
                {
                    nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
                }
                else
                {
                    var isMultipart = await dbClient.Ctx.MultipartFiles.AsNoTracking().AnyAsync(x => x.Id == itemGuid).ConfigureAwait(false);
                    if (isMultipart)
                    {
                        nzbDownloadUrl = $"/api/download-nzb/{davItem.Id}";
                    }
                }
            }
        }

        var response = new GetFileDetailsResponse
        {
            DavItemId = davItem.Id.ToString(),
            Name = davItem.Name,
            Path = davItem.Path,
            WebdavPath = davItem.Path,
            IdsPath = DavItem.IdsFolder.Path + "/" + string.Join("/", davItem.Id.ToString().Take(5)) + "/" + davItem.Id,
            MappedPath = mappedLink?.LinkPath,
            JobName = queueJobName,
            DownloadUrl = downloadUrl,
            NzbDownloadUrl = nzbDownloadUrl,
            FileSize = davItem.FileSize ?? 0,
            ItemType = davItem.Type,
            ItemTypeString = davItem.Type.ToString(),
            CreatedAt = davItem.ReleaseDate,
            LastHealthCheck = davItem.LastHealthCheck,
            NextHealthCheck = davItem.NextHealthCheck,
            MediaInfo = davItem.MediaInfo,
            IsCorrupted = davItem.IsCorrupted,
            CorruptionReason = davItem.CorruptionReason,
            MissingArticleCount = missingArticleCount,
            ProviderStats = mergedStats.Values.OrderBy(ps => ps.ProviderIndex).Select(ps => new GetFileDetailsResponse.ProviderStatistic
            {
                ProviderIndex = ps.ProviderIndex,
                ProviderHost = providerHosts.GetValueOrDefault(ps.ProviderIndex, $"Provider {ps.ProviderIndex}"),
                SuccessfulSegments = ps.SuccessfulSegments,
                FailedSegments = ps.FailedSegments,
                TimeoutErrors = ps.TimeoutErrors,
                MissingArticleErrors = ps.MissingArticleErrors,
                TotalBytes = ps.TotalBytes,
                TotalTimeMs = ps.TotalTimeMs,
                LastUsed = ps.LastUsed,
                AverageSpeedBps = ps.RecentAverageSpeedBps > 0 ? ps.RecentAverageSpeedBps : ps.AverageSpeedBps,
                SuccessRate = ps.SuccessRate
            }).ToList(),
            LatestHealthCheckResult = latestHealthCheck != null ? new GetFileDetailsResponse.HealthCheckInfo
            {
                Result = latestHealthCheck.Result,
                RepairStatus = latestHealthCheck.RepairStatus,
                Message = latestHealthCheck.Message,
                CreatedAt = latestHealthCheck.CreatedAt
            } : null
        };

        // Parse Arr resolution info if available (when queue item was auto-resolved as "stuck")
        if (historyItem != null && !string.IsNullOrEmpty(historyItem.ArrResolutionInfo))
        {
            try
            {
                var resolutionJson = JsonDocument.Parse(historyItem.ArrResolutionInfo);
                var root = resolutionJson.RootElement;

                response.ArrResolution = new GetFileDetailsResponse.ArrResolutionDetails
                {
                    Action = root.TryGetProperty("action", out var actionProp) ? actionProp.GetString() ?? "" : "",
                    TriggeredBy = root.TryGetProperty("triggeredBy", out var triggeredProp) && triggeredProp.ValueKind == JsonValueKind.Array
                        ? triggeredProp.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                        : new List<string>(),
                    StatusMessages = root.TryGetProperty("statusMessages", out var statusProp) && statusProp.ValueKind == JsonValueKind.Array
                        ? statusProp.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                        : new List<string>(),
                    ResolvedAt = root.TryGetProperty("resolvedAt", out var resolvedProp) && DateTimeOffset.TryParse(resolvedProp.GetString(), out var resolvedAt)
                        ? resolvedAt
                        : null,
                    ArrHost = root.TryGetProperty("host", out var hostProp) ? hostProp.GetString() : null
                };
            }
            catch (JsonException ex)
            {
                Serilog.Log.Warning("[GetFileDetails] Failed to parse ArrResolutionInfo for {JobName}: {Error}", queueJobName, ex.Message);
            }
        }

        // Get segment statistics if available
        if (nzbFile != null)
        {
            response.TotalSegments = nzbFile.SegmentIds?.Length ?? 0;

            // Calculate min/max/avg from segment sizes if available
            var segmentSizes = nzbFile.GetSegmentSizes();
            if (segmentSizes != null && segmentSizes.Length > 0)
            {
                response.MinSegmentSize = segmentSizes.Min();
                response.MaxSegmentSize = segmentSizes.Max();
                response.AvgSegmentSize = (long)segmentSizes.Average();
            }
        }

        // Query Rclone instances for cache status and persist to DB
        response.CacheStatus = await GetRcloneCacheStatusAsync(davItem, response.FileSize).ConfigureAwait(false);

        // Update cache state in database
        var bestCache = response.CacheStatus
            .Where(c => c.CachePercentage > 0)
            .OrderByDescending(c => c.CachePercentage)
            .FirstOrDefault();

        davItem.LastCacheCheck = DateTimeOffset.UtcNow;
        if (bestCache != null)
        {
            davItem.IsCached = bestCache.IsFullyCached;
            davItem.CachedBytes = bestCache.CachedBytes;
            davItem.CachePercentage = bestCache.CachePercentage;
            davItem.CachedInInstance = bestCache.InstanceName;
        }
        else
        {
            davItem.IsCached = false;
            davItem.CachedBytes = 0;
            davItem.CachePercentage = 0;
            davItem.CachedInInstance = null;
        }

        await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);

        return Ok(response);
    }

    private async Task<List<GetFileDetailsResponse.RcloneCacheStatus>> GetRcloneCacheStatusAsync(DavItem davItem, long expectedSize)
    {
        var cacheStatuses = new List<GetFileDetailsResponse.RcloneCacheStatus>();

        try
        {
            // Get enabled Rclone instances
            var instances = await db.RcloneInstances
                .AsNoTracking()
                .Where(i => i.IsEnabled)
                .ToListAsync()
                .ConfigureAwait(false);

            if (instances.Count == 0)
                return cacheStatuses;

            // Build paths to check:
            // 1. Content path: /content/sonarr/JobName/FileName.mkv (davItem.Path)
            // 2. IDs path: .ids/9/6/1/b/0/961b00d8-556d-4072-9e22-bda197ee68fc
            var idStr = davItem.Id.ToString();
            var idsPath = ".ids/" + string.Join("/", idStr.Take(5).Select(c => c.ToString())) + "/" + idStr;
            var contentPath = davItem.Path.TrimStart('/');

            foreach (var instance in instances)
            {
                try
                {
                    // First, check VFS cache directory directly if configured (most reliable for fully cached files)
                    if (!string.IsNullOrEmpty(instance.VfsCachePath))
                    {
                        var cacheResult = CheckVfsCacheDirectory(instance, idsPath, contentPath, expectedSize);
                        if (cacheResult != null)
                        {
                            cacheStatuses.Add(new GetFileDetailsResponse.RcloneCacheStatus
                            {
                                InstanceName = instance.Name,
                                IsFullyCached = cacheResult.Value.isFullyCached,
                                CachedBytes = cacheResult.Value.cachedBytes,
                                CachePercentage = cacheResult.Value.percentage,
                                Status = cacheResult.Value.isFullyCached ? "fully_cached" : "partial_cache",
                                CachedPath = cacheResult.Value.cachedPath
                            });
                            continue; // Found in cache dir, skip API check
                        }
                    }

                    // Fall back to vfs/transfers API (shows active/recent files)
                    using var client = new RcloneClient(instance);
                    var transfers = await client.GetVfsTransfersAsync().ConfigureAwait(false);

                    if (transfers?.Transfers == null)
                    {
                        cacheStatuses.Add(new GetFileDetailsResponse.RcloneCacheStatus
                        {
                            InstanceName = instance.Name,
                            IsFullyCached = false,
                            CachedBytes = 0,
                            CachePercentage = 0,
                            Status = "not_found"
                        });
                        continue;
                    }

                    // Look for the file in transfers - check both content path and .ids path
                    var transfer = transfers.Transfers.FirstOrDefault(t =>
                    {
                        var name = t.Name;
                        // Check content path (e.g., /content/sonarr/JobName/File.mkv)
                        if (name.Contains(contentPath) || name.EndsWith(davItem.Name))
                            return true;
                        // Check .ids path (e.g., /.ids/9/6/1/b/0/uuid)
                        if (name.Contains(idsPath) || name.EndsWith(idStr))
                            return true;
                        return false;
                    });

                    if (transfer != null)
                    {
                        cacheStatuses.Add(new GetFileDetailsResponse.RcloneCacheStatus
                        {
                            InstanceName = instance.Name,
                            IsFullyCached = transfer.CachePercentage >= 100,
                            CachedBytes = transfer.CacheBytes,
                            CachePercentage = transfer.CachePercentage,
                            Status = transfer.CacheStatus
                        });
                    }
                    else
                    {
                        cacheStatuses.Add(new GetFileDetailsResponse.RcloneCacheStatus
                        {
                            InstanceName = instance.Name,
                            IsFullyCached = false,
                            CachedBytes = 0,
                            CachePercentage = 0,
                            Status = "not_in_cache"
                        });
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Debug("[GetFileDetails] Failed to query cache status from {Instance}: {Error}",
                        instance.Name, ex.Message);
                    cacheStatuses.Add(new GetFileDetailsResponse.RcloneCacheStatus
                    {
                        InstanceName = instance.Name,
                        IsFullyCached = false,
                        CachedBytes = 0,
                        CachePercentage = 0,
                        Status = "error"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[GetFileDetails] Failed to query Rclone cache status");
        }

        return cacheStatuses;
    }

    /// <summary>
    /// Check if file exists in Rclone VFS cache directory.
    /// Cache structure: {VfsCachePath}/vfs/{remote}/{path}
    /// Uses sparse file detection to accurately determine actual cached bytes.
    /// </summary>
    private (bool isFullyCached, long cachedBytes, int percentage, string cachedPath)? CheckVfsCacheDirectory(
        RcloneInstance instance, string idsPath, string contentPath, long expectedSize)
    {
        try
        {
            var vfsCachePath = instance.VfsCachePath!;
            var remoteName = instance.RemoteName.TrimEnd(':');

            // Check both possible cache paths
            var possiblePaths = new[]
            {
                // .ids path: {VfsCachePath}/vfs/{remote}/.ids/9/6/1/b/0/uuid
                Path.Combine(vfsCachePath, "vfs", remoteName, idsPath),
                // Content path: {VfsCachePath}/vfs/{remote}/{contentPath}
                Path.Combine(vfsCachePath, "vfs", remoteName, contentPath),
                // Alternative structure without 'vfs' subdirectory
                Path.Combine(vfsCachePath, remoteName, idsPath),
                Path.Combine(vfsCachePath, remoteName, contentPath),
            };

            foreach (var cachePath in possiblePaths)
            {
                if (System.IO.File.Exists(cachePath))
                {
                    // Use sparse file detection to get actual cached bytes
                    // Rclone VFS creates sparse files that report full size but may be partially downloaded
                    var (isFullyCached, cachedBytes, percentage) = FileSystemUtil.GetCacheStatus(cachePath, expectedSize);

                    return (isFullyCached, cachedBytes, percentage, cachePath);
                }
            }

            return null; // Not found in cache directory
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug("[GetFileDetails] Error checking VFS cache directory for {Instance}: {Error}",
                instance.Name, ex.Message);
            return null;
        }
    }
}
