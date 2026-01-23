# NzbDav Patches Integration Analysis

**Source:** `/home/ubuntu/patches/nzbdav-patches-2026-01-22/`
**Author:** Aamer Akhter
**Base Commit:** 3809304 (johoja12/nzbdav main)
**Total Patches:** 38
**Analysis Date:** 2026-01-23

---

## Executive Summary

This patch series adds comprehensive Emby support, rclone monitoring, startup improvements, and STRM file generation to NzbDav. The patches are organized into 7 feature groups with clear dependencies.

---

## Feature Groups Overview

| # | Feature | Patches | Lines Changed | Priority | Complexity |
|---|---------|---------|---------------|----------|------------|
| 01 | Rclone Stats Tab | 2 | +2,577 | Medium | Medium |
| 02 | Build/Version Info | 3 | ~400 | Low | Low |
| 03 | Startup Page | 3 | +282 | Medium | Low |
| 04 | Emby Support | 2 | +792 | **High** | Medium |
| 05 | Integrations Tab | 9 | ~1,500 | Medium | Medium |
| 06 | STRM Dual Output | 14 | ~2,000 | High | High |
| 07 | Stats Emby Badges | 5 | ~800 | Medium | Low |

---

## Detailed Feature Analysis

### 01-rclone-stats (2 patches)

**Purpose:** Real-time rclone instance monitoring in Stats page

**New Files:**
- `backend/Api/Controllers/RcloneInstances/RcloneInstancesController.cs`
- `backend/Api/Controllers/Stats/StatsController.Rclone.cs`
- `backend/Api/Controllers/Stats/RcloneStatsResponse.cs`
- `backend/Clients/RcloneClient.cs`
- `backend/Database/Models/RcloneInstance.cs`
- `backend/Database/Migrations/20260117210000_AddRcloneInstanceFeatureFlags.cs`
- `backend/Utils/FilenameMatchingUtil.cs`
- `frontend/app/routes/settings/rclone/rclone.tsx`
- `frontend/app/routes/stats/components/RcloneStats.tsx`
- `frontend/app/types/rclone.ts`

**Features:**
- Multiple rclone instance management (CRUD)
- Real-time VFS transfer monitoring
- Cache utilization display
- Active media playback with source indicator (Cache vs Downloading)
- Per-instance bandwidth tracking

**Database Changes:**
- New `RcloneInstances` table with columns: Id, Name, Host, Port, RemoteName, IsEnabled, EnableDirRefresh, EnablePrefetch, VfsCachePath

**Dependencies:** None (standalone)

---

### 02-build-version (3 patches)

**Purpose:** Build metadata and version information

**Patches:**
1. `0003-Add-build-system-Makefile-build.sh-VERSION.patch` - Makefile and build scripts
2. `0004-Add-cross-workspace-deployment-support-to-Makefile.patch` - Deployment helpers
3. `0005-Add-api-version-endpoint-for-build-info.patch` - `/api/version` endpoint

**New Files:**
- `Makefile` (optional, can be excluded)
- `build.sh` (optional)
- `VERSION` (optional)

**Modified Files:**
- `backend/Api/SabControllers/GetVersion/GetVersionController.cs`
- `Dockerfile` - adds build args

**Docker Build Args:**
```dockerfile
ARG NZBDAV_VERSION
ARG NZBDAV_BUILD_DATE
ARG NZBDAV_GIT_BRANCH
ARG NZBDAV_GIT_COMMIT
```

**API Response (`/api/version`):**
```json
{
  "version": "v1.0.0",
  "buildDate": "2026-01-22T10:00:00Z",
  "gitBranch": "main",
  "gitCommit": "abc123"
}
```

**Dependencies:** None (standalone)

**Note:** Makefile patches (0003, 0004) are optional and can be excluded if you prefer a different build approach.

---

### 03-startup-page (3 patches)

**Purpose:** Immediate UI feedback while backend initializes

**Patches:**
1. `0006-Add-startup-status-page-for-backend-initialization.patch` - Core startup page
2. `0007-Start-frontend-before-backend-for-immediate-startup-.patch` - Entrypoint change
3. `0008-Fix-Health-tab-auth-and-startup-page-health-polling.patch` - Bug fixes

**New Files:**
- `frontend/app/routes/startup/route.tsx`
- `frontend/app/routes/startup/route.module.css`

**Modified Files:**
- `frontend/app/root.tsx` - Catches backend errors, redirects to /startup
- `frontend/server/app.ts` - Adds /health to proxy list
- `entrypoint.sh` - Starts frontend before backend

**Behavior:**
1. Container starts → Frontend starts immediately
2. User visits → Sees startup page with build info
3. Frontend polls `/health` endpoint
4. Backend becomes ready → Auto-redirect to /login

**Dependencies:** 02-build-version (for version display)

**⚠️ Warning:** Changes to `entrypoint.sh` may conflict with existing startup logic.

---

### 04-emby-support (2 patches)

**Purpose:** Core Emby playback detection

**Patches:**
1. `0009-Add-Emby-play-detection-support.patch` - EmbyVerificationService
2. `0019-Add-EmbyPlayback-and-EmbyBackground-connection-types.patch` - Connection types

**New Files:**
- `backend/Services/EmbyVerificationService.cs`
- `backend/Api/Controllers/TestEmbyConnection/*.cs`

**New Connection Types:**
| Type | Value | Description |
|------|-------|-------------|
| EmbyPlayback | 9 | Active Emby playback session |
| EmbyBackground | 10 | Emby background activity |
| EmbyStrmPlayback | 11 | STRM file playback via Emby |

**ConfigManager Additions:**
```csharp
public EmbyConfig GetEmbyConfig()
{
    return new EmbyConfig
    {
        Servers = GetConfigValue<List<EmbyServer>>("emby.servers") ?? new List<EmbyServer>()
    };
}
```

**Emby Detection Logic:**
- Uses `/Sessions?api_key=KEY` endpoint
- Caches sessions for 3 seconds (TTL)
- Unlike Plex, Emby has no background activity (intro detection, thumbnails)
- All Emby streams via STRM files are real playback

**Dependencies:** None (standalone, but enables 05, 06, 07)

---

### 05-integrations-tab (9 patches)

**Purpose:** Enhanced Settings > Integrations page with health monitoring

**Patches:**
1. `0010-Add-background-health-polling-for-Plex-Emby-servers.patch`
2. `0011-Move-server-health-status-to-Stats-Integrations-tab.patch`
3. `0012-Add-SABnzbd-Radarr-Sonarr-Rclone-to-Integrations-tab.patch`
4. `0013-Add-project-logos-and-consistent-badge-colors-to-Int.patch`
5. `0014-Add-version-info-and-vfs-transfers-support-indicator.patch`
6. `0015-Fix-rclone-version-use-core-version-endpoint-instead.patch`
7. `0016-Add-version-fetching-for-Plex-and-Emby-servers.patch`
8. `0017-Fix-stream-classification-race-condition-when-both-P.patch`
9. `0018-Fix-Remove-duplicate-RcloneVersionInfo-class.patch`

**Features:**
- Background health polling for Plex/Emby servers
- Version fetching for all services (SABnzbd, Radarr, Sonarr, Rclone, Plex, Emby)
- Visual status indicators with project logos
- VFS/transfers endpoint support detection for rclone
- Race condition fix when both Plex and Emby are configured

**Modified Files:**
- `frontend/app/routes/settings/integrations/integrations.tsx` (heavily modified)
- `backend/Services/PlexVerificationService.cs`
- `backend/Services/EmbyVerificationService.cs`
- `backend/Clients/RcloneClient.cs`

**Dependencies:** 04-emby-support, 01-rclone-stats

---

### 06-strm-dual-output (14 patches)

**Purpose:** Generate .strm files alongside symlinks for Emby compatibility

**Patches:**
1. `0020-Add-dual-.strm-output-support-for-Emby-alongside-sym.patch` - Core dual output
2. `0021-Add-Settings-UI-for-dual-STRM-output.patch` - Config UI
3. `0022-Add-orphan-STRM-file-cleanup-to-maintenance-service.patch` - Orphan cleanup
4. `0023-Add-dual-STRM-config-keys-to-defaultConfig.patch` - Default config
5. `0024-Add-Populate-STRM-Library-button-to-Settings-UI.patch` - Manual populate
6. `0025-Fix-populate-strm-API-authentication.patch` - Auth fix
7. `0026-Fix-populate-strm-route-to-use-direct-fetch-with-API.patch` - API fix
8. `0027-Fix-STRM-file-extension-use-.strm-instead-of-.mkv.st.patch` - Extension fix
9. `0028-Fix-Exclude-STRM-library-from-cache-when-dual-mode-e.patch` - Cache exclusion
10. `0029-feat-STRM-playback-detection-for-Emby.patch` - Detection
11. `0030-refactor-Use-algorithmic-validation-for-STRM-detecti.patch` - Improved validation
12. `0031-fix-Complete-STRM-detection-implementation.patch` - Detection complete
13. `0032-Add-Emby-STRM-detection-for-streaming-monitor.patch` - Monitor integration
14. `0033-Move-Populate-STRM-Library-to-Maintenance-page.patch` - UI reorganization

**New Files:**
- `backend/Services/StrmService.cs` (or similar)
- `backend/Services/StrmLibraryPopulator.cs`
- `backend/Api/Controllers/Maintenance/MaintenanceController.cs` additions

**Config Keys:**
```
strm.dualOutput.enabled = true/false
strm.dualOutput.path = /data/strm-library
api.also-create-strm = true/false
api.strm-library-dir = /data/strm-library
```

**Use Case:**
Emby cannot follow NZBDav symlinks directly. STRM files provide HTTP streaming URLs that work with Emby:
```
http://backend:8082/stream/{davItemId}
```

**Features:**
- Creates `.strm` files in parallel with symlinks
- Orphan STRM cleanup in maintenance
- "Populate STRM Library" button for existing content
- STRM playback detection for streaming monitor

**Dependencies:** 04-emby-support

---

### 07-stats-emby-badges (5 patches)

**Purpose:** Emby flow attribution in Stats page

**Patches:**
1. `0034-Fix-Expose-Emby-active-sessions-for-UI-playback-tagg.patch`
2. `0035-Add-missing-connection-types-6-11-to-Stats-page-badg.patch`
3. `0036-Improve-Emby-session-matching-with-episode-based-fuz.patch`
4. `0037-Add-Emby-playback-background-sections-to-ProviderSta.patch`
5. `0038-Add-retry-for-Emby-detection-to-handle-cache-refresh.patch`

**Features:**
- Connection type badges 6-11 (Analysis, Plex BG, Plex, Emby, Emby BG, STRM)
- Episode-based fuzzy matching for Emby session detection
- Emby Playback/Background sections in ProviderStatus component
- Retry logic for Emby detection race condition

**Modified Files:**
- `frontend/app/routes/stats/components/ConnectionsTable.tsx`
- `frontend/app/routes/stats/components/ProviderStatus.tsx`
- `backend/Services/EmbyVerificationService.cs`
- `backend/Streams/NzbFileStream.cs`

**Dependencies:** 04-emby-support, 06-strm-dual-output

---

## Dependency Graph

```
02-build-version ──────► 03-startup-page
                              │
                              ▼
01-rclone-stats ──────► 05-integrations-tab
                              ▲
                              │
04-emby-support ──────┬──────┘
                      │
                      ├──────► 06-strm-dual-output ──────► 07-stats-emby-badges
                      │
                      └──────► 07-stats-emby-badges
```

---

## Integration Plan

### Phase 1: Core Infrastructure (Low Risk)
**Patches:** 02-build-version (optional), 03-startup-page
**Effort:** 1-2 hours
**Risk:** Low

1. Apply 02-build-version patches (or just 0005 for /api/version)
2. Apply 03-startup-page patches
3. Test: Container startup shows status page
4. Test: Auto-redirect when backend ready

### Phase 2: Emby Foundation (Medium Risk)
**Patches:** 04-emby-support
**Effort:** 2-3 hours
**Risk:** Medium

1. Apply 04-emby-support patches
2. Add EmbyVerificationService registration in Program.cs
3. Test: Emby connection test works
4. Test: Emby playback detection works

### Phase 3: Rclone Monitoring (Medium Risk)
**Patches:** 01-rclone-stats
**Effort:** 3-4 hours
**Risk:** Medium (database migration)

1. Apply 01-rclone-stats patches
2. Run database migration
3. Test: Rclone settings page works
4. Test: Rclone stats tab shows data

### Phase 4: Integrations Enhancement (Low Risk)
**Patches:** 05-integrations-tab
**Effort:** 2-3 hours
**Risk:** Low

1. Apply 05-integrations-tab patches
2. Test: Version display for all services
3. Test: Health polling works
4. Test: Race condition fix (Plex + Emby)

### Phase 5: STRM Dual Output (High Risk)
**Patches:** 06-strm-dual-output
**Effort:** 4-6 hours
**Risk:** High (14 patches, complex logic)

1. Apply patches incrementally
2. Test after each patch
3. Test: STRM files created in parallel
4. Test: Orphan cleanup works
5. Test: Populate button works
6. Test: STRM detection works

### Phase 6: Stats Badges (Low Risk)
**Patches:** 07-stats-emby-badges
**Effort:** 1-2 hours
**Risk:** Low

1. Apply 07-stats-emby-badges patches
2. Test: Badge display for types 6-11
3. Test: Emby sections in ProviderStatus

---

## Suggestions: Missing Features

Based on the patch analysis and current NzbDav architecture, here are features that might be missing or could be enhanced:

### 1. Jellyfin Support
**Status:** Not included in patches
**Description:** Jellyfin is a popular open-source alternative to Emby. Similar detection logic could be added.
**Implementation:** Create `JellyfinVerificationService.cs` mirroring EmbyVerificationService
**Priority:** Medium

### 2. Multi-User Playback Tracking
**Status:** Partially covered
**Description:** Track which user is playing what content
**Implementation:** Extend session detection to include user information
**Priority:** Low

### 3. Bandwidth Limiting per Connection Type
**Status:** Not included
**Description:** Allow limiting bandwidth for background activities vs playback
**Implementation:** Add QoS settings to GlobalOperationLimiter
**Priority:** Medium

### 4. STRM File Versioning
**Status:** Not included
**Description:** Track when STRM files were generated, allow regeneration
**Implementation:** Store STRM metadata in database
**Priority:** Low

### 5. Rclone VFS Cache Prewarming
**Status:** Mentioned (EnablePrefetch flag) but not fully implemented
**Description:** Automatically cache frequently accessed content
**Implementation:** Background service that triggers rclone prefetch based on Plex/Emby history
**Priority:** Medium

### 6. Health Check for Rclone Instances
**Status:** Basic (connection test)
**Description:** Periodic health monitoring of rclone instances
**Implementation:** Add to HealthCheckService or create RcloneHealthService
**Priority:** Low

### 7. Webhook Notifications for STRM Events
**Status:** Not included
**Description:** Notify external systems when STRM files are created/deleted
**Implementation:** Extend WebhookService with STRM events
**Priority:** Low

### 8. STRM Analytics Dashboard
**Status:** Not included
**Description:** Track STRM file usage, playback stats
**Implementation:** New stats section showing STRM access patterns
**Priority:** Low

### 9. Automatic Emby Library Scan Trigger
**Status:** Not included
**Description:** Trigger Emby library scan after STRM files are created
**Implementation:** Add Emby API call to refresh library path
**Priority:** Medium

### 10. Migration Tool for Existing Symlinks
**Status:** "Populate STRM Library" exists but limited
**Description:** Better migration from symlink-only to dual output
**Implementation:** Enhance with progress tracking, dry-run mode
**Priority:** Low

---

## Files to Review Before Integration

### High-Impact Files (review carefully)
1. `entrypoint.sh` - Startup order change
2. `backend/Program.cs` - DI registration
3. `backend/Database/DavDatabaseContext.cs` - New tables
4. `frontend/app/root.tsx` - Backend error handling
5. `backend/Queue/QueueItemProcessor.cs` - STRM creation

### Database Migrations
1. `20260117210000_AddRcloneInstanceFeatureFlags.cs` - Rclone instances table

### Config Keys Added
```
emby.servers
rclone.instances
strm.dualOutput.enabled
strm.dualOutput.path
api.also-create-strm
api.strm-library-dir
```

---

## Testing Checklist

### Pre-Integration
- [ ] Backup database
- [ ] Document current config
- [ ] Note current startup behavior

### Post-Integration
- [ ] Container starts with startup page
- [ ] Backend readiness detection works
- [ ] Emby connection test works
- [ ] Emby playback detection works
- [ ] Rclone instance CRUD works
- [ ] Rclone stats display works
- [ ] STRM files created alongside symlinks
- [ ] STRM playback detected correctly
- [ ] Stats badges show all types (6-11)
- [ ] No console errors in frontend
- [ ] No exceptions in backend logs

---

## Related Files

- **Patches Location:** `/home/ubuntu/patches/nzbdav-patches-2026-01-22/`
- **README:** `/home/ubuntu/patches/nzbdav-patches-2026-01-22/README.md`
- **Cover Letter:** `/home/ubuntu/patches/nzbdav-patches-2026-01-22/COVER_LETTER.txt`
- **All Patches (sequential):** `/home/ubuntu/patches/nzbdav-patches-2026-01-22/all-patches/`
- **Grouped by Feature:** `/home/ubuntu/patches/nzbdav-patches-2026-01-22/01-rclone-stats/`, etc.
