using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.RcloneInstances;

[ApiController]
[Route("api/rclone-instances")]
public class RcloneInstancesController(IDbContextFactory<DavDatabaseContext> dbFactory) : BaseApiController
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
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

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        db.RcloneInstances.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return Ok(new { status = true, instance });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id)
    {
        var form = await Request.ReadFormAsync().ConfigureAwait(false);

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
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

        await db.SaveChangesAsync().ConfigureAwait(false);

        return Ok(new { status = true, instance });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
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
        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
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

    protected override Task<IActionResult> HandleRequest() => Task.FromResult<IActionResult>(Ok());
}
