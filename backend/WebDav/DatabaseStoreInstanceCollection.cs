using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.WebDav;

/// <summary>
/// The /instances collection that lists all rclone instances with shard routing enabled.
/// Each instance gets its own virtual root with filtered .ids based on shard configuration.
/// </summary>
public class DatabaseStoreInstancesCollection(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    UsenetStreamingClient usenetClient,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    NzbAnalysisService nzbAnalysisService
) : BaseStoreReadonlyCollection
{
    public override string Name => "instances";
    public override string UniqueKey => "instances-root";
    public override DateTime CreatedAt => default;

    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        // Parse instance ID
        if (!Guid.TryParse(request.Name, out var instanceId))
            return null;

        var instance = await dbClient.Ctx.RcloneInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == instanceId, request.CancellationToken)
            .ConfigureAwait(false);

        if (instance == null)
            return null;

        return new DatabaseStoreInstanceRootCollection(
            instance,
            httpContext,
            dbClient,
            configManager,
            usenetClient,
            queueManager,
            websocketManager,
            nzbAnalysisService
        );
    }

    protected override async Task<IStoreItem[]> GetAllItemsAsync(CancellationToken cancellationToken)
    {
        // List all rclone instances (enabled or not, they can still browse)
        var instances = await dbClient.Ctx.RcloneInstances
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return instances
            .Select(i => new DatabaseStoreInstanceRootCollection(
                i,
                httpContext,
                dbClient,
                configManager,
                usenetClient,
                queueManager,
                websocketManager,
                nzbAnalysisService
            ))
            .Select(x => x as IStoreItem)
            .ToArray();
    }

    protected override bool SupportsFastMove(SupportsFastMoveRequest request)
    {
        return false;
    }
}

/// <summary>
/// The root collection for a specific rclone instance.
/// Shows the same folder structure as the main root, but with .ids filtered by shard.
/// </summary>
public class DatabaseStoreInstanceRootCollection(
    RcloneInstance instance,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    UsenetStreamingClient usenetClient,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    NzbAnalysisService nzbAnalysisService
) : BaseStoreReadonlyCollection
{
    public override string Name => instance.Id.ToString();
    public override string UniqueKey => $"instance-root:{instance.Id}";
    public override DateTime CreatedAt => instance.CreatedAt.DateTime;

    private readonly HashSet<char> _shardPrefixes = instance.IsShardEnabled && !string.IsNullOrEmpty(instance.ShardPrefixes)
        ? ShardRoutingUtil.ParseShardPrefixes(instance.ShardPrefixes)
        : new HashSet<char>("0123456789abcdef".ToCharArray()); // All prefixes if not sharded

    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        // Map virtual folder names to actual collections
        return request.Name.ToLower() switch
        {
            ".ids" => GetFilteredIdsCollection(),
            "content" => await GetContentCollection(request.CancellationToken),
            "completed" => await GetCompletedCollection(request.CancellationToken),
            "nzb" => await GetNzbCollection(request.CancellationToken),
            _ => null
        };
    }

    protected override Task<IStoreItem[]> GetAllItemsAsync(CancellationToken cancellationToken)
    {
        // Always show these virtual directories
        var items = new List<IStoreItem>
        {
            GetFilteredIdsCollection(),
        };

        // Add content/completed/nzb as virtual folder stubs
        // These will resolve to actual collections when accessed
        items.Add(new VirtualFolderStub("content", "instance-content:" + instance.Id));
        items.Add(new VirtualFolderStub("completed", "instance-completed:" + instance.Id));
        items.Add(new VirtualFolderStub("nzb", "instance-nzb:" + instance.Id));

        return Task.FromResult(items.ToArray());
    }

    private DatabaseStoreFilteredIdsCollection GetFilteredIdsCollection()
    {
        return new DatabaseStoreFilteredIdsCollection(
            ".ids",
            "",
            httpContext,
            dbClient,
            usenetClient,
            configManager,
            nzbAnalysisService,
            _shardPrefixes
        );
    }

    private async Task<IStoreItem?> GetContentCollection(CancellationToken ct)
    {
        // Return the actual content folder from the database
        var contentFolder = await dbClient.GetDirectoryChildAsync(DavItem.Root.Id, "content", ct).ConfigureAwait(false);
        if (contentFolder == null) return null;

        return new DatabaseStoreCollection(
            contentFolder,
            httpContext,
            dbClient,
            configManager,
            usenetClient,
            queueManager,
            websocketManager,
            nzbAnalysisService
        );
    }

    private async Task<IStoreItem?> GetCompletedCollection(CancellationToken ct)
    {
        // Return the completed symlink folder
        var completedFolder = await dbClient.GetDirectoryChildAsync(DavItem.Root.Id, "completed", ct).ConfigureAwait(false);
        if (completedFolder == null) return null;

        return new DatabaseStoreSymlinkCollection(
            completedFolder,
            dbClient,
            configManager
        );
    }

    private async Task<IStoreItem?> GetNzbCollection(CancellationToken ct)
    {
        // Return the nzb watch folder
        var nzbFolder = await dbClient.GetDirectoryChildAsync(DavItem.Root.Id, "nzb", ct).ConfigureAwait(false);
        if (nzbFolder == null) return null;

        return new DatabaseStoreWatchFolder(
            nzbFolder,
            httpContext,
            dbClient,
            configManager,
            usenetClient,
            queueManager,
            websocketManager,
            nzbAnalysisService
        );
    }

    protected override bool SupportsFastMove(SupportsFastMoveRequest request)
    {
        return false;
    }
}

/// <summary>
/// Simple stub for virtual folders that shows in directory listings.
/// </summary>
public class VirtualFolderStub(string folderName, string folderUniqueKey) : BaseStoreReadonlyCollection
{
    public override string Name => folderName;
    public override string UniqueKey => folderUniqueKey;
    public override DateTime CreatedAt => default;

    protected override Task<IStoreItem?> GetItemAsync(GetItemRequest request) => Task.FromResult<IStoreItem?>(null);
    protected override Task<IStoreItem[]> GetAllItemsAsync(CancellationToken ct) => Task.FromResult(Array.Empty<IStoreItem>());
    protected override bool SupportsFastMove(SupportsFastMoveRequest request) => false;
}
