using System.ComponentModel.DataAnnotations;

namespace NzbWebDAV.Database.Models;

/// <summary>
/// Represents a configured rclone instance for prefetch cache warming.
/// </summary>
public class RcloneInstance
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Display name for this rclone instance (e.g., "NAS01 rclone", "NAS02 rclone")
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Host address for rclone RC API (e.g., "192.168.55.174")
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Port for rclone RC API (default: 5572)
    /// </summary>
    public int Port { get; set; } = 5572;

    /// <summary>
    /// Optional username for RC authentication
    /// </summary>
    [MaxLength(100)]
    public string? Username { get; set; }

    /// <summary>
    /// Optional password for RC authentication
    /// </summary>
    [MaxLength(255)]
    public string? Password { get; set; }

    /// <summary>
    /// The rclone remote name that mounts NZBDav (e.g., "nzbdav:")
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string RemoteName { get; set; } = "nzbdav:";

    /// <summary>
    /// Whether this instance is enabled (master toggle)
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether to use this instance for dir refresh (vfs/refresh on import, vfs/forget on delete)
    /// </summary>
    public bool EnableDirRefresh { get; set; } = true;

    /// <summary>
    /// Whether to use this instance for prefetch cache warming
    /// </summary>
    public bool EnablePrefetch { get; set; } = true;

    /// <summary>
    /// Optional path to rclone VFS cache directory for disk cache deletion.
    /// When flushing cache via RC, files will also be deleted from this path.
    /// Structure: {VfsCachePath}/vfs/{remote}/.ids/...
    /// </summary>
    [MaxLength(500)]
    public string? VfsCachePath { get; set; }

    /// <summary>
    /// When this instance was created
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Last time this instance was successfully tested
    /// </summary>
    public DateTimeOffset? LastTestedAt { get; set; }

    /// <summary>
    /// Last test result (null = never tested, true = success, false = failed)
    /// </summary>
    public bool? LastTestSuccess { get; set; }

    /// <summary>
    /// Error message from last failed test
    /// </summary>
    [MaxLength(500)]
    public string? LastTestError { get; set; }

    /// <summary>
    /// Get the base URL for RC API calls
    /// </summary>
    public string GetBaseUrl() => $"http://{Host}:{Port}";
}
