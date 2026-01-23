using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace NzbWebDAV.Utils;

/// <summary>
/// Utility for filesystem operations, including sparse file detection.
/// </summary>
public static class FileSystemUtil
{
    /// <summary>
    /// Gets the actual disk usage of a file (handles sparse files).
    /// On Linux, sparse files report apparent size via FileInfo.Length,
    /// but the actual blocks used may be less if not fully written.
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <returns>Actual bytes used on disk, or null if unable to determine</returns>
    public static long? GetActualDiskUsage(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            // On Linux, use stat to get actual blocks allocated
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return GetActualDiskUsageLinux(filePath);
            }

            // On other platforms, fall back to apparent size
            var fileInfo = new FileInfo(filePath);
            return fileInfo.Length;
        }
        catch (Exception ex)
        {
            Log.Debug("[FileSystemUtil] Failed to get actual disk usage for {Path}: {Error}", filePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Gets actual disk usage on Linux using stat command.
    /// stat --format=%b returns number of 512-byte blocks allocated.
    /// </summary>
    private static long? GetActualDiskUsageLinux(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "stat",
                Arguments = $"--format=%b \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000); // 5 second timeout

            if (process.ExitCode != 0)
            {
                Log.Debug("[FileSystemUtil] stat command failed for {Path}", filePath);
                return null;
            }

            // stat returns number of 512-byte blocks
            if (long.TryParse(output, out var blocks))
            {
                return blocks * 512;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug("[FileSystemUtil] Error running stat for {Path}: {Error}", filePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Checks if a file is a sparse file (actual disk usage less than apparent size).
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <param name="actualBytes">Output: actual bytes on disk</param>
    /// <param name="apparentBytes">Output: apparent file size</param>
    /// <returns>True if file is sparse (actual < apparent), false otherwise</returns>
    public static bool IsSparseFile(string filePath, out long actualBytes, out long apparentBytes)
    {
        actualBytes = 0;
        apparentBytes = 0;

        if (!File.Exists(filePath))
            return false;

        try
        {
            var fileInfo = new FileInfo(filePath);
            apparentBytes = fileInfo.Length;

            var actual = GetActualDiskUsage(filePath);
            if (actual.HasValue)
            {
                actualBytes = actual.Value;
                // Allow small variance for filesystem overhead
                return actualBytes < (apparentBytes * 0.99);
            }

            // If we can't determine actual usage, assume not sparse
            actualBytes = apparentBytes;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets cache status for a file, properly detecting partial cache via sparse file detection.
    /// </summary>
    /// <param name="filePath">Path to the cached file</param>
    /// <param name="expectedSize">Expected total file size</param>
    /// <returns>Tuple of (isFullyCached, actualCachedBytes, cachePercentage)</returns>
    public static (bool isFullyCached, long cachedBytes, int percentage) GetCacheStatus(string filePath, long expectedSize)
    {
        if (!File.Exists(filePath))
            return (false, 0, 0);

        try
        {
            var fileInfo = new FileInfo(filePath);
            var apparentSize = fileInfo.Length;

            // Get actual disk usage (handles sparse files)
            var actualBytes = GetActualDiskUsage(filePath) ?? apparentSize;

            // Use expected size for percentage calculation if available
            var referenceSize = expectedSize > 0 ? expectedSize : apparentSize;

            if (referenceSize <= 0)
                return (true, actualBytes, 100);

            var percentage = (int)Math.Min(100, (actualBytes * 100) / referenceSize);

            // Consider fully cached if actual bytes >= 99% of expected
            var isFullyCached = actualBytes >= (referenceSize * 0.99);

            return (isFullyCached, actualBytes, percentage);
        }
        catch (Exception ex)
        {
            Log.Debug("[FileSystemUtil] Error getting cache status for {Path}: {Error}", filePath, ex.Message);
            return (false, 0, 0);
        }
    }
}
