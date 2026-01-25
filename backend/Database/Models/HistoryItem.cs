namespace NzbWebDAV.Database.Models;

public class HistoryItem
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string FileName { get; set; } = null!;
    public string JobName { get; set; } = null!;
    public string Category { get; set; }
    public DownloadStatusOption DownloadStatus { get; set; }
    public long TotalSegmentBytes { get; set; }
    public int DownloadTimeSeconds { get; set; }
    public string? FailMessage { get; set; }
    public Guid? DownloadDirId { get; set; }
    public bool IsHidden { get; set; }
    public DateTime? HiddenAt { get; set; }
    public string? NzbContents { get; set; }
    public string? FailureReason { get; set; }
    public bool IsImported { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }

    /// <summary>
    /// JSON containing Arr queue resolution details when item was auto-resolved as "stuck".
    /// Format: { "action": "RemoveAndBlocklistAndSearch", "triggeredBy": ["sample file"], "statusMessages": [...], "resolvedAt": "...", "host": "sonarr.example.org" }
    /// </summary>
    public string? ArrResolutionInfo { get; set; }

    public enum DownloadStatusOption
    {
        Completed = 1,
        Failed = 2,
    }
}