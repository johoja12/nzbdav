# Multi-Rclone VFS Cache Architecture

## Problem Statement

You have:
- NzbDav serving content via WebDAV
- Plex using symlinks pointing to `.ids/{prefix}/{uuid}` paths
- Rclone mounting WebDAV with VFS cache on NFS
- Need to scale cache capacity across multiple disks on NAS

## Current Architecture

```
Plex Media Library
    │
    ▼ (symlinks)
/mnt/remote/nzbdav/.ids/9/6/1/b/0/{uuid}
    │
    ▼ (rclone mount)
Single Rclone Instance ──► VFS Cache (NFS disk)
    │
    ▼ (WebDAV)
NzbDav Backend (port 8080)
```

## Scaling Options

---

### Option 1: Sharding by ID Prefix (Recommended)

Route files to different Rclone instances based on the first character of their UUID.

**Architecture:**
```
                    ┌─────────────────────────────────────────┐
                    │             Load Balancer               │
                    │   (nginx/haproxy with path routing)     │
                    └─────────────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
   Rclone #1              Rclone #2             Rclone #3
   (IDs 0-5)              (IDs 6-a)             (IDs b-f)
   Cache: /disk1          Cache: /disk2         Cache: /disk3
        │                     │                     │
        └─────────────────────┴─────────────────────┘
                              │
                              ▼
                         NzbDav WebDAV
```

**Implementation:**

1. Create 3 Rclone configs, each with different cache paths:
```bash
# rclone-shard-1.conf
[nzbdav]
type = webdav
url = http://nzbdav:8080
vendor = other

# rclone-shard-2.conf (same, different cache path at runtime)
```

2. Start multiple Rclone instances:
```bash
# Shard 1: IDs starting with 0-5
rclone mount nzbdav: /mnt/shard1 \
  --vfs-cache-mode full \
  --cache-dir /nas/disk1/vfs-cache \
  --config /etc/rclone/rclone.conf

# Shard 2: IDs starting with 6-a
rclone mount nzbdav: /mnt/shard2 \
  --vfs-cache-mode full \
  --cache-dir /nas/disk2/vfs-cache \
  --config /etc/rclone/rclone.conf

# Shard 3: IDs starting with b-f
rclone mount nzbdav: /mnt/shard3 \
  --vfs-cache-mode full \
  --cache-dir /nas/disk3/vfs-cache \
  --config /etc/rclone/rclone.conf
```

3. Use mergerfs or symlink router to combine:
```bash
# Option A: mergerfs (simplest)
mergerfs /mnt/shard1:/mnt/shard2:/mnt/shard3 /mnt/remote/nzbdav \
  -o category.create=mfs,moveonenospc=true

# Option B: Custom symlink structure pointing to correct shard
# Plex symlinks would need to point to /mnt/shard{N}/.ids/...
```

**Pros:**
- Even distribution (UUID is random, so ~equal load per shard)
- Simple to add more shards later
- Each shard is independent (failure isolation)
- Easy to monitor per-shard cache usage

**Cons:**
- Requires symlink regeneration or mergerfs layer
- More complex setup
- Need to coordinate cache eviction across shards

---

### Option 2: Sharding by Category (Sonarr vs Radarr)

Route by content type using the `/content/{category}/` path structure.

**Architecture:**
```
                    ┌─────────────────────────────────────────┐
                    │              NzbDav WebDAV              │
                    │  /content/sonarr/...  /content/radarr/..│
                    └─────────────────────────────────────────┘
                              │
        ┌─────────────────────┴─────────────────────┐
        ▼                                           ▼
   Rclone #1                                   Rclone #2
   (TV Shows)                                  (Movies)
   Mount: /mnt/tv                             Mount: /mnt/movies
   Cache: /disk1                              Cache: /disk2
```

**Implementation:**

1. Mount separate Rclone instances for each category:
```bash
# TV Shows (Sonarr)
rclone mount nzbdav:/content/sonarr /mnt/tv \
  --vfs-cache-mode full \
  --cache-dir /nas/disk1/tv-cache

# Movies (Radarr)
rclone mount nzbdav:/content/radarr /mnt/movies \
  --vfs-cache-mode full \
  --cache-dir /nas/disk2/movie-cache
```

2. Update Plex libraries to point to category-specific mounts.

**Pros:**
- Natural separation by media type
- Can tune cache settings per content type (movies = larger files, TV = more files)
- Easy to understand and manage
- No symlink changes needed if using content paths

**Cons:**
- Uneven distribution (TV typically has more files, movies are larger)
- Doesn't work with `.ids/` paths (which Plex currently uses)
- Requires changing Plex library paths

---

### Option 3: NzbDav-Side Sharding (Built-in Support)

Add sharding support directly to NzbDav, where the backend routes to different storage based on ID prefix.

**Architecture:**
```
                    ┌─────────────────────────────────────────┐
                    │              NzbDav WebDAV              │
                    │         (internal routing logic)        │
                    └─────────────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
   Rclone #1              Rclone #2             Rclone #3
   (registered)           (registered)          (registered)
   Cache: /disk1          Cache: /disk2         Cache: /disk3
```

**Implementation:**

Modify NzbDav to:
1. Accept multiple Rclone instances in settings (already done!)
2. Add a "shard key" to each Rclone instance (e.g., "0-5", "6-a", "b-f")
3. When serving `.ids/{prefix}/` requests, route to the correct instance
4. Use Rclone's `vfs/refresh` API to pre-warm the correct shard

**Pros:**
- Transparent to Plex (no path changes)
- Integrated with existing NzbDav Rclone management
- Can use cache status API to check correct shard
- Single mount point for Plex

**Cons:**
- Requires NzbDav code changes
- More complex internal routing
- All traffic still goes through NzbDav

---

### Option 4: Union Mount with mergerfs + Cache Tiering

Use mergerfs to present a unified view, with automatic file distribution.

**Architecture:**
```
                    ┌─────────────────────────────────────────┐
                    │               mergerfs                  │
                    │         /mnt/remote/nzbdav              │
                    └─────────────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
   Rclone #1              Rclone #2             Rclone #3
   /mnt/r1                /mnt/r2               /mnt/r3
   Cache: SSD             Cache: HDD1           Cache: HDD2
   (hot tier)             (warm tier)           (cold tier)
```

**Implementation:**

```bash
# Mount multiple Rclone instances
rclone mount nzbdav: /mnt/r1 --cache-dir /ssd/cache &
rclone mount nzbdav: /mnt/r2 --cache-dir /nas/disk1/cache &
rclone mount nzbdav: /mnt/r3 --cache-dir /nas/disk2/cache &

# Combine with mergerfs
mergerfs \
  /mnt/r1:/mnt/r2:/mnt/r3 \
  /mnt/remote/nzbdav \
  -o defaults,allow_other,use_ino,cache.files=auto-full \
  -o category.create=mfs \
  -o category.search=ff
```

**Pros:**
- Transparent to Plex
- Automatic load distribution
- Can mix SSD (hot) and HDD (cold) tiers
- No code changes required

**Cons:**
- Same file may be cached in multiple places (waste)
- mergerfs adds latency
- Complex failure modes
- Harder to debug cache issues

---

### Option 5: Consistent Hashing with HAProxy

Use HAProxy to route requests to Rclone instances based on path hash.

**Architecture:**
```
                    ┌─────────────────────────────────────────┐
                    │               HAProxy                   │
                    │    (consistent hash on .ids path)       │
                    └─────────────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        ▼                     ▼                     ▼
   Rclone #1              Rclone #2             Rclone #3
   WebDAV on :5081        WebDAV on :5082       WebDAV on :5083
```

**Implementation:**

```haproxy
frontend nzbdav_frontend
    bind *:5080
    default_backend nzbdav_shards

backend nzbdav_shards
    balance uri whole
    hash-type consistent
    server shard1 127.0.0.1:5081 check
    server shard2 127.0.0.1:5082 check
    server shard3 127.0.0.1:5083 check
```

But wait - Rclone mounts don't serve WebDAV, they mount it. This option doesn't apply directly.

---

## Recommendation

### For Your Setup: Option 1 (ID Prefix Sharding) + mergerfs

This gives you:
1. **Even distribution** - UUIDs are random, so each shard gets ~equal files
2. **Transparent to Plex** - mergerfs presents a single mount point
3. **Independent scaling** - Add more shards as needed
4. **Failure isolation** - One shard down doesn't affect others

**Step-by-step Implementation:**

1. **Create shard mount points:**
```bash
mkdir -p /mnt/shard{1,2,3}
mkdir -p /mnt/remote/nzbdav
```

2. **Create Rclone systemd services:**
```bash
# /etc/systemd/system/rclone-shard1.service
[Unit]
Description=Rclone VFS Mount - Shard 1
After=network-online.target

[Service]
Type=notify
ExecStart=/usr/bin/rclone mount nzbdav: /mnt/shard1 \
  --config /etc/rclone/rclone.conf \
  --vfs-cache-mode full \
  --cache-dir /nas/disk1/vfs-cache \
  --vfs-cache-max-size 500G \
  --vfs-cache-max-age 168h \
  --allow-other \
  --rc \
  --rc-addr=127.0.0.1:5571

ExecStop=/bin/fusermount -uz /mnt/shard1
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

Repeat for shard2 (disk2, port 5572) and shard3 (disk3, port 5573).

3. **Create mergerfs mount:**
```bash
# /etc/systemd/system/mergerfs-nzbdav.service
[Unit]
Description=MergerFS for NzbDav shards
After=rclone-shard1.service rclone-shard2.service rclone-shard3.service

[Service]
Type=simple
ExecStart=/usr/bin/mergerfs \
  /mnt/shard1:/mnt/shard2:/mnt/shard3 \
  /mnt/remote/nzbdav \
  -o defaults,allow_other,use_ino \
  -o category.search=ff \
  -o cache.files=auto-full

ExecStop=/bin/fusermount -uz /mnt/remote/nzbdav
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

4. **Register Rclone instances in NzbDav:**
   - Add each shard's RC endpoint (5571, 5572, 5573)
   - Configure VFS cache path for each
   - Enable cache status monitoring

5. **Update Plex:** No changes needed - same mount point!

---

## Comparison Matrix

| Option | Distribution | Plex Changes | Complexity | Cache Efficiency | Failure Isolation |
|--------|-------------|--------------|------------|------------------|-------------------|
| 1. ID Prefix + mergerfs | Even | None | Medium | High | High |
| 2. Category Sharding | Uneven | Yes | Low | Medium | High |
| 3. NzbDav-Side | Even | None | High | High | Medium |
| 4. Pure mergerfs | Random | None | Low | Low (duplicates) | Medium |
| 5. HAProxy | N/A | N/A | N/A | N/A | N/A |

---

## Future Enhancement: Smart Sharding in NzbDav

We could add a feature to NzbDav that:
1. Tracks which shard each file is assigned to
2. Sends `vfs/refresh` only to the correct shard
3. Shows per-shard cache status in the UI
4. Automatically rebalances when shards are added/removed

This would require:
- New DB column: `DavItem.AssignedShard`
- Shard assignment on file creation (hash of ID prefix)
- Update Rclone API calls to target specific shard

Let me know if you want me to implement this!
