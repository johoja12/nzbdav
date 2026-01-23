# Multi-Rclone Shard Routing

This document describes how to set up multiple rclone instances with shard-based routing to distribute VFS cache across multiple servers or disk arrays, eliminating duplicate caching.

## Overview

When running NzbDav in a distributed environment with multiple rclone instances (e.g., multiple NAS boxes, different cache locations), each instance would normally cache ALL files. With shard routing, files are distributed across instances based on their UUID's first hex character, so each file is only cached once.

### Benefits

- **Eliminates duplicate caching**: Each file is cached by exactly one rclone instance
- **Linear cache scaling**: 4 instances = 4x effective cache capacity
- **Load distribution**: Streaming workload spread across instances
- **Fault tolerance**: Other shards continue working if one is down

### How It Works

1. Each file in NzbDav has a UUID (e.g., `3f8a2b1c-...`)
2. The UUID's first hex character (`3` in this example) determines which shard handles it
3. With 4 shards: shard 0 handles `0-3`, shard 1 handles `4-7`, etc.
4. Each rclone instance mounts its shard-specific WebDAV path
5. mergerfs combines all shard mounts into a single unified path

## Architecture

```
                                    NzbDav Server
                                         |
                    +--------------------+--------------------+
                    |                    |                    |
              /instances/A          /instances/B         /instances/C
              (prefixes 0-5)        (prefixes 6-A)       (prefixes B-F)
                    |                    |                    |
              rclone mount          rclone mount         rclone mount
              (RC port 5572)        (RC port 5573)       (RC port 5574)
                    |                    |                    |
              /mnt/shard0           /mnt/shard1          /mnt/shard2
                    |                    |                    |
                    +--------------------+--------------------+
                                         |
                                    mergerfs
                                         |
                                /mnt/nzbdav-merged
                                         |
                                   Plex/Jellyfin
```

## Configuration

### Step 1: Add Rclone Instances in NzbDav

1. Navigate to **Settings > Rclone** in the NzbDav UI
2. Click **+ Add Instance** for each rclone server
3. Configure connection details (host, port, credentials)
4. Set the **VFS Cache Path** for each instance

### Step 2: Enable Shard Routing

For each instance:

1. Click **Apply** on the recommended shard configuration, or manually:
2. Enable **Shard Routing** toggle
3. Set **Shard Index** (0, 1, 2, ... unique per instance)
4. Set **ID Prefixes** (e.g., `0-3` for shard 0 with 4 total shards)

The UI will show:
- **Recommended prefixes** based on total instance count
- **WebDAV mount path** specific to each instance
- **Recommended rclone.conf** configuration

### Step 3: Configure Rclone

Create an rclone remote for each shard. Example `rclone.conf`:

```ini
[nzbdav-shard0]
type = webdav
url = http://nzbdav-server:3000/instances/abc12345-1234-5678-9abc-def012345678
vendor = other
user = your-webdav-user
pass = your-webdav-pass-obscured

[nzbdav-shard1]
type = webdav
url = http://nzbdav-server:3000/instances/def67890-1234-5678-9abc-def012345678
vendor = other
user = your-webdav-user
pass = your-webdav-pass-obscured
```

**Important**: Each shard mounts a different `/instances/{instance-id}` path.

### Step 4: Mount Each Shard

Mount each rclone remote with VFS cache enabled:

```bash
# Shard 0
rclone mount nzbdav-shard0: /mnt/nzbdav-shard0 \
  --vfs-cache-mode full \
  --vfs-cache-max-size 100G \
  --cache-dir /mnt/cache/shard0 \
  --links \
  --use-cookies \
  --rc \
  --rc-addr :5572

# Shard 1
rclone mount nzbdav-shard1: /mnt/nzbdav-shard1 \
  --vfs-cache-mode full \
  --vfs-cache-max-size 100G \
  --cache-dir /mnt/cache/shard1 \
  --links \
  --use-cookies \
  --rc \
  --rc-addr :5573
```

**Key flags:**
- `--links`: Required for rclonelink symlink support
- `--use-cookies`: Required for authentication
- `--rc` and `--rc-addr`: Required for NzbDav to communicate with rclone
- `--vfs-cache-mode full`: Enables caching for streaming

### Step 5: Combine with mergerfs

Use mergerfs to combine all shard mounts into a single path:

```bash
mergerfs \
  /mnt/nzbdav-shard0:/mnt/nzbdav-shard1:/mnt/nzbdav-shard2 \
  /mnt/nzbdav-merged \
  -o defaults,allow_other,use_ino,category.create=mfs
```

Or in `/etc/fstab`:

```
/mnt/nzbdav-shard0:/mnt/nzbdav-shard1:/mnt/nzbdav-shard2 /mnt/nzbdav-merged fuse.mergerfs defaults,allow_other,use_ino,category.create=mfs 0 0
```

### Step 6: Configure Plex/Jellyfin

Point your media server at the merged path:
- **Library path**: `/mnt/nzbdav-merged/content/`

Sonarr/Radarr should also use the merged path for media management.

## Cache Migration

When enabling shard routing on an existing setup, cached files need to be moved to their correct shard locations.

### Automatic Migration

1. Ensure **all rclone instances are running** and accessible
2. In the NzbDav UI, click **Migrate Cache Files** for each instance
3. The migration will:
   - Check that all instances are available (migration fails if any are down)
   - Move files matching the instance's shard prefixes
   - Log progress to the NzbDav logs
   - Report files moved and total bytes

### What Gets Migrated

The VFS cache structure:
```
{VfsCachePath}/vfs/{remote}/.ids/{prefix1}/{prefix2}/.../{guid}
```

Files are moved from the legacy `.ids` path to an instance-specific path:
```
{VfsCachePath}/vfs/{remote}/instances/{instance-id}/.ids/{prefix}/...
```

### Migration Requirements

- **All enabled rclone instances must be running**: Migration will abort if any instance is unavailable
- **Sufficient disk space**: The destination should have space for the files being moved
- **No active streaming**: Best performed during low-usage periods

## Prefix Distribution

Prefixes are the first hex character of file UUIDs (0-9, a-f = 16 values).

| Total Shards | Shard 0 | Shard 1 | Shard 2 | Shard 3 |
|--------------|---------|---------|---------|---------|
| 1            | 0-f     | -       | -       | -       |
| 2            | 0-7     | 8-f     | -       | -       |
| 3            | 0-5     | 6-a     | b-f     | -       |
| 4            | 0-3     | 4-7     | 8-b     | c-f     |

The UI will calculate and recommend the optimal distribution.

## WebDAV Path Structure

Each instance's WebDAV path at `/instances/{id}/` exposes:

| Path | Contents |
|------|----------|
| `/content/` | Full directory listing (all files visible) |
| `/completed/` | Full symlinks to completed items |
| `/nzb/` | Full NZB file storage |
| `/.ids/` | **Filtered** - only IDs matching shard prefixes |

This means:
- Directory listings show all content (for proper navigation)
- Actual file access only works for files matching the shard
- Other files appear but return 404 when accessed (handled by mergerfs fallback)

## Troubleshooting

### Instance Unavailable During Migration

```
Cannot migrate: 2 of 3 instances are unavailable. All rclone instances must be running.
```

**Solution**: Ensure all rclone instances are running and accessible before migrating.

### Files Not Appearing in Shard

1. Check the file's UUID first character
2. Verify it matches the shard's prefix configuration
3. Check rclone mount logs for connection errors

### Cache Not Being Used

1. Verify `--vfs-cache-mode full` is set
2. Check cache directory permissions
3. Ensure sufficient disk space in cache location

### Streaming Errors After Sharding

1. Verify mergerfs is running and healthy
2. Check that all shard mounts are accessible
3. Test each shard individually before merging

## API Endpoints

### Get Shard Recommendations

```
GET /api/rclone-instances/shard-recommendations
```

Returns recommended prefix distribution for all instances.

### Apply Recommendation

```
POST /api/rclone-instances/{id}/apply-shard-recommendation
```

Automatically configures optimal shard settings for an instance.

### Migrate Cache

```
POST /api/rclone-instances/{id}/migrate-cache
```

Migrates cached files to the instance's shard-specific location.

### Test Instance Availability

```
POST /api/rclone-instances/{id}/test
```

Tests connection to an rclone instance.

## Best Practices

1. **Use identical cache sizes**: Each shard should have the same cache capacity
2. **Monitor all instances**: Set up health checks for each rclone mount
3. **Plan for failures**: mergerfs handles missing shards gracefully
4. **Migrate during low usage**: Cache migration can be I/O intensive
5. **Backup before migrating**: Keep copies of cache data until verified
6. **Start with 2 shards**: Scale up as needed, rather than starting with many

## Example: 3-Server Setup

### Server 1 (NzbDav host)
- Runs NzbDav container
- Runs rclone shard 0 (prefixes 0-5)
- Cache: `/mnt/nvme/cache/shard0`

### Server 2 (NAS)
- Runs rclone shard 1 (prefixes 6-a)
- Cache: `/mnt/nvme/cache/shard1`
- RC API accessible from Server 1

### Server 3 (NAS)
- Runs rclone shard 2 (prefixes b-f)
- Cache: `/mnt/nvme/cache/shard2`
- RC API accessible from Server 1

### Mergerfs (on Server 1)
```bash
mergerfs /mnt/shard0:/mnt/shard1:/mnt/shard2 /mnt/nzbdav -o defaults,allow_other
```

### Plex (on Server 1)
- Library: `/mnt/nzbdav/content/`
