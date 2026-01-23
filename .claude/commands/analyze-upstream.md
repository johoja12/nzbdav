# Analyze Upstream Commits

Analyze commits from the upstream repository and create a merge plan.

## Instructions

1. First, fetch the latest from upstream:
```bash
git fetch upstream
```

2. Get the list of commits from the last $ARGUMENTS days (default: 7 if not specified):
```bash
git log upstream/main --oneline --since="$ARGUMENTS days ago"
```

3. For each commit, analyze:
   - **Description**: What does this commit do?
   - **Usefulness**: Is it useful to integrate? Consider our custom features (Plex integration, SABnzbd auto-pause, shard routing, rclone multi-instance, streaming priority)
   - **Priority**: P1 (Critical bug fix), P2 (Important), P3 (Nice-to-have), P4 (Low), P5 (Skip)
   - **Approach**: Cherry-pick (clean), Manual (conflicts expected), Skip, or Review
   - **Conflicts**: Which of our custom files might conflict?

4. Categorize commits into:
   - Bug Fixes (highest priority)
   - New Features
   - UI Improvements
   - Infrastructure/Refactoring
   - Skip (not needed or conflicts with our implementation)

5. Check for file conflicts between our changes and upstream:
```bash
# Files we've changed
git diff upstream/main..main --name-only

# Files upstream changed
git diff main..upstream/main --name-only
```

6. Document the analysis in `docs/UPSTREAM_MERGE_PLAN.md` with:
   - Summary of commit count and analysis period
   - Tables for each category with commit hash, description, priority, approach, and conflicts
   - Recommended integration order (phases)
   - List of high-conflict areas to preserve our custom code

## Our Custom Features to Preserve

When analyzing conflicts, remember these are our custom additions that must be preserved:
- Plex OAuth and session verification
- SABnzbd auto-pause integration
- Rclone multi-instance with shard routing
- Cache migration service
- Streaming priority (PlexPlayback > PlexBackground > BufferedStreaming)
- Provider affinity and benchmarking
- Missing articles tracking
- Emby integration
- STRM file generation

## High Conflict Files

These files have significant custom modifications:
- `backend/Clients/Usenet/Connections/ConnectionPool.cs` - Circuit breaker fixes
- `backend/Clients/Usenet/MultiConnectionNntpClient.cs` - Custom error handling
- `backend/Services/HealthCheckService.cs` - Extensive customizations
- `backend/Services/StreamingConnectionLimiter.cs` - Priority system
- `frontend/server/app.ts` - Custom proxy code
- `entrypoint.sh` - GID fix already applied

## Usage

```
/analyze-upstream 7      # Analyze last 7 days
/analyze-upstream 14     # Analyze last 14 days
/analyze-upstream 30     # Analyze last 30 days
```
