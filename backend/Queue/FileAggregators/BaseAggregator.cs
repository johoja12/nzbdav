using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.FileProcessors;

namespace NzbWebDAV.Queue.FileAggregators;

public abstract class BaseAggregator
{
    public abstract void UpdateDatabase(List<BaseProcessor.Result> processorResults);
    protected abstract DavDatabaseClient DBClient { get; }
    protected abstract DavItem MountDirectory { get; }

    private static readonly char[] DirectorySeparators =
    [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
    ];

    /// <summary>
    /// Ensures that all parent-directories for the given `relativePath` exist.
    /// </summary>
    /// <param name="relativePath">The path at which to place a file, relative to the `MountDirectory`.</param>
    /// <returns>The parentDirectory DavItem</returns>
    protected DavItem EnsureParentDirectory(string relativePath)
    {
        var pathSegments = relativePath
            .Split(DirectorySeparators, StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        // Skip leading folder if it matches the mount directory name (prevents duplicate nested folders)
        // This can happen when archives contain a root folder with the same name as the release
        var startIndex = 0;
        if (pathSegments.Length > 1 &&
            string.Equals(pathSegments[0], MountDirectory.Name, StringComparison.OrdinalIgnoreCase))
        {
            startIndex = 1;
        }

        var parentDirectory = MountDirectory;
        var pathKey = "";
        for (var i = startIndex; i < pathSegments.Length - 1; i++)
        {
            pathKey = Path.Join(pathKey, pathSegments[i]);
            parentDirectory = EnsureDirectory(parentDirectory, pathSegments[i], pathKey);
        }

        return parentDirectory;
    }

    private readonly Dictionary<string, DavItem> _directoryCache = new();

    protected DavItem EnsureDirectory(DavItem parentDirectory, string directoryName, string pathKey)
    {
        if (_directoryCache.TryGetValue(pathKey, out var cachedDirectory)) return cachedDirectory;

        var directory = DavItem.New(
            id: Guid.NewGuid(),
            parent: parentDirectory,
            name: directoryName,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            releaseDate: null,
            lastHealthCheck: null
        );
        _directoryCache.Add(pathKey, directory);
        DBClient.Ctx.Items.Add(directory);
        return directory;
    }
}