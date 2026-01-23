# Full NZB Tester Tool

The `FullNzbTester` is a powerful diagnostic tool embedded within the NzbDav backend. It simulates the full lifecycle of streaming a video file from an NZB without the overhead of WebDAV, FUSE, or a media player. It directly exercises the internal `DavMultipartFileStream` logic to measure performance and identify bottlenecks.

## Capabilities

1.  **NZB Parsing:** Validates the NZB structure and segments.
2.  **Network Probing:** Tests connectivity to Usenet providers by fetching the first segment of every file.
3.  **PAR2/RAR Analysis:** Simulates the deobfuscation and archive extraction steps.
4.  **FFprobe Verification:** Pipes the virtual file stream to `ffprobe` to verify the video/audio headers are valid and readable.
5.  **Scrubbing Simulation:** Jumps to various points in the file (10%, 50%, 90%, 20%) to measure seek latency and buffer responsiveness.
6.  **Throughput Benchmark:** Performs a sequential download of 50MB to calculate raw streaming speed.

## Usage

The tool is run via the `dotnet` CLI, invoking the `NzbWebDAV.csproj` project with the `--test-full-nzb` flag.

### Prerequisites

*   The NzbDav container or environment must be running (or at least the config database must be accessible).
*   `ffprobe` must be installed and in the system PATH.
*   You must provide a valid `CONFIG_PATH` environment variable pointing to your NzbDav configuration directory (where `db.sqlite` resides).

### Command Line

```bash
export CONFIG_PATH=/path/to/your/config
dotnet run --project backend/NzbWebDAV.csproj -- --test-full-nzb /path/to/your/video.nzb
```

### Docker Example

If you are running NzbDav in Docker, you can `exec` into the container to run the tool. Note that inside the container, the source code might not be available in the same way as a dev environment, but if you are developing, you likely have the source mounted.

If you are in a development environment (like the one this README was written in):

```bash
# Point to the active configuration
export CONFIG_PATH=/opt/docker_local/nzbdav/config

# Run the tester against a sample NZB
dotnet run --project backend/NzbWebDAV.csproj -- --test-full-nzb /home/ubuntu/nzbdav/babylon5.nzb
```

## Interpreting Results

At the end of the run, a **RESULTS SUMMARY** table is printed:

*   **Scrubbing Latency:** Lower is better. < 1000ms is excellent. > 5000ms indicates serious lag perceived by the user.
*   **Sequential Throughput:** Higher is better. Should approach your maximum internet connection speed or Usenet provider limit.

**Example Output:**

```
═══════════════════════════════════════════════════════════════
  FULL NZB TESTER RESULTS SUMMARY
═══════════════════════════════════════════════════════════════
  File Processed:       babylon5.nzb
  Total Files:          69
───────────────────────────────────────────────────────────────
  SCRUBBING LATENCY (Seek + First Read):
    Seek to 10%:           7852 ms (!!)
    Seek to 50%:           5604 ms (!!)
    Seek to 90%:           2496 ms (!)
    Seek to 20%:           6917 ms (!!)
    Total Scrub Time:     22.87 s
───────────────────────────────────────────────────────────────
  SEQUENTIAL THROUGHPUT:
    Speed:                8.73 MB/s
═══════════════════════════════════════════════════════════════
```
