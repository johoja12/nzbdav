using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Service for detecting STRM file playback requests.
/// Uses algorithmic validation - if a request uses a downloadKey generated with the strmKey,
/// it originated from an STRM file. No registry needed, works for all existing STRM files.
/// </summary>
public class StrmKeyRegistry
{
    private readonly ConfigManager _configManager;

    public static StrmKeyRegistry? Instance { get; private set; }

    public StrmKeyRegistry(ConfigManager configManager)
    {
        _configManager = configManager;
        Instance = this;
    }

    /// <summary>
    /// Check if a request is from STRM playback by validating the downloadKey.
    /// STRM files use the strmKey for signing, while WebDAV uses webdavKey.
    /// If the downloadKey validates with strmKey, it's STRM playback.
    /// </summary>
    public bool IsStrmPlayback(string? downloadKey, string? path)
    {
        if (string.IsNullOrEmpty(downloadKey) || string.IsNullOrEmpty(path))
            return false;

        var strmKey = _configManager.GetStrmKey();
        if (string.IsNullOrEmpty(strmKey))
            return false;

        // Generate what the downloadKey SHOULD be if it was created with strmKey
        var expectedKey = GetWebdavItemRequest.GenerateDownloadKey(strmKey, path);

        var isStrm = downloadKey == expectedKey;
        if (isStrm)
        {
            Log.Debug("[StrmKeyRegistry] STRM playback detected for path: {Path}", path);
        }

        return isStrm;
    }

    /// <summary>
    /// Legacy method for compatibility - no longer registers, just logs.
    /// STRM detection now uses algorithmic validation.
    /// </summary>
    public void Register(string downloadKey, Guid davItemId, string source, string filePath)
    {
        // No-op - algorithmic validation doesn't need registration
        Log.Debug("[StrmKeyRegistry] STRM file created for {Source}: {FilePath}", source, filePath);
    }
}
