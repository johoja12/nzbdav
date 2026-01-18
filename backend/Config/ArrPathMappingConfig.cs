namespace NzbWebDAV.Config;

/// <summary>
/// Represents a single path mapping between NZBDav and Arr (Sonarr/Radarr) paths.
/// </summary>
public record PathMapping
{
    /// <summary>
    /// The path prefix as seen by NZBDav (e.g., "/media-union/nas3.2/")
    /// </summary>
    public string NzbdavPrefix { get; init; } = "";

    /// <summary>
    /// The corresponding path prefix in Arr (e.g., "/nas03/media-3.2/")
    /// </summary>
    public string ArrPrefix { get; init; } = "";
}

/// <summary>
/// Collection of path mappings for a specific Arr instance.
/// </summary>
public record ArrPathMappings
{
    public List<PathMapping> Mappings { get; init; } = new();

    /// <summary>
    /// Translate a NZBDav path to an Arr path using the configured mappings.
    /// </summary>
    public string TranslateToArrPath(string nzbdavPath)
    {
        foreach (var mapping in Mappings)
        {
            if (!string.IsNullOrEmpty(mapping.NzbdavPrefix) &&
                nzbdavPath.StartsWith(mapping.NzbdavPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.ArrPrefix + nzbdavPath[mapping.NzbdavPrefix.Length..];
            }
        }
        return nzbdavPath; // No mapping found, return as-is
    }

    /// <summary>
    /// Translate an Arr path back to a NZBDav path using the configured mappings.
    /// </summary>
    public string TranslateFromArrPath(string arrPath)
    {
        foreach (var mapping in Mappings)
        {
            if (!string.IsNullOrEmpty(mapping.ArrPrefix) &&
                arrPath.StartsWith(mapping.ArrPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.NzbdavPrefix + arrPath[mapping.ArrPrefix.Length..];
            }
        }
        return arrPath; // No mapping found, return as-is
    }
}
