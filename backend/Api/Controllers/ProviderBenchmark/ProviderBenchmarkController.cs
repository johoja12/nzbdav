using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Api.Controllers.ProviderBenchmark;

[ApiController]
[Route("api/provider-benchmark")]
public class ProviderBenchmarkController(
    UsenetStreamingClient usenetClient,
    DavDatabaseContext dbContext,
    WebsocketManager websocketManager
) : ControllerBase
{
    private const int TestSizeMb = 300;
    private const long MinFileSizeBytes = 1L * 1024 * 1024 * 1024; // 1GB minimum
    private const int ConnectionsPerTest = 20;

    [HttpGet]
    public async Task<IActionResult> GetHistory()
    {
        return await GetBenchmarkHistory().ConfigureAwait(false);
    }

    [HttpGet("providers")]
    public IActionResult GetProviders()
    {
        var response = new ProviderListResponse { Status = true };

        try
        {
            var allProviders = usenetClient.GetProviderInfo();
            response.Providers = allProviders.Select(p => new ProviderInfoDto
            {
                Index = p.Index,
                Host = p.Host,
                Type = p.Type,
                MaxConnections = p.MaxConnections,
                IsDisabled = p.Type == "Disabled"
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Benchmark] Error getting provider list");
            response.Status = false;
            response.Error = ex.Message;
        }

        return Ok(response);
    }

    [HttpPost]
    public async Task<IActionResult> RunBenchmarkPost([FromBody] ProviderBenchmarkRequest? request)
    {
        return await RunBenchmark(request).ConfigureAwait(false);
    }

    private async Task<IActionResult> GetBenchmarkHistory()
    {
        var response = new ProviderBenchmarkHistoryResponse { Status = true };

        try
        {
            // Get distinct runs, ordered by most recent first
            var runs = await dbContext.ProviderBenchmarkResults
                .GroupBy(r => r.RunId)
                .Select(g => new
                {
                    RunId = g.Key,
                    CreatedAt = g.First().CreatedAt,
                    TestFileName = g.First().TestFileName,
                    TestFileSize = g.First().TestFileSize,
                    TestSizeMb = g.First().TestSizeMb,
                    Results = g.ToList()
                })
                .OrderByDescending(r => r.CreatedAt)
                .Take(10) // Last 10 benchmark runs
                .ToListAsync()
                .ConfigureAwait(false);

            response.Runs = runs.Select(r => new ProviderBenchmarkRunSummary
            {
                RunId = r.RunId,
                CreatedAt = r.CreatedAt,
                TestFileName = r.TestFileName,
                TestFileSize = r.TestFileSize,
                TestSizeMb = r.TestSizeMb,
                Results = r.Results.Select(res => new ProviderBenchmarkResultDto
                {
                    ProviderIndex = res.ProviderIndex,
                    ProviderHost = res.ProviderHost,
                    ProviderType = res.ProviderType,
                    IsLoadBalanced = res.IsLoadBalanced,
                    BytesDownloaded = res.BytesDownloaded,
                    ElapsedSeconds = res.ElapsedSeconds,
                    SpeedMbps = res.SpeedMbps,
                    Success = res.Success,
                    ErrorMessage = res.ErrorMessage
                }).ToList()
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Benchmark] Error getting benchmark history");
            response.Status = false;
            response.Error = ex.Message;
        }

        return Ok(response);
    }

    private async Task<IActionResult> RunBenchmark(ProviderBenchmarkRequest? request)
    {
        var runId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var response = new ProviderBenchmarkResponse
        {
            Status = true,
            RunId = runId,
            CreatedAt = createdAt
        };

        Log.Information("[Benchmark] Benchmark requested, searching for test file (min {MinSize} GB)...",
            MinFileSizeBytes / 1024.0 / 1024.0 / 1024.0);

        try
        {
            // Find a random NZB file >= 1GB
            var testFile = await FindRandomLargeFile().ConfigureAwait(false);
            if (testFile == null)
            {
                Log.Warning("[Benchmark] No suitable test file found (>= {MinSize} GB)", MinFileSizeBytes / 1024.0 / 1024.0 / 1024.0);
                response.Status = false;
                response.Error = "No NZB file >= 1GB found in database. Please import a larger file first.";
                return Ok(response);
            }

            var (fileName, segmentIds, fileSize) = testFile.Value;
            response.TestFileName = fileName;
            response.TestFileSize = fileSize;
            response.TestSizeMb = TestSizeMb;

            Log.Information("[Benchmark] Selected test file: {FileName} ({Size:F2} GB, {Segments} segments)",
                fileName, fileSize / 1024.0 / 1024.0 / 1024.0, segmentIds.Length);

            // Get all providers
            var allProviders = usenetClient.GetProviderInfo().ToList();

            // Filter providers based on request
            List<(int Index, string Host, string Type, int MaxConnections)> providers;
            if (request?.ProviderIndices != null && request.ProviderIndices.Count > 0)
            {
                // Use only the specified providers (allows testing any provider including disabled/backup)
                providers = allProviders
                    .Where(p => request.ProviderIndices.Contains(p.Index))
                    .ToList();

                Log.Information("[Benchmark] Testing {Count} selected providers: {Providers}",
                    providers.Count, string.Join(", ", providers.Select(p => p.Host)));
            }
            else
            {
                // Default: test all non-disabled providers
                providers = allProviders
                    .Where(p => p.Type != "Disabled")
                    .ToList();

                Log.Information("[Benchmark] Testing all {Count} active providers: {Providers}",
                    providers.Count, string.Join(", ", providers.Select(p => p.Host)));
            }

            if (providers.Count == 0)
            {
                Log.Warning("[Benchmark] No providers selected for testing");
                response.Status = false;
                response.Error = "No providers selected for testing.";
                return Ok(response);
            }

            Log.Information("[Benchmark] Will download {Size} MB from each provider using {Connections} connections",
                TestSizeMb, ConnectionsPerTest);

            var dbResults = new List<ProviderBenchmarkResult>();

            // Test each provider individually
            var providerNum = 0;
            foreach (var provider in providers)
            {
                providerNum++;
                Log.Information("[Benchmark] Testing provider {Num}/{Total}: {Host}",
                    providerNum, providers.Count, provider.Host);

                var result = await TestProvider(
                    provider.Index,
                    provider.Host,
                    provider.Type,
                    segmentIds,
                    fileSize,
                    fileName,
                    isLoadBalanced: false,
                    HttpContext.RequestAborted
                ).ConfigureAwait(false);

                response.Results.Add(result);

                if (result.Success)
                {
                    Log.Information("[Benchmark] Provider {Host} completed: {Speed:F2} MB/s",
                        provider.Host, result.SpeedMbps);
                }
                else
                {
                    Log.Warning("[Benchmark] Provider {Host} failed: {Error}",
                        provider.Host, result.ErrorMessage);
                }

                // Send WebSocket update for real-time UI
                await SendBenchmarkProgress(response).ConfigureAwait(false);

                // Create database record
                dbResults.Add(new ProviderBenchmarkResult
                {
                    RunId = runId,
                    CreatedAt = createdAt,
                    TestFileName = fileName,
                    TestFileSize = fileSize,
                    TestSizeMb = TestSizeMb,
                    ProviderIndex = result.ProviderIndex,
                    ProviderHost = result.ProviderHost,
                    ProviderType = result.ProviderType,
                    IsLoadBalanced = result.IsLoadBalanced,
                    BytesDownloaded = result.BytesDownloaded,
                    ElapsedSeconds = result.ElapsedSeconds,
                    SpeedMbps = result.SpeedMbps,
                    Success = result.Success,
                    ErrorMessage = result.ErrorMessage
                });
            }

            // Test load-balanced (NZB Affinity) - only if requested and multiple providers
            var includeLoadBalanced = request?.IncludeLoadBalanced ?? true;
            if (includeLoadBalanced && providers.Count > 1)
            {
                Log.Information("[Benchmark] Testing load-balanced mode (all {Count} providers combined)",
                    providers.Count);

                var lbResult = await TestProvider(
                    providerIndex: -1,
                    providerHost: "All Providers (Load Balanced)",
                    providerType: "Affinity",
                    segmentIds,
                    fileSize,
                    fileName,
                    isLoadBalanced: true,
                    HttpContext.RequestAborted
                ).ConfigureAwait(false);

                response.Results.Add(lbResult);

                if (lbResult.Success)
                {
                    Log.Information("[Benchmark] Load-balanced test completed: {Speed:F2} MB/s", lbResult.SpeedMbps);
                }
                else
                {
                    Log.Warning("[Benchmark] Load-balanced test failed: {Error}", lbResult.ErrorMessage);
                }

                // Send WebSocket update for real-time UI
                await SendBenchmarkProgress(response).ConfigureAwait(false);

                dbResults.Add(new ProviderBenchmarkResult
                {
                    RunId = runId,
                    CreatedAt = createdAt,
                    TestFileName = fileName,
                    TestFileSize = fileSize,
                    TestSizeMb = TestSizeMb,
                    ProviderIndex = lbResult.ProviderIndex,
                    ProviderHost = lbResult.ProviderHost,
                    ProviderType = lbResult.ProviderType,
                    IsLoadBalanced = lbResult.IsLoadBalanced,
                    BytesDownloaded = lbResult.BytesDownloaded,
                    ElapsedSeconds = lbResult.ElapsedSeconds,
                    SpeedMbps = lbResult.SpeedMbps,
                    Success = lbResult.Success,
                    ErrorMessage = lbResult.ErrorMessage
                });
            }

            // Save all results to database
            dbContext.ProviderBenchmarkResults.AddRange(dbResults);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);

            // Log summary
            var successfulResults = response.Results.Where(r => r.Success).ToList();
            var failedResults = response.Results.Where(r => !r.Success).ToList();

            Log.Information("[Benchmark] === Benchmark Complete ===");
            Log.Information("[Benchmark] Test file: {FileName}", fileName);
            Log.Information("[Benchmark] Successful: {Success}, Failed: {Failed}",
                successfulResults.Count, failedResults.Count);

            if (successfulResults.Count > 0)
            {
                var fastest = successfulResults.OrderByDescending(r => r.SpeedMbps).First();
                var slowest = successfulResults.OrderBy(r => r.SpeedMbps).First();
                Log.Information("[Benchmark] Fastest: {Host} @ {Speed:F2} MB/s", fastest.ProviderHost, fastest.SpeedMbps);
                Log.Information("[Benchmark] Slowest: {Host} @ {Speed:F2} MB/s", slowest.ProviderHost, slowest.SpeedMbps);
            }

            foreach (var failed in failedResults)
            {
                Log.Warning("[Benchmark] Failed: {Host} - {Error}", failed.ProviderHost, failed.ErrorMessage);
            }

            Log.Information("[Benchmark] Results saved to database (RunId: {RunId})", runId);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("[Benchmark] Benchmark was cancelled by user");
            response.Status = false;
            response.Error = "Benchmark was cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Benchmark] Error during benchmark: {Message}", ex.Message);
            response.Status = false;
            response.Error = ex.Message;
        }

        return Ok(response);
    }

    private async Task<(string FileName, string[] SegmentIds, long FileSize)?> FindRandomLargeFile()
    {
        var random = new Random();

        // Find random NzbFile >= 1GB
        var nzbFileIds = await dbContext.NzbFiles
            .Include(n => n.DavItem)
            .Where(n => n.DavItem != null && n.DavItem.FileSize >= MinFileSizeBytes)
            .Select(n => n.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        if (nzbFileIds.Count > 0)
        {
            var randomId = nzbFileIds[random.Next(nzbFileIds.Count)];
            var nzbFile = await dbContext.NzbFiles
                .Include(n => n.DavItem)
                .FirstOrDefaultAsync(n => n.Id == randomId)
                .ConfigureAwait(false);

            if (nzbFile != null && nzbFile.SegmentIds.Length > 0)
            {
                return (
                    nzbFile.DavItem?.Name ?? "Unknown",
                    nzbFile.SegmentIds,
                    nzbFile.DavItem?.FileSize ?? 0
                );
            }
        }

        // Try MultipartFile
        var multipartFileIds = await dbContext.MultipartFiles
            .Include(m => m.DavItem)
            .Where(m => m.DavItem != null && m.DavItem.FileSize >= MinFileSizeBytes)
            .Select(m => m.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        if (multipartFileIds.Count > 0)
        {
            var randomId = multipartFileIds[random.Next(multipartFileIds.Count)];
            var multipartFile = await dbContext.MultipartFiles
                .Include(m => m.DavItem)
                .FirstOrDefaultAsync(m => m.Id == randomId)
                .ConfigureAwait(false);

            if (multipartFile != null)
            {
                var segmentIds = multipartFile.Metadata?.FileParts?
                    .SelectMany(p => p.SegmentIds)
                    .ToArray() ?? [];

                if (segmentIds.Length > 0)
                {
                    return (
                        multipartFile.DavItem?.Name ?? "Unknown",
                        segmentIds,
                        multipartFile.DavItem?.FileSize ?? 0
                    );
                }
            }
        }

        // Try RarFile
        var rarFileIds = await dbContext.RarFiles
            .Include(r => r.DavItem)
            .Where(r => r.DavItem != null && r.DavItem.FileSize >= MinFileSizeBytes)
            .Select(r => r.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        if (rarFileIds.Count > 0)
        {
            var randomId = rarFileIds[random.Next(rarFileIds.Count)];
            var rarFile = await dbContext.RarFiles
                .Include(r => r.DavItem)
                .FirstOrDefaultAsync(r => r.Id == randomId)
                .ConfigureAwait(false);

            if (rarFile != null)
            {
                var segmentIds = rarFile.RarParts?
                    .SelectMany(p => p.SegmentIds)
                    .ToArray() ?? [];

                if (segmentIds.Length > 0)
                {
                    return (
                        rarFile.DavItem?.Name ?? "Unknown",
                        segmentIds,
                        rarFile.DavItem?.FileSize ?? 0
                    );
                }
            }
        }

        return null;
    }

    private async Task<ProviderBenchmarkResultDto> TestProvider(
        int providerIndex,
        string providerHost,
        string providerType,
        string[] segmentIds,
        long fileSize,
        string fileName,
        bool isLoadBalanced,
        CancellationToken cancellationToken)
    {
        var result = new ProviderBenchmarkResultDto
        {
            ProviderIndex = providerIndex,
            ProviderHost = providerHost,
            ProviderType = providerType,
            IsLoadBalanced = isLoadBalanced
        };

        var targetSize = (long)TestSizeMb * 1024 * 1024;
        var affinityKey = FilenameNormalizer.NormalizeName(System.IO.Path.GetFileNameWithoutExtension(fileName));

        try
        {
            var usageContext = new ConnectionUsageContext(
                ConnectionUsageType.Streaming,
                new ConnectionUsageDetails
                {
                    Text = $"Benchmark: {fileName}",
                    JobName = fileName,
                    AffinityKey = affinityKey,
                    ForcedProviderIndex = isLoadBalanced ? null : providerIndex
                }
            );

            await using var stream = usenetClient.GetFileStream(
                segmentIds,
                fileSize,
                ConnectionsPerTest,
                usageContext,
                useBufferedStreaming: true,
                bufferSize: ConnectionsPerTest * 5
            );

            var buffer = new byte[256 * 1024]; // 256KB chunks
            var totalRead = 0L;
            var lastLoggedMb = 0L;
            var stopwatch = Stopwatch.StartNew();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5)); // 5 minute timeout per provider

            Log.Information("[Benchmark] Starting download from {Host}...", providerHost);

            while (totalRead < targetSize && !cts.Token.IsCancellationRequested)
            {
                var bytesToRead = (int)Math.Min(buffer.Length, targetSize - totalRead);
                var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cts.Token).ConfigureAwait(false);

                if (read == 0)
                {
                    Log.Warning("[Benchmark] Stream ended early at {Read:F1} MB (expected {Target} MB)",
                        totalRead / 1024.0 / 1024.0, targetSize / 1024.0 / 1024.0);
                    break;
                }
                totalRead += read;

                // Log progress every 100 MB
                var currentMb = totalRead / 1024 / 1024;
                if (currentMb >= lastLoggedMb + 100)
                {
                    var currentSpeed = (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds;
                    Log.Information("[Benchmark] {Host}: {Read} MB / {Target} MB ({Speed:F1} MB/s)",
                        providerHost, currentMb, TestSizeMb, currentSpeed);
                    lastLoggedMb = currentMb;
                }
            }

            stopwatch.Stop();

            result.BytesDownloaded = totalRead;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.SpeedMbps = (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds;
            result.Success = true;

            Log.Information("[Benchmark] Download complete from {Host}: {Bytes:F1} MB in {Time:F1}s = {Speed:F2} MB/s",
                providerHost, totalRead / 1024.0 / 1024.0, result.ElapsedSeconds, result.SpeedMbps);
        }
        catch (OperationCanceledException ex)
        {
            result.Success = false;
            result.ErrorMessage = "Test timed out" + (ex.Message.Length > 0 ? $": {ex.Message}" : "");
            Log.Warning("[Benchmark] Provider {Host} test timed out: {Message}", providerHost, ex.Message);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = string.IsNullOrEmpty(ex.Message)
                ? $"Error: {ex.GetType().Name}"
                : ex.Message;
            Log.Warning(ex, "[Benchmark] Provider {Host} test failed: {Message}", providerHost, result.ErrorMessage);
        }

        return result;
    }

    private async Task SendBenchmarkProgress(ProviderBenchmarkResponse response)
    {
        try
        {
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            await websocketManager.SendMessage(WebsocketTopic.BenchmarkProgress, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug("[Benchmark] Failed to send WebSocket progress: {Message}", ex.Message);
        }
    }
}
