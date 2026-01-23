# Upstream Merge Plan

**Generated:** 2026-01-24
**Upstream Branch:** upstream/main
**Analysis Period:** Last 1 week (52 commits)
**Fork Commits:** 50+ commits ahead of upstream

## Summary

The upstream repository has 52 new commits in the last week. This document analyzes each commit for integration into our fork, considering our custom features (Plex integration, SABnzbd auto-pause, shard routing, rclone multi-instance, streaming priority).

## Implementation Status Summary

| Status | Count | Description |
|--------|-------|-------------|
| ✅ Implemented | 5 | Patches integrated into our fork |
| ⏳ Pending | 35 | Queued for integration |
| ⚠️ Partial | 2 | Different approach or partial implementation |
| ❌ Skipped | 10 | Will not integrate (conflicts/not needed) |

## Priority Legend

- **P1 (Critical):** Bug fixes that affect core functionality - integrate immediately
- **P2 (High):** Important features or fixes - integrate soon
- **P3 (Medium):** Nice-to-have improvements - integrate when convenient
- **P4 (Low):** Minor changes - integrate if no conflicts
- **P5 (Skip):** Not needed or conflicts with our implementation

## Integration Approach Legend

- **Cherry-pick:** Clean commit that can be cherry-picked directly
- **Manual:** Requires manual adaptation due to conflicts
- **Skip:** Do not integrate
- **Review:** Needs detailed review before deciding

---

## Category 1: Bug Fixes (Priority 1-2)

### 1.1 Critical Bug Fixes

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `23d8541` | Fixed `Cannot find byte position` bug | ✅ | `1417a77` | P1 | Cherry-pick | None |
| `7966414` | Fixed bug determining rar part numbers | ✅ | `e52fc7b` | P1 | Cherry-pick | None |
| `686878b` | Added support for par2 filenames containing relative paths | ✅ | `ffd36b7` | P1 | Cherry-pick | None |
| `7953211` | Added additional validation for rar volumes | ⏳ | - | P2 | Cherry-pick | Low |
| `aca8eac` | Fixed bug in GetQueueController when requesting non-first page | ✅ | `713338f` | P2 | Cherry-pick | None |
| `0cca3dd` | Fixed bug when updating upload-category dropdown while uploads active | ⏳ | - | P2 | Cherry-pick | None |
| `326be3f` | Fixed identifying root-cause exceptions | ✅ | `e52fc7b` | P2 | Cherry-pick | None |

### 1.2 Connection/Network Fixes

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `e46125e` | Do not retry nntp command when cancellation is requested | ⚠️ | `befdd3d` | P1 | Manual | **HIGH** - We have different circuit breaker approach |
| `9a66a24` | Ensure backend-proxied connections are closed correctly on errors | ⏳ | - | P2 | Cherry-pick | Low - frontend/server/app.ts |
| `b51ae7e` | Added logic to replace unhealthy Nntp connections | ⚠️ | `bb146dd` | P2 | Review | We have circuit breaker fixes |
| `d7b5153` | Minor refactor to nntp-retry logic | ⏳ | - | P3 | Review | Check compatibility |
| `9461b11` | Changed log-level to debug for logs relating to retried nntp commands | ❌ | - | P4 | Skip | We have our own logging |

---

## Category 2: New Features

### 2.1 Per-Category Health Checks (P2 - High Value)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `42fe84b` | Migrate old health-check setting to new per-category format | ⏳ | - | P2 | Manual | Review migration logic |
| `35139db` | Support per-category health checks during queue processing | ⏳ | - | P2 | Manual | Check HealthCheckService conflicts |
| `61eaf60` | Added UI setting for health-check categories | ⏳ | - | P2 | Manual | UI conflicts possible |
| `799c5af` | Added MultiCheckboxInput component | ⏳ | - | P2 | Cherry-pick | None |
| `392aadc` | Added support for sabnzbd `get_cats` api | ⏳ | - | P2 | Cherry-pick | None |

### 2.2 File Filtering with Wildcards (P3)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `3c0c6cc` | Migrate blocklisted-extensions to blocklisted-files format | ⏳ | - | P3 | Cherry-pick | None |
| `b179693` | Added support for file filtering with wildcard patterns | ⏳ | - | P3 | Cherry-pick | None |
| `dcf111b` | Added TagInput component | ⏳ | - | P3 | Cherry-pick | None |
| `dab4f2f` | Updated UI for 'Ignored Files' setting | ⏳ | - | P3 | Cherry-pick | None |
| `dff0ec0` | Updated UI for 'Categories' setting | ⏳ | - | P3 | Cherry-pick | None |

### 2.3 BlobStore for NZB Storage (P3 - Infrastructure)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `8b0fd2e` | Added BlobStore class to manage data blobs | ⏳ | - | P3 | Review | New feature - assess value |
| `adabf6d` | Added BlobCleanupItems table | ⏳ | - | P3 | Review | Requires migration |
| `73251d6` | Added BlobCleanupService | ⏳ | - | P3 | Review | New service |
| `0ae416d` | Updated AddFileController to write nzbs to BlobStore | ⏳ | - | P3 | Review | Depends on BlobStore |
| `0cadac9` | Added trigger for BlobCleanupItem on QueueItem delete | ⏳ | - | P3 | Review | Depends on BlobStore |
| `f617da7` | Switched to reading queue nzb stream from blob-store | ⏳ | - | P3 | Review | Depends on BlobStore |

### 2.4 Custom NZB Parser (P3)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `1b6e719` | Added custom nzb parser and removed Usenet dependency | ⏳ | - | P3 | Review | **Medium** - Major refactor, 17 files |

### 2.5 Mobile Upload Support (P3)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `0d2b675` | Provide a way to upload nzbs to queue from mobile | ⏳ | - | P3 | Cherry-pick | None |
| `e563469` | Added dropzone for uploading nzb files | ⏳ | - | P3 | Cherry-pick | None |
| `916bb4d` | Update UI status badge to reflect nzb-uploading status | ⏳ | - | P3 | Cherry-pick | None |
| `85cfad1` | Allow selecting and removing uploading queue items from UI | ⏳ | - | P3 | Cherry-pick | None |

---

## Category 3: UI Improvements

### 3.1 Queue/History UI (P3-P4)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `fad9f82` | Refactored /queue route | ⏳ | - | P4 | Review | May conflict with our queue UI |
| `9e97500` | Added React.memo to QueueRow component | ⏳ | - | P3 | Cherry-pick | Performance improvement |
| `9f12495` | Added pagination component | ⚠️ | `8c71185` | P3 | Manual | We have our own pagination |
| `8771e24` | Updated Queue and History table styling | ❌ | - | P4 | Skip | Style preference |
| `8ab4b00` | Updated /health page styling | ❌ | - | P4 | Skip | Style preference |
| `b9e078b` | Fix border of last table-row on /queue page | ⏳ | - | P4 | Cherry-pick | None |
| `a483777` | Removed Queue/History table badge | ❌ | - | P4 | Skip | Style preference |
| `e067e1d` | Added min-height to queueContainer UI | ⏳ | - | P4 | Cherry-pick | None |
| `139971a` | Updated simple-dropdown styling | ❌ | - | P4 | Skip | Style preference |
| `ecb203b` | Replaced unicode up arrow with CSS icon | ⏳ | - | P4 | Cherry-pick | None |

### 3.2 Category Dropdown (P4)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `c065073` | Refactored manual-category dropdown | ⏳ | - | P4 | Review | UI refactor |
| `f270b88` | Populate category dropdown with settings data | ⏳ | - | P4 | Review | Related to categories |
| `8e83bac` | Added placeholder UI for manual-upload category dropdown | ⏳ | - | P4 | Cherry-pick | None |

---

## Category 4: Infrastructure/Refactoring

### 4.1 Hosted Services Refactor (P3)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `6c9dd65` | Refactored ArrMonitoringService and HealthCheckService as asp.net hosted services | ⏳ | - | P3 | Manual | **HIGH** - We have custom HealthCheckService |

### 4.2 Environment Utilities (P4)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `ae0dfe3` | Refactored Environment.GetEnvironmentVariable -> EnvironmentUtil | ❌ | - | P4 | Skip | Low value |
| `4711100` | Renamed EnvironmentUtil.GetVariable -> GetRequiredVariable | ❌ | - | P4 | Skip | Low value |

### 4.3 Other (P4)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `3ba1518` | Updated default user-agent to nzbdav/{version} | ⏳ | - | P4 | Cherry-pick | None |
| `b13f20b` | Updated AddUrl api to fallback to url for nzb filename | ⏳ | - | P4 | Cherry-pick | None |
| `edf14e6` | Allow saving provider without testing, if disabled | ⏳ | - | P3 | Cherry-pick | None |
| `04d61d8` | Updated Arr automatic-queue-management options | ⏳ | - | P3 | Review | Check compatibility |

### 4.4 Entrypoint Fix (P2)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `14e6b6e` | Update entrypoint.sh to handle existing user/group of PUID/PGID | ✅ | `bcd8d9d` | P2 | N/A | Already implemented |

### 4.5 UsenetSharp Updates (P3)

| Commit | Description | Status | Fork Commit | Priority | Approach | Conflicts |
|--------|-------------|--------|-------------|----------|----------|-----------|
| `91d1c01` | Updated to use UsenetSharp 1.0.5 | ⏳ | - | P3 | Cherry-pick | Check package version |
| `c3547e4` | Updated to use UsenetSharp 1.0.6 | ⏳ | - | P3 | Cherry-pick | Check package version |

---

## Category 5: Skip

| Commit | Description | Reason |
|--------|-------------|--------|
| `dead14a` | Updated README.md demo and screenshots | Documentation only |

---

## Recommended Integration Order

### Phase 1: Critical Bug Fixes (Do First)
1. `23d8541` - Cannot find byte position bug
2. `7966414` - Rar part numbers bug
3. `686878b` - Par2 relative paths
4. `7953211` - Rar volume validation
5. `aca8eac` - GetQueueController pagination bug
6. `326be3f` - Root-cause exceptions

### Phase 2: Connection Fixes (Careful Review)
1. `9a66a24` - Backend proxy connection cleanup
2. `e46125e` - NNTP cancellation handling (MANUAL - conflicts with our changes)
3. `b51ae7e` - Unhealthy connection replacement (REVIEW)

### Phase 3: High-Value Features
1. Per-category health checks (5 commits)
2. Mobile upload support (4 commits)
3. `edf14e6` - Save disabled provider without testing

### Phase 4: Nice-to-Have
1. File filtering wildcards (5 commits)
2. UI improvements (selected)
3. UsenetSharp updates

### Phase 5: Major Refactors (Defer)
1. BlobStore infrastructure (6 commits) - assess value first
2. Custom NZB parser - major change, assess benefit
3. Hosted services refactor - conflicts with our services

---

## Potential Conflicts with Our Custom Code

### High Conflict Areas
1. **MultiConnectionNntpClient.cs** - We have circuit breaker, permit skipping changes
2. **HealthCheckService.cs** - We have extensive customizations
3. **ConnectionPool.cs** - We have circuit breaker cancellation fix
4. **entrypoint.sh** - We already have GID fix
5. **frontend/server/app.ts** - We have custom proxy code

### Our Custom Features to Preserve
- Plex OAuth and session verification
- SABnzbd auto-pause integration
- Rclone multi-instance with shard routing
- Cache migration service
- Streaming priority (PlexPlayback > PlexBackground > BufferedStreaming)
- Provider affinity and benchmarking
- Missing articles tracking

---

## Fork-Only Features

These are custom features in our fork that don't exist in upstream:

| Feature | Commits | Description |
|---------|---------|-------------|
| **Plex Integration** | `fa78028`, `7862818`, `6bcf19d`, etc. | OAuth authentication, session verification, streaming priority |
| **SABnzbd Auto-Pause** | `fa78028` | Pause downloads when Plex is streaming |
| **Rclone Multi-Instance** | `56a90a1` | Shard routing across multiple Rclone VFS instances |
| **Cache Migration** | `56a90a1` | Migrate VFS cache between Rclone instances |
| **Provider Affinity** | `e4a9a6a`, `d86ac92` | Per-NZB provider benchmarking and selection |
| **Missing Articles Tracking** | `24f48c4` | Track and display missing article statistics |
| **STRM Generation** | `76af3d3`, `c3b8414`, etc. | Dual output mode with .strm files for Emby |
| **Emby Integration** | `c9af850`, `258eb28` | Playback detection, cache state tracking |
| **Startup Status Page** | `49aff65`, `bd79519` | Show backend initialization progress |
| **Streaming Connection Limiter** | Multiple | Priority-based connection management |
| **Provider Error Separation** | `24f48c4` | Separate MissingArticle vs Timeout errors |

---

## Next Steps

1. Create feature branch for each phase
2. Cherry-pick/merge commits in order
3. Run tests after each integration
4. Build and test Docker image
5. Document any conflicts resolved
6. Update this document with new status after each integration
