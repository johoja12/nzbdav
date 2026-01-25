using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetFileDetails;

public class GetFileDetailsResponse
{
    public string DavItemId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Path { get; set; } = null!;
    public string WebdavPath { get; set; } = null!;
    public string? IdsPath { get; set; }
    public string? MappedPath { get; set; }
    public string? JobName { get; set; }
    public string DownloadUrl { get; set; } = null!;
    public string? NzbDownloadUrl { get; set; }
    public long FileSize { get; set; }
    public DavItem.ItemType ItemType { get; set; }
    public string ItemTypeString { get; set; } = null!;
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastHealthCheck { get; set; }
    public DateTimeOffset? NextHealthCheck { get; set; }
    public int MissingArticleCount { get; set; }
    public int TotalSegments { get; set; }
    public long? MinSegmentSize { get; set; }
    public long? MaxSegmentSize { get; set; }
    public long? AvgSegmentSize { get; set; }
    public string? MediaInfo { get; set; }
    public bool IsCorrupted { get; set; }
    public string? CorruptionReason { get; set; }
    public List<ProviderStatistic> ProviderStats { get; set; } = new();
    public HealthCheckInfo? LatestHealthCheckResult { get; set; }
    public List<RcloneCacheStatus> CacheStatus { get; set; } = new();

    /// <summary>
    /// Resolution info when the file's queue item was auto-resolved as "stuck" by ArrMonitoringService.
    /// Contains action taken, triggered rules, and Arr status messages.
    /// </summary>
    public ArrResolutionDetails? ArrResolution { get; set; }

    public class RcloneCacheStatus
    {
        public string InstanceName { get; set; } = null!;
        public bool IsFullyCached { get; set; }
        public long CachedBytes { get; set; }
        public int CachePercentage { get; set; }
        public string Status { get; set; } = "unknown";
        public string? CachedPath { get; set; }
    }

    public class ProviderStatistic
    {
        public int ProviderIndex { get; set; }
        public string ProviderHost { get; set; } = null!;
        public int SuccessfulSegments { get; set; }
        public int FailedSegments { get; set; }
        public int TimeoutErrors { get; set; }
        public int MissingArticleErrors { get; set; }
        public long TotalBytes { get; set; }
        public long TotalTimeMs { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        public long AverageSpeedBps { get; set; }
        public double SuccessRate { get; set; }
    }

    public class HealthCheckInfo
    {
        public HealthCheckResult.HealthResult Result { get; set; }
        public HealthCheckResult.RepairAction RepairStatus { get; set; }
        public string? Message { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public class ArrResolutionDetails
    {
        /// <summary>Action taken to resolve the stuck queue item (e.g., "RemoveAndBlocklistAndSearch")</summary>
        public string Action { get; set; } = null!;

        /// <summary>Rule messages that triggered this resolution (e.g., "sample file", "password protected")</summary>
        public List<string> TriggeredBy { get; set; } = new();

        /// <summary>Actual status messages from Arr (the full error details)</summary>
        public List<string> StatusMessages { get; set; } = new();

        /// <summary>When the resolution occurred</summary>
        public DateTimeOffset? ResolvedAt { get; set; }

        /// <summary>Arr instance host that reported the stuck item</summary>
        public string? ArrHost { get; set; }
    }
}
