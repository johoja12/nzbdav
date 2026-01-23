using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.RcloneInstances;

[ApiController]
[Route("api/rclone-instances")]
public class RcloneInstancesController(DavDatabaseContext db) : ControllerBase
{
    private readonly RcloneCacheMigrationService _migrationService = new(db);
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var instances = await db.RcloneInstances.OrderBy(i => i.Name).ToListAsync().ConfigureAwait(false);
        return Ok(new { status = true, instances });
    }

    [HttpPost]
    public async Task<IActionResult> Create()
    {
        var form = await Request.ReadFormAsync().ConfigureAwait(false);

        var instance = new RcloneInstance
        {
            Id = Guid.NewGuid(),
            Name = form["name"].FirstOrDefault() ?? "",
            Host = form["host"].FirstOrDefault() ?? "",
            Port = int.TryParse(form["port"].FirstOrDefault(), out var port) ? port : 5572,
            Username = form["username"].FirstOrDefault(),
            Password = form["password"].FirstOrDefault(),
            RemoteName = form["remoteName"].FirstOrDefault() ?? "nzbdav:",
            IsEnabled = form["isEnabled"].FirstOrDefault() != "false",
            EnableDirRefresh = form["enableDirRefresh"].FirstOrDefault() != "false",
            EnablePrefetch = form["enablePrefetch"].FirstOrDefault() != "false",
            VfsCachePath = form["vfsCachePath"].FirstOrDefault(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.RcloneInstances.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return Ok(new { status = true, instance });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id)
    {
        var form = await Request.ReadFormAsync().ConfigureAwait(false);

        var instance = await db.RcloneInstances.FindAsync(id).ConfigureAwait(false);
        if (instance == null)
            return NotFound(new { status = false, error = "Instance not found" });

        instance.Name = form["name"].FirstOrDefault() ?? instance.Name;
        instance.Host = form["host"].FirstOrDefault() ?? instance.Host;
        instance.Port = int.TryParse(form["port"].FirstOrDefault(), out var port) ? port : instance.Port;
        instance.Username = form["username"].FirstOrDefault() ?? instance.Username;
        instance.Password = form["password"].FirstOrDefault() ?? instance.Password;
        instance.RemoteName = form["remoteName"].FirstOrDefault() ?? instance.RemoteName;
        instance.IsEnabled = form["isEnabled"].FirstOrDefault() != "false";
        instance.EnableDirRefresh = form["enableDirRefresh"].FirstOrDefault() != "false";
        instance.EnablePrefetch = form["enablePrefetch"].FirstOrDefault() != "false";
        instance.VfsCachePath = form["vfsCachePath"].FirstOrDefault() ?? instance.VfsCachePath;

        // Shard routing fields
        if (form.ContainsKey("isShardEnabled"))
            instance.IsShardEnabled = form["isShardEnabled"].FirstOrDefault() == "true";
        if (form.ContainsKey("shardPrefixes"))
            instance.ShardPrefixes = form["shardPrefixes"].FirstOrDefault();
        if (form.ContainsKey("shardIndex") && int.TryParse(form["shardIndex"].FirstOrDefault(), out var shardIndex))
            instance.ShardIndex = shardIndex;

        await db.SaveChangesAsync().ConfigureAwait(false);

        return Ok(new { status = true, instance });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var instance = await db.RcloneInstances.FindAsync(id).ConfigureAwait(false);
        if (instance == null)
            return NotFound(new { status = false, error = "Instance not found" });

        db.RcloneInstances.Remove(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return Ok(new { status = true });
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> TestConnection(Guid id)
    {
        var instance = await db.RcloneInstances.FindAsync(id).ConfigureAwait(false);
        if (instance == null)
            return NotFound(new { status = false, error = "Instance not found" });

        using var client = new RcloneClient(instance);
        var result = await client.TestConnectionAsync().ConfigureAwait(false);

        instance.LastTestedAt = DateTimeOffset.UtcNow;
        instance.LastTestSuccess = result.Success;
        instance.LastTestError = result.Success ? null : result.Message;
        await db.SaveChangesAsync().ConfigureAwait(false);

        return Ok(new { status = true, result });
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestNewConnection()
    {
        var form = await Request.ReadFormAsync().ConfigureAwait(false);

        var instance = new RcloneInstance
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            Host = form["host"].FirstOrDefault() ?? "",
            Port = int.TryParse(form["port"].FirstOrDefault(), out var port) ? port : 5572,
            Username = form["username"].FirstOrDefault(),
            Password = form["password"].FirstOrDefault(),
            RemoteName = form["remoteName"].FirstOrDefault() ?? "nzbdav:"
        };

        using var client = new RcloneClient(instance);
        var result = await client.TestConnectionAsync().ConfigureAwait(false);

        return Ok(new { status = true, result });
    }

    /// <summary>
    /// Get shard recommendations for all instances based on total count.
    /// </summary>
    [HttpGet("shard-recommendations")]
    public async Task<IActionResult> GetShardRecommendations()
    {
        var recommendations = await _migrationService.GetShardRecommendationsAsync().ConfigureAwait(false);
        return Ok(new { status = true, recommendations });
    }

    /// <summary>
    /// Get current migration status for progress tracking.
    /// </summary>
    [HttpGet("cache-migration/status")]
    public IActionResult GetMigrationStatus()
    {
        return Ok(MigrationStatus.GetStatus());
    }

    /// <summary>
    /// Preview cache migration - shows what files would be moved without actually moving them.
    /// </summary>
    [HttpGet("cache-migration/preview")]
    public async Task<IActionResult> PreviewCacheMigration()
    {
        var preview = await _migrationService.PreviewMigrationAsync().ConfigureAwait(false);

        var transfers = preview.Transfers.Select(kvp => new
        {
            sourceInstanceId = kvp.Key.SourceId,
            targetInstanceId = kvp.Key.TargetId,
            sourceInstanceName = kvp.Value.SourceInstanceName,
            targetInstanceName = kvp.Value.TargetInstanceName,
            fileCount = kvp.Value.FileCount,
            totalBytes = kvp.Value.TotalBytes,
            prefixes = string.Join("", kvp.Value.Prefixes.OrderBy(c => c))
        }).ToList();

        return Ok(new
        {
            status = true,
            message = preview.Message,
            totalFiles = preview.TotalFiles,
            totalBytes = preview.TotalBytes,
            transfers
        });
    }

    /// <summary>
    /// Migrate cache files between ALL instances based on shard configuration.
    /// Moves cached files from each instance to the correct instance based on shard prefixes.
    /// </summary>
    [HttpPost("cache-migration/migrate-all")]
    public async Task<IActionResult> MigrateAllCaches()
    {
        var result = await _migrationService.MigrateAllAsync().ConfigureAwait(false);

        if (!result.Success)
            return BadRequest(new { status = false, error = result.Error });

        return Ok(new
        {
            status = true,
            message = result.Message,
            filesMoved = result.FilesMoved,
            bytesMoved = result.BytesMoved,
            errors = result.Errors
        });
    }

    /// <summary>
    /// Migrate cache files OUT of a specific instance to their correct destinations.
    /// Files that belong to other instances (based on shard config) will be moved.
    /// </summary>
    [HttpPost("{id:guid}/migrate-cache")]
    public async Task<IActionResult> MigrateCacheFromInstance(Guid id)
    {
        var result = await _migrationService.MigrateFromInstanceAsync(id).ConfigureAwait(false);

        if (!result.Success)
            return BadRequest(new { status = false, error = result.Error });

        return Ok(new
        {
            status = true,
            message = result.Message,
            filesMoved = result.FilesMoved,
            bytesMoved = result.BytesMoved,
            errors = result.Errors
        });
    }

    /// <summary>
    /// Apply recommended shard configuration to an instance.
    /// </summary>
    [HttpPost("{id:guid}/apply-shard-recommendation")]
    public async Task<IActionResult> ApplyShardRecommendation(Guid id)
    {
        var instance = await db.RcloneInstances.FindAsync(id).ConfigureAwait(false);
        if (instance == null)
            return NotFound(new { status = false, error = "Instance not found" });

        // Get all instances to calculate total shards
        var allInstances = await db.RcloneInstances
            .OrderBy(x => x.ShardIndex ?? 0)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync()
            .ConfigureAwait(false);

        var totalShards = allInstances.Count;
        var shardIndex = allInstances.FindIndex(x => x.Id == id);

        if (shardIndex < 0) shardIndex = 0;

        var recommendedPrefixes = RcloneCacheMigrationService.GetRecommendedPrefixes(shardIndex, totalShards);

        instance.IsShardEnabled = true;
        instance.ShardIndex = shardIndex;
        instance.ShardPrefixes = recommendedPrefixes;

        await db.SaveChangesAsync().ConfigureAwait(false);

        return Ok(new
        {
            status = true,
            instance,
            shardIndex,
            totalShards,
            recommendedPrefixes
        });
    }
}
