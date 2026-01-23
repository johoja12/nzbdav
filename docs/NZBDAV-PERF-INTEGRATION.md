# NzbDav-Perf Integration Analysis

**Source Repository:** `/home/ubuntu/nzbdav-perf` (Bitbucket: aamer_akhter/nzbdav-perf)
**Target Branch:** `integration2` (NzbDav main repo)
**Analysis Date:** 2026-01-23

---

## Overview

`nzbdav-perf` is a Python-based performance testing and benchmarking tool for NzbDav. It provides automated testing of streaming performance through multiple platforms (WebDAV, Plex, Emby) with detailed reporting and environment condition tracking.

---

## Tool Architecture

```
nzbdav-perf/
├── src/
│   ├── main.py                    # CLI entry point (Click-based)
│   ├── test_runner.py             # Test orchestration
│   ├── report_generator.py        # JSON/Markdown/CSV report generation
│   ├── version_collector.py       # Collects NzbDav/Docker version info
│   ├── environment_collector.py   # Captures system state before tests
│   ├── cache_manager.py           # Rclone VFS cache control
│   ├── preflight.py               # Pre-flight condition checks
│   ├── readiness.py               # NzbDav readiness detection
│   ├── config.py                  # Configuration loading
│   ├── models.py                  # Data models (Pydantic)
│   └── drivers/
│       ├── http/                  # HTTP drivers (httpx, requests, curl)
│       │   ├── httpx_driver.py
│       │   ├── requests_driver.py
│       │   └── curl_driver.py
│       └── playback/              # Media player drivers
│           ├── plex_api.py        # Plex API-based testing
│           ├── plex_browser.py    # Plex browser automation
│           ├── emby_api.py        # Emby API-based testing
│           └── emby_browser.py    # Emby browser automation
├── config.yml                     # Test configuration
├── secrets.yml                    # API keys (gitignored)
├── Makefile                       # Build and run commands
├── Dockerfile                     # Container build
└── reports/                       # Generated test reports
```

---

## Feature Summary by Commit

### Core Testing Features

| Commit | Feature | Description |
|--------|---------|-------------|
| `091e53b` | Initial implementation | Base performance testing framework |
| `bf55cda` | Plex/Emby API testing | API-based playback testing with session capture |
| `908b320` | Browser driver fixes | Threading for async, improved selectors |
| `0a7b7c3` | NzbDav /api/version | Version endpoint support |
| `b7f52a0` | Full build info | Show NzbDav build info from API |
| `27f0b4d` | Version/build in reports | Include version info in test reports |

### Environment & Pre-flight

| Commit | Feature | Description |
|--------|---------|-------------|
| `9dfd67e` | Environment conditions | Capture SAB, Rclone, Plex, Emby state before tests |
| `4d6c33b` | NzbDav uptime wait | Wait for uptime threshold before testing |
| `be1afb7` | Environment fixes | Docker uptime fallback, Rclone cache fields |
| `30c4928` | Readiness check | Wait for NzbDav to be ready before tests |
| `521ad3e` | Stats API integration | Use /api/stats/system, /api/stats/pool, /api/stats/errors |

### NNTP Provider Stats

| Commit | Feature | Description |
|--------|---------|-------------|
| `eee900f` | NNTP provider stats | Capture provider performance in environment |
| `d75aeb7` | Simplified display | Cleaner NNTP stats presentation |
| `9dc8d1a` | Connection counts | Add connection count to provider stats |

### SABnzbd Integration

| Commit | Feature | Description |
|--------|---------|-------------|
| `621cf4e` | SAB pause during tests | Auto-pause SABnzbd during performance tests |
| `4a92f5e` | Multi-SAB support | Support multiple SABnzbd instances |
| `55dedb6` | API key in secrets | Move SAB API key to secrets.yml |
| `a0a7f1a` | Status label fix | "Not Paused" instead of "Downloading" |

### Cache Management

| Commit | Feature | Description |
|--------|---------|-------------|
| `d770bf6` | Disk cache clearing | Clear VFS cache for accurate cold tests |
| `32d2808` | Clear test cache | Command to clear cache for specific test files |
| `04c14dd`, `77aba8c` | Cache path updates | Update rclone cache paths |

### Report Generation

| Commit | Feature | Description |
|--------|---------|-------------|
| `e3d1ab4` | Improved tables | Shorter headers, consistent Mbps units |
| `0638467` | Enhanced version info | Branch, repo, full timestamp |
| `23c26cc` | list-reports command | Display test report history |
| `a4d007f` | jq compatibility | Fix list-reports for different jq versions |

### Docker & Build

| Commit | Feature | Description |
|--------|---------|-------------|
| `6a2c4d6` | Dockerfile fix | Use \| delimiter for sed paths |
| `399667d` | Build info mount | Mount only config/reports, not src |
| `566e5cb` | test-image target | Comparative Docker image testing |
| `4b56633` | Version fallback | Docker metadata fallback for version |

---

## Key Capabilities

### 1. Test Scenarios
- **Cold**: Clear cache, measure first-access performance
- **Warm**: Cached access, measure best-case performance
- **Seek Cold**: Seek to random positions on cold cache
- **Seek Warm**: Seek to random positions on warm cache

### 2. Test Platforms
- **WebDAV**: Direct HTTP/HTTPS streaming tests
- **Plex**: API-based playback (session start/stop, metrics)
- **Emby**: API-based playback

### 3. HTTP Drivers
- **httpx**: Default async HTTP client
- **requests**: Fallback sync client
- **curl**: Shell-based curl wrapper

### 4. Environment Capture
Captures before each test run:
- SABnzbd: Paused state, queue slots, queue size
- Rclone: Cache usage (%), active transfers
- NzbDav: Uptime, ping latency, build version
- NNTP Providers: Pool stats, error counts per provider
- Plex/Emby: Active session counts

### 5. Pre-flight Checks
Configurable checks before testing:
- SABnzbd must be paused
- Rclone cache below threshold
- No active transfers/sessions
- NzbDav minimum uptime

### 6. Report Formats
- **JSON**: Machine-readable full results
- **Markdown**: Human-readable summary with tables
- **CSV**: Spreadsheet-compatible data

---

## Integration Options

### Option A: Include as Submodule
Add nzbdav-perf as a git submodule under `/tools/perf-test/`:

```bash
git submodule add git@bitbucket.org:aamer_akhter/nzbdav-perf.git tools/perf-test
```

**Pros:**
- Keeps histories separate
- Easy to update from upstream
- No code duplication

**Cons:**
- Requires submodule management
- Separate versioning

### Option B: Merge as Directory
Copy the code into `/tools/perf-test/` and commit:

**Pros:**
- Single repository
- Unified versioning
- Simpler CI/CD

**Cons:**
- Loses original git history
- Manual sync with upstream

### Option C: Companion Repository
Keep separate but add documentation links:

**Pros:**
- Complete separation of concerns
- Independent release cycles

**Cons:**
- Users must clone separately
- Version compatibility issues

---

## Required NzbDav API Endpoints

The perf tool expects these endpoints:

| Endpoint | Purpose | Status |
|----------|---------|--------|
| `/api/version` | Version/build info | **Exists** (SABnzbd compat) |
| `/api/stats/system` | Uptime, build version, startup time | **NEEDS TO BE ADDED** |
| `/api/stats/pool` | Connection pool stats by provider | **NEEDS TO BE ADDED** |
| `/api/stats/errors` | Provider error counts | **NEEDS TO BE ADDED** |
| `/api/sab?mode=queue` | SABnzbd queue (compatibility) | **Exists** |

### Existing Stats Endpoints (for reference)

| Endpoint | Purpose |
|----------|---------|
| `/api/stats/connections` | Active connections by provider |
| `/api/stats/bandwidth/current` | Current bandwidth stats |
| `/api/stats/bandwidth/history` | Historical bandwidth data |
| `/api/stats/deleted-files` | List of deleted files |
| `/api/stats/missing-articles` | Missing article summaries |
| `/api/stats/repair` | Trigger repair for files |
| `/api/stats/mapped-files` | Mapped files list |
| `/api/stats/dashboard/summary` | Dashboard summary |

### New Endpoints Required

#### 1. `/api/stats/system` (GET)
Returns system-level stats for performance testing:
```json
{
  "uptimeSeconds": 3600,
  "buildVersion": "v2026-01-23-FEATURE",
  "startupTime": "2026-01-23T10:00:00Z"
}
```

#### 2. `/api/stats/pool` (GET)
Returns connection pool stats per provider:
```json
{
  "providers": [
    {
      "index": 0,
      "host": "news.example.com",
      "maxConnections": 50,
      "activeConnections": 12,
      "idleConnections": 5
    }
  ]
}
```

#### 3. `/api/stats/errors` (GET)
Returns provider error counts:
```json
{
  "providers": [
    {
      "index": 0,
      "host": "news.example.com",
      "missingArticleErrors": 42,
      "timeoutErrors": 7,
      "totalErrors": 49
    }
  ]
}
```

---

## Integration Plan

### Phase 1: Add Missing API Endpoints
**Priority: High** - Required for perf tool to function

1. **Add `/api/stats/system`** endpoint:
   - Track application start time
   - Calculate uptime from start time
   - Return build version string

2. **Add `/api/stats/pool`** endpoint:
   - Expose connection pool stats per provider
   - Include max/active/idle connection counts

3. **Add `/api/stats/errors`** endpoint:
   - Aggregate error counts from NzbProviderStats table
   - Group by provider index

### Phase 2: Tool Integration
**Priority: Medium**

1. **Decision: Submodule vs Copy**
   - Recommend: Git submodule at `tools/perf-test/`
   - Allows independent updates
   - Keeps Python/C# codebases separate

2. **Add to repository**:
   ```bash
   git submodule add git@bitbucket.org:aamer_akhter/nzbdav-perf.git tools/perf-test
   ```

3. **Update .gitmodules** with proper tracking

### Phase 3: Documentation
**Priority: Medium**

1. Add setup guide in `docs/PERFORMANCE-TESTING.md`
2. Document required secrets (API keys)
3. Add example test profiles for common scenarios

### Phase 4: CI/CD Integration
**Priority: Low** (future)

1. GitHub Actions workflow for manual perf tests
2. Nightly regression tests comparing builds
3. Performance trend tracking over time

---

## Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `SKIP_PREFLIGHT` | Skip pre-flight checks | `0` |
| `STRICT_PREFLIGHT` | Abort on any warning | `0` |
| `NZBDAV_API_KEY` | API key for authenticated endpoints | - |
| `PLEX_TOKEN` | Plex authentication token | - |
| `EMBY_API_KEY` | Emby API key | - |
| `SABNZBD_API_KEY` | SABnzbd API key | - |

---

## Test File Configuration

Test files are defined in `config.yml`:

```yaml
test_files:
  movie_1080p:
    name: "All the Places (2023) 1080p"
    webdav_path: "content/movies/All the Places (2023)/file.mkv"
    plex_rating_key: "75882"
    emby_item_id: "316100"
    expected_bitrate_mbps: 3.4
```

---

## Recommended Next Steps

1. **Verify API Endpoints**: Check which stats endpoints exist in current NzbDav
2. **Choose Integration Method**: Submodule recommended for maintainability
3. **Set Up CI Pipeline**: GitHub Actions workflow for automated testing
4. **Add Test Profiles**: Create profiles for different test scenarios
5. **Document Setup**: Add setup guide for users wanting to run perf tests

---

## Related Files

- **Tool Source**: `/home/ubuntu/nzbdav-perf/`
- **Config Example**: `/home/ubuntu/nzbdav-perf/config.yml`
- **Reports**: `/home/ubuntu/nzbdav-perf/reports/`
