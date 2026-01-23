using Microsoft.AspNetCore.Http;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.WebDav;

/// <summary>
/// A filtered version of DatabaseStoreIdsCollection that only shows items
/// matching specific shard prefixes. Used for instance-specific WebDAV paths.
/// </summary>
public class DatabaseStoreFilteredIdsCollection(
    string name,
    string currentPath,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    NzbAnalysisService nzbAnalysisService,
    HashSet<char> allowedPrefixes
) : BaseStoreReadonlyCollection
{
    public override string Name => name;
    public override string UniqueKey => $"shard:{string.Join("", allowedPrefixes)}:{currentPath}";
    public override DateTime CreatedAt => default;

    private const string Alphabet = "0123456789abcdef";

    private readonly string[] _currentPathParts = currentPath.Split(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar,
        StringSplitOptions.RemoveEmptyEntries
    );

    protected override async Task<IStoreItem?> GetItemAsync(GetItemRequest request)
    {
        var (dir, ctx, db, usenet, config, analysis) = (request.Name, httpContext, dbClient, usenetClient, configManager, nzbAnalysisService);

        if (_currentPathParts.Length < DavItem.IdPrefixLength)
        {
            if (request.Name.Length != 1) return null;
            if (!Alphabet.Contains(request.Name[0])) return null;

            // At the first level (e.g., /.ids/X), filter by allowed prefixes
            if (_currentPathParts.Length == 0)
            {
                if (!allowedPrefixes.Contains(char.ToLower(request.Name[0])))
                    return null; // Not in our shard
            }

            return new DatabaseStoreFilteredIdsCollection(
                dir, Path.Join(currentPath, dir), ctx, db, usenet, config, analysis, allowedPrefixes);
        }

        var item = await dbClient.GetFileById(request.Name).ConfigureAwait(false);
        if (item == null) return null;

        // Verify the item belongs to our shard
        if (!ShardRoutingUtil.ShardHandlesId(string.Join(",", allowedPrefixes), item.Id))
            return null;

        return new DatabaseStoreIdFile(item, ctx, dbClient, usenet, config, analysis);
    }

    protected override async Task<IStoreItem[]> GetAllItemsAsync(CancellationToken cancellationToken)
    {
        var (ctx, db, usenet, config, analysis) = (httpContext, dbClient, usenetClient, configManager, nzbAnalysisService);

        if (_currentPathParts.Length < DavItem.IdPrefixLength)
        {
            // At the first level, only return allowed prefixes
            if (_currentPathParts.Length == 0)
            {
                return Alphabet
                    .Where(x => allowedPrefixes.Contains(x))
                    .Select(x => x.ToString())
                    .Select(x => new DatabaseStoreFilteredIdsCollection(
                        x, Path.Join(currentPath, x), ctx, db, usenet, config, analysis, allowedPrefixes))
                    .Select(x => x as IStoreItem)
                    .ToArray();
            }

            // Deeper levels, return all sub-prefixes
            return Alphabet
                .Select(x => x.ToString())
                .Select(x => new DatabaseStoreFilteredIdsCollection(
                    x, Path.Join(currentPath, x), ctx, db, usenet, config, analysis, allowedPrefixes))
                .Select(x => x as IStoreItem)
                .ToArray();
        }

        var idPrefix = string.Join("", _currentPathParts);
        var files = await dbClient.GetFilesByIdPrefix(idPrefix).ConfigureAwait(false);

        // Filter files by shard
        return files
            .Where(x => ShardRoutingUtil.ShardHandlesId(string.Join(",", allowedPrefixes), x.Id))
            .Select(x => new DatabaseStoreIdFile(x, ctx, db, usenet, config, analysis))
            .Select(x => x as IStoreItem)
            .ToArray();
    }

    protected override bool SupportsFastMove(SupportsFastMoveRequest request)
    {
        return false;
    }
}
