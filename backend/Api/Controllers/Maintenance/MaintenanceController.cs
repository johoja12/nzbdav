using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Api.Controllers.Maintenance;

public class ResetConnectionsRequest
{
    public ConnectionUsageType? Type { get; set; }
}

[ApiController]
[Route("api/maintenance")]
public class MaintenanceController(
    UsenetStreamingClient usenetClient,
    DavDatabaseClient dbClient,
    NzbAnalysisService nzbAnalysisService,
    ConfigManager configManager
) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        return Task.FromResult<IActionResult>(NotFound());
    }

    [HttpGet("active-analyses")]
    public IActionResult GetActiveAnalyses()
    {
        var apiKey = HttpContext.GetRequestApiKey();
        if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
            return Unauthorized(new { error = "API Key Incorrect" });

        return Ok(nzbAnalysisService.GetActiveAnalyses());
    }

    [HttpPost("analyze/{id}")]
    public async Task<IActionResult> Analyze(Guid id)
    {
        return await AnalyzeBulk(new AnalyzeRequest { DavItemIds = new List<Guid> { id } });
    }

    public class AnalyzeRequest
    {
        public List<Guid> DavItemIds { get; set; } = new();
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeBulk([FromBody] AnalyzeRequest request)
    {
        try
        {
            var apiKey = HttpContext.GetRequestApiKey();
            if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
                return Unauthorized(new { error = "API Key Incorrect" });

            if (request.DavItemIds == null || request.DavItemIds.Count == 0)
                return BadRequest(new { error = "DavItemIds is required" });

            var processedCount = 0;
            foreach (var id in request.DavItemIds)
            {
                // Fetch generic DavItem first
                var davItem = await dbClient.Ctx.Items
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == id)
                    .ConfigureAwait(false);

                if (davItem == null) continue;

                string[]? segmentIds = null;
                if (davItem.Type == DavItem.ItemType.NzbFile)
                {
                    var nzbFile = await dbClient.GetNzbFileAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
                    segmentIds = nzbFile?.SegmentIds;
                }

                nzbAnalysisService.TriggerAnalysisInBackground(id, segmentIds, force: true);
                processedCount++;
            }

            return Accepted(new { message = $"Analysis started in background for {processedCount} item(s)." });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { error = e.Message });
        }
    }

    [HttpPost("reset-connections")]    public async Task<IActionResult> ResetConnections([FromBody] ResetConnectionsRequest request)
    {
        try
        {
            var apiKey = HttpContext.GetRequestApiKey();
            if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
                return Unauthorized(new { error = "API Key Incorrect" });

            await usenetClient.ResetConnections(request.Type);
            return Ok(new { message = "Connections reset successfully." });
        }
        catch (Exception e)
        {
            return StatusCode(500, new { error = e.Message });
        }
    }

    [HttpPost("populate-strm")]
    public async Task<IActionResult> PopulateStrmLibrary()
    {
        try
        {
            var apiKey = HttpContext.GetRequestApiKey();
            if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
                return Unauthorized(new { error = "API Key Incorrect" });

            if (!configManager.GetAlsoCreateStrm())
                return BadRequest(new { error = "Dual STRM output is not enabled. Enable 'Also create STRM files' in settings first." });

            var strmLibraryDir = configManager.GetStrmLibraryDir();
            if (string.IsNullOrEmpty(strmLibraryDir))
                return BadRequest(new { error = "STRM Library Directory is not configured." });

            var baseUrl = configManager.GetBaseUrl();
            if (string.IsNullOrEmpty(baseUrl))
                return BadRequest(new { error = "Base URL is not configured." });

            // Get all video files from database
            var videoExtensions = new[] { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts" };
            var videoItems = await dbClient.Ctx.Items
                .AsNoTracking()
                .Where(x => x.Type != DavItem.ItemType.Directory)
                .ToListAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var filteredItems = videoItems
                .Where(x => videoExtensions.Any(ext => x.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Log.Information("[PopulateStrm] Found {Count} video items to process", filteredItems.Count);

            var createdCount = 0;
            var skippedCount = 0;
            var strmKey = configManager.GetStrmKey();

            foreach (var davItem in filteredItems)
            {
                try
                {
                    var strmFilePath = GetStrmFilePath(davItem, strmLibraryDir);

                    // Skip if file already exists
                    if (System.IO.File.Exists(strmFilePath))
                    {
                        skippedCount++;
                        continue;
                    }

                    // Create directory if needed
                    var directoryName = Path.GetDirectoryName(strmFilePath);
                    if (directoryName != null && !Directory.Exists(directoryName))
                        Directory.CreateDirectory(directoryName);

                    // Generate STRM content
                    var targetUrl = GetStrmTargetUrl(davItem, baseUrl, strmKey);
                    await System.IO.File.WriteAllTextAsync(strmFilePath, targetUrl, HttpContext.RequestAborted);
                    createdCount++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[PopulateStrm] Error creating STRM for {Name}", davItem.Name);
                }
            }

            Log.Information("[PopulateStrm] Completed: {Created} created, {Skipped} skipped (already exist)", createdCount, skippedCount);
            return Ok(new {
                message = $"STRM library populated successfully.",
                created = createdCount,
                skipped = skippedCount,
                total = filteredItems.Count
            });
        }
        catch (Exception e)
        {
            Log.Error(e, "[PopulateStrm] Error populating STRM library");
            return StatusCode(500, new { error = e.Message });
        }
    }

    private static string GetStrmFilePath(DavItem davItem, string targetDirectory)
    {
        var path = Path.ChangeExtension(davItem.Path, ".strm");
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Join(targetDirectory, Path.Join(parts[2..]));
    }

    private static string GetStrmTargetUrl(DavItem davItem, string baseUrl, string strmKey)
    {
        if (baseUrl.EndsWith('/')) baseUrl = baseUrl.TrimEnd('/');
        var pathUrl = DatabaseStoreSymlinkFile.GetTargetPath(davItem.Id, "", '/');
        if (pathUrl.StartsWith('/')) pathUrl = pathUrl.TrimStart('/');
        var downloadKey = GetWebdavItemRequest.GenerateDownloadKey(strmKey, pathUrl);
        var extension = Path.GetExtension(davItem.Name).ToLower().TrimStart('.');
        return $"{baseUrl}/view/{pathUrl}?downloadKey={downloadKey}&extension={extension}";
    }
}
