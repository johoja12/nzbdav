using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Api.Controllers.ProviderBenchmark;

[ApiController]
[Route("api/provider-benchmark")]
public class ProviderBenchmarkController(
    UsenetStreamingClient usenetClient,
    DavDatabaseContext dbContext,
    WebsocketManager websocketManager,
    NzbProviderAffinityService affinityService
) : ControllerBase
{
    private const int TestSizeMb = 200;
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
        // Find a test file first (while we still have the HTTP context)
        var testFile = await FindRandomLargeFile().ConfigureAwait(false);
        if (testFile == null)
        {
            Log.Warning("[Benchmark] No suitable test file found (>= {MinSize} GB)", MinFileSizeBytes / 1024.0 / 1024.0 / 1024.0);
            return Ok(new ProviderBenchmarkResponse
            {
                Status = false,
                Error = "No NZB file >= 1GB found in database. Please import a larger file first."
            });
        }

        var (fileName, segmentIds, fileSize) = testFile.Value;

        // Get all providers
        var allProviders = usenetClient.GetProviderInfo().ToList();

        // Filter providers based on request
        List<(int Index, string Host, string Type, int MaxConnections)> providers;
        if (request?.ProviderIndices != null && request.ProviderIndices.Count > 0)
        {
            providers = allProviders
                .Where(p => request.ProviderIndices.Contains(p.Index))
                .ToList();
        }
        else
        {
            providers = allProviders
                .Where(p => p.Type != "Disabled")
                .ToList();
        }

        if (providers.Count == 0)
        {
            Log.Warning("[Benchmark] No providers selected for testing");
            return Ok(new ProviderBenchmarkResponse
            {
                Status = false,
                Error = "No providers selected for testing."
            });
        }

        var runId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var includeLoadBalanced = request?.IncludeLoadBalanced ?? true;

        // Calculate total providers to test (individual + load-balanced if applicable)
        var totalProviders = providers.Count + (includeLoadBalanced && providers.Count > 1 ? 1 : 0);

        Log.Information("[Benchmark] Starting background benchmark: {FileName} ({Size:F2} GB), testing {Count} providers",
            fileName, fileSize / 1024.0 / 1024.0 / 1024.0, totalProviders);

        // Start the benchmark in a background task (NOT tied to HTTP request)
        _ = Task.Run(async () =>
        {
            await RunBenchmarkBackground(
                runId, createdAt, fileName, segmentIds, fileSize, providers,
                includeLoadBalanced, totalProviders
            ).ConfigureAwait(false);
        });

        // Return immediately with "started" response
        return Ok(new ProviderBenchmarkResponse
        {
            Status = true,
            RunId = runId,
            CreatedAt = createdAt,
            TestFileName = fileName,
            TestFileSize = fileSize,
            TestSizeMb = TestSizeMb,
            TotalProviders = totalProviders,
            IsComplete = false // Benchmark is running in background
        });
    }

    /// <summary>
    /// Runs the benchmark in a background task, sending progress via WebSocket.
    /// Not tied to HTTP request lifecycle.
    /// </summary>
    private async Task RunBenchmarkBackground(
        Guid runId,
        DateTimeOffset createdAt,
        string fileName,
        string[] segmentIds,
        long fileSize,
        List<(int Index, string Host, string Type, int MaxConnections)> providers,
        bool includeLoadBalanced,
        int totalProviders)
    {
        var response = new ProviderBenchmarkResponse
        {
            Status = true,
            RunId = runId,
            CreatedAt = createdAt,
            TestFileName = fileName,
            TestFileSize = fileSize,
            TestSizeMb = TestSizeMb,
            TotalProviders = totalProviders,
            IsComplete = false
        };

        // Use a separate cancellation token for the benchmark (10 minute overall timeout)
        using var benchmarkCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        try
        {
            Log.Information("[Benchmark] Background task started for RunId: {RunId}", runId);

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
                    benchmarkCts.Token
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
                    benchmarkCts.Token
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

            // Save all results to database using a new DbContext (background task)
            await using var db = new DavDatabaseContext();
            db.ProviderBenchmarkResults.AddRange(dbResults);
            await db.SaveChangesAsync().ConfigureAwait(false);

            // Refresh the affinity service's benchmark cache so new results are used for provider selection
            await affinityService.RefreshBenchmarkSpeeds().ConfigureAwait(false);

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
            Log.Warning("[Benchmark] Background benchmark timed out (10 minute limit)");
            response.Status = false;
            response.Error = "Benchmark timed out after 10 minutes.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Benchmark] Background benchmark error: {Message}", ex.Message);
            response.Status = false;
            response.Error = ex.Message;
        }
        finally
        {
            // Send final completion message
            response.IsComplete = true;
            await SendBenchmarkProgress(response).ConfigureAwait(false);
            Log.Information("[Benchmark] Background task completed for RunId: {RunId}", runId);
        }
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

    private async Task<(string FileName, string[] SegmentIds, long FileSize)?> FindRandomLargeFile()
    {
        // Get all DavItemIds that have missing article errors, with their error counts
        var errorCounts = await dbContext.MissingArticleSummaries
            .GroupBy(m => m.DavItemId)
            .Select(g => new { DavItemId = g.Key, TotalErrors = g.Sum(x => x.TotalEvents) })
            .ToDictionaryAsync(x => x.DavItemId, x => x.TotalErrors)
            .ConfigureAwait(false);

        // Collect all candidate files with their error counts
        var candidates = new List<(Guid Id, string Name, long FileSize, int ErrorCount, string Type)>();

        // Find NzbFiles >= 1GB that are not corrupted
        // Note: SegmentIds.Length can't be translated to SQL (JSON array), so we filter client-side
        var nzbFiles = await dbContext.NzbFiles
            .Include(n => n.DavItem)
            .Where(n => n.DavItem != null &&
                        n.DavItem.FileSize >= MinFileSizeBytes &&
                        !n.DavItem.IsCorrupted)
            .Select(n => new { n.Id, n.DavItem!.Name, n.DavItem.FileSize, n.SegmentIds })
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var f in nzbFiles.Where(f => f.SegmentIds.Length > 0))
        {
            var errors = errorCounts.GetValueOrDefault(f.Id, 0);
            candidates.Add((f.Id, f.Name, f.FileSize ?? 0, errors, "Nzb"));
        }

        // Find MultipartFiles >= 1GB that are not corrupted
        var multipartFiles = await dbContext.MultipartFiles
            .Include(m => m.DavItem)
            .Where(m => m.DavItem != null &&
                        m.DavItem.FileSize >= MinFileSizeBytes &&
                        !m.DavItem.IsCorrupted)
            .Select(m => new { m.Id, m.DavItem!.Name, m.DavItem.FileSize })
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var f in multipartFiles)
        {
            var errors = errorCounts.GetValueOrDefault(f.Id, 0);
            candidates.Add((f.Id, f.Name, f.FileSize ?? 0, errors, "Multipart"));
        }

        // Find RarFiles >= 1GB that are not corrupted
        var rarFiles = await dbContext.RarFiles
            .Include(r => r.DavItem)
            .Where(r => r.DavItem != null &&
                        r.DavItem.FileSize >= MinFileSizeBytes &&
                        !r.DavItem.IsCorrupted)
            .Select(r => new { r.Id, r.DavItem!.Name, r.DavItem.FileSize })
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var f in rarFiles)
        {
            var errors = errorCounts.GetValueOrDefault(f.Id, 0);
            candidates.Add((f.Id, f.Name, f.FileSize ?? 0, errors, "Rar"));
        }

        if (candidates.Count == 0)
            return null;

        // Sort by error count ascending (prefer files with 0 errors)
        // Then randomize among files with same error count
        var sortedCandidates = candidates
            .OrderBy(c => c.ErrorCount)
            .ThenBy(_ => Random.Shared.Next())
            .ToList();

        // Prefer files with 0 errors if available, otherwise pick the one with fewest errors
        var zeroErrorCandidates = sortedCandidates.Where(c => c.ErrorCount == 0).ToList();
        var selected = zeroErrorCandidates.Count > 0
            ? zeroErrorCandidates[Random.Shared.Next(zeroErrorCandidates.Count)]
            : sortedCandidates.First();

        Log.Information("[Benchmark] Selected file: {Name} ({Size:F2} GB, {ErrorCount} missing article events, Type={Type})",
            selected.Name, selected.FileSize / 1024.0 / 1024.0 / 1024.0, selected.ErrorCount, selected.Type);

        // Fetch the actual file data based on type
        return selected.Type switch
        {
            "Nzb" => await GetNzbFileData(selected.Id).ConfigureAwait(false),
            "Multipart" => await GetMultipartFileData(selected.Id).ConfigureAwait(false),
            "Rar" => await GetRarFileData(selected.Id).ConfigureAwait(false),
            _ => null
        };
    }

    private async Task<(string FileName, string[] SegmentIds, long FileSize)?> GetNzbFileData(Guid id)
    {
        var nzbFile = await dbContext.NzbFiles
            .Include(n => n.DavItem)
            .FirstOrDefaultAsync(n => n.Id == id)
            .ConfigureAwait(false);

        if (nzbFile?.SegmentIds.Length > 0)
        {
            return (
                nzbFile.DavItem?.Name ?? "Unknown",
                nzbFile.SegmentIds,
                nzbFile.DavItem?.FileSize ?? 0
            );
        }
        return null;
    }

    private async Task<(string FileName, string[] SegmentIds, long FileSize)?> GetMultipartFileData(Guid id)
    {
        var multipartFile = await dbContext.MultipartFiles
            .Include(m => m.DavItem)
            .FirstOrDefaultAsync(m => m.Id == id)
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
        return null;
    }

    private async Task<(string FileName, string[] SegmentIds, long FileSize)?> GetRarFileData(Guid id)
    {
        var rarFile = await dbContext.RarFiles
            .Include(r => r.DavItem)
            .FirstOrDefaultAsync(r => r.Id == id)
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

        // Track progress outside try block so exception handlers can calculate partial speed
        var totalRead = 0L;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var usageContext = new ConnectionUsageContext(
                ConnectionUsageType.Streaming,
                new ConnectionUsageDetails
                {
                    Text = $"Benchmark: {fileName}",
                    JobName = fileName,
                    AffinityKey = affinityKey,
                    ForcedProviderIndex = isLoadBalanced ? null : providerIndex,
                    DisableGracefulDegradation = true // Throw exception on permanent failure instead of zero-filling
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
            var lastLoggedMb = 0L;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5)); // 5 minute timeout per provider

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
            result.SpeedMbps = totalRead > 0 ? (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds : 0;
            result.Success = true;

            Log.Information("[Benchmark] Download complete from {Host}: {Bytes:F1} MB in {Time:F1}s = {Speed:F2} MB/s",
                providerHost, totalRead / 1024.0 / 1024.0, result.ElapsedSeconds, result.SpeedMbps);
        }
        catch (PermanentSegmentFailureException ex)
        {
            // Permanent failure (article not found, etc.) - stop test early but record achieved speed
            stopwatch.Stop();
            result.BytesDownloaded = totalRead;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.SpeedMbps = totalRead > 0 && stopwatch.Elapsed.TotalSeconds > 0
                ? (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds
                : 0;
            result.Success = totalRead > 0; // Consider successful if we got some data
            result.ErrorMessage = $"Stopped early: {ex.FailureReason}";

            Log.Warning("[Benchmark] Provider {Host} test stopped early due to permanent failure at segment {Segment}: {Reason}. Achieved {Speed:F2} MB/s ({Bytes:F1} MB in {Time:F1}s)",
                providerHost, ex.SegmentIndex, ex.FailureReason, result.SpeedMbps, totalRead / 1024.0 / 1024.0, result.ElapsedSeconds);
        }
        catch (OperationCanceledException ex)
        {
            // Timeout - record partial speed if we got any data
            stopwatch.Stop();
            result.BytesDownloaded = totalRead;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.SpeedMbps = totalRead > 0 && stopwatch.Elapsed.TotalSeconds > 0
                ? (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds
                : 0;
            result.Success = totalRead > 0; // Consider successful if we got some data
            result.ErrorMessage = totalRead > 0
                ? $"Timed out after {totalRead / 1024.0 / 1024.0:F1} MB"
                : "Test timed out" + (ex.Message.Length > 0 ? $": {ex.Message}" : "");

            Log.Warning("[Benchmark] Provider {Host} test timed out. Achieved {Speed:F2} MB/s ({Bytes:F1} MB in {Time:F1}s)",
                providerHost, result.SpeedMbps, totalRead / 1024.0 / 1024.0, result.ElapsedSeconds);
        }
        catch (Exception ex)
        {
            // Other errors - record partial speed if we got any data
            stopwatch.Stop();
            result.BytesDownloaded = totalRead;
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            result.SpeedMbps = totalRead > 0 && stopwatch.Elapsed.TotalSeconds > 0
                ? (totalRead / 1024.0 / 1024.0) / stopwatch.Elapsed.TotalSeconds
                : 0;
            result.Success = totalRead > 0; // Consider successful if we got some data
            result.ErrorMessage = string.IsNullOrEmpty(ex.Message)
                ? $"Error: {ex.GetType().Name}"
                : ex.Message;

            Log.Warning(ex, "[Benchmark] Provider {Host} test failed: {Message}. Achieved {Speed:F2} MB/s ({Bytes:F1} MB)",
                providerHost, result.ErrorMessage, result.SpeedMbps, totalRead / 1024.0 / 1024.0);
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
