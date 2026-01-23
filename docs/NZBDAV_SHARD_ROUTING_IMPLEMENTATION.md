# NzbDav-Side Shard Routing - Implementation Plan

## Overview

The goal is to have NzbDav assign each file to a specific Rclone shard based on its ID prefix, then:
1. Expose shard-specific WebDAV paths
2. Route Rclone API calls (refresh, forget, cache status) to the correct shard
3. Generate symlinks pointing to the correct shard mount

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         NzbDav WebDAV                           │
│                                                                 │
│  /.ids/           → Legacy path (all files)                     │
│  /.ids-s0/        → Shard 0 files (IDs starting with 0-3)       │
│  /.ids-s1/        → Shard 1 files (IDs starting with 4-7)       │
│  /.ids-s2/        → Shard 2 files (IDs starting with 8-b)       │
│  /.ids-s3/        → Shard 3 files (IDs starting with c-f)       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
         │              │              │              │
         ▼              ▼              ▼              ▼
    Rclone S0      Rclone S1      Rclone S2      Rclone S3
    mounts:        mounts:        mounts:        mounts:
    /.ids-s0/      /.ids-s1/      /.ids-s2/      /.ids-s3/
    cache: /d1     cache: /d2     cache: /d3     cache: /d4
         │              │              │              │
         ▼              ▼              ▼              ▼
┌─────────────────────────────────────────────────────────────────┐
│                        mergerfs                                  │
│                   /mnt/remote/nzbdav                            │
│                                                                 │
│  /.ids-s0/ ← from Rclone S0                                     │
│  /.ids-s1/ ← from Rclone S1                                     │
│  /.ids-s2/ ← from Rclone S2                                     │
│  /.ids-s3/ ← from Rclone S3                                     │
└─────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────────┐
│                      Plex Media Library                          │
│                                                                 │
│  Movie.mkv → /mnt/remote/nzbdav/.ids-s2/8/a/3/f/1/{uuid}        │
│  Show.mkv  → /mnt/remote/nzbdav/.ids-s0/1/2/3/4/5/{uuid}        │
└─────────────────────────────────────────────────────────────────┘
```

## Benefits

1. **No duplicate caching** - Each Rclone only sees files in its shard
2. **Targeted API calls** - vfs/refresh only hits the correct shard
3. **Even distribution** - UUIDs are random, so shards are balanced
4. **Transparent to Plex** - mergerfs combines shard mounts
5. **Graceful migration** - Legacy `/.ids/` path still works

---

## Code Changes Required

### 1. Database: Add Shard Assignment to RcloneInstance

**File: `backend/Database/Models/RcloneInstance.cs`**

```csharp
// Add shard configuration
public string? ShardPrefixes { get; set; }  // e.g., "0,1,2,3" or "0-3"
public int? ShardIndex { get; set; }        // 0, 1, 2, 3...
public bool IsShardEnabled { get; set; }    // Enable shard routing
```

**Migration:**
```csharp
migrationBuilder.AddColumn<string>(
    name: "ShardPrefixes",
    table: "RcloneInstances",
    type: "TEXT",
    nullable: true);

migrationBuilder.AddColumn<int>(
    name: "ShardIndex",
    table: "RcloneInstances",
    type: "INTEGER",
    nullable: true);

migrationBuilder.AddColumn<bool>(
    name: "IsShardEnabled",
    table: "RcloneInstances",
    type: "INTEGER",
    nullable: false,
    defaultValue: false);
```

---

### 2. Add Shard Routing Helper

**File: `backend/Utils/ShardRoutingUtil.cs`** (new file)

```csharp
namespace NzbWebDAV.Utils;

public static class ShardRoutingUtil
{
    /// <summary>
    /// Get the shard index for a given UUID based on its first hex character.
    /// Default: 4 shards (0-3, 4-7, 8-b, c-f)
    /// </summary>
    public static int GetShardIndex(Guid id, int totalShards = 4)
    {
        // First character of UUID determines shard
        var firstChar = id.ToString()[0];
        var hexValue = Convert.ToInt32(firstChar.ToString(), 16); // 0-15
        return hexValue * totalShards / 16;
    }

    /// <summary>
    /// Get the shard-specific .ids path for a file
    /// </summary>
    public static string GetShardedIdsPath(Guid id, int totalShards = 4)
    {
        var shardIndex = GetShardIndex(id, totalShards);
        var idStr = id.ToString();
        var prefix = string.Join("/", idStr.Take(5).Select(c => c.ToString()));
        return $"/.ids-s{shardIndex}/{prefix}/{idStr}";
    }

    /// <summary>
    /// Check if a shard handles a given ID prefix
    /// </summary>
    public static bool ShardHandlesPrefix(string shardPrefixes, char idFirstChar)
    {
        if (string.IsNullOrEmpty(shardPrefixes)) return true; // No filter = all

        var prefixes = ParseShardPrefixes(shardPrefixes);
        return prefixes.Contains(char.ToLower(idFirstChar));
    }

    /// <summary>
    /// Parse shard prefix configuration like "0,1,2,3" or "0-3" or "8-b"
    /// </summary>
    public static HashSet<char> ParseShardPrefixes(string config)
    {
        var result = new HashSet<char>();
        var parts = config.Split(',');

        foreach (var part in parts)
        {
            var trimmed = part.Trim().ToLower();
            if (trimmed.Contains('-'))
            {
                // Range like "0-3" or "a-f"
                var range = trimmed.Split('-');
                if (range.Length == 2)
                {
                    var start = Convert.ToInt32(range[0], 16);
                    var end = Convert.ToInt32(range[1], 16);
                    for (var i = start; i <= end; i++)
                    {
                        result.Add(i.ToString("x")[0]);
                    }
                }
            }
            else if (trimmed.Length == 1)
            {
                result.Add(trimmed[0]);
            }
        }

        return result;
    }
}
```

---

### 3. WebDAV: Add Shard-Specific Virtual Directories

**File: `backend/WebDav/DatabaseStore.cs`**

Currently, the IDs folder is defined as a single virtual directory. We need to add shard-specific variants.

```csharp
// In DatabaseStore.cs or IdsStoreCollection.cs

// Add shard detection in path parsing
public static bool TryParseShardPath(string path, out int shardIndex, out string remainingPath)
{
    shardIndex = -1;
    remainingPath = path;

    // Match /.ids-s{N}/...
    var match = Regex.Match(path, @"^/\.ids-s(\d+)/(.*)$");
    if (match.Success)
    {
        shardIndex = int.Parse(match.Groups[1].Value);
        remainingPath = "/" + match.Groups[2].Value;
        return true;
    }

    return false;
}
```

**File: `backend/WebDav/Stores/IdsStoreCollection.cs`**

Modify to filter items by shard when accessed via shard-specific path:

```csharp
public class IdsStoreCollection : BaseStoreCollection
{
    private readonly int? _shardFilter;  // null = no filter (legacy path)
    private readonly int _totalShards;

    public IdsStoreCollection(DavItem item, int? shardFilter = null, int totalShards = 4)
        : base(item)
    {
        _shardFilter = shardFilter;
        _totalShards = totalShards;
    }

    public override async IAsyncEnumerable<IStoreItem> GetItemsAsync(CancellationToken ct)
    {
        // When listing, only show items matching our shard
        var items = await base.GetItemsAsync(ct).ToListAsync(ct);

        foreach (var item in items)
        {
            if (_shardFilter == null)
            {
                yield return item; // Legacy path - show all
            }
            else if (item is DavItem davItem)
            {
                var itemShard = ShardRoutingUtil.GetShardIndex(davItem.Id, _totalShards);
                if (itemShard == _shardFilter)
                {
                    yield return item;
                }
            }
        }
    }
}
```

---

### 4. Update RcloneClient to Route by Shard

**File: `backend/Clients/RcloneClient.cs`**

```csharp
public class RcloneClient : IDisposable
{
    private readonly RcloneInstance _instance;

    // Add shard-aware methods
    public bool HandlesFile(Guid fileId)
    {
        if (!_instance.IsShardEnabled || string.IsNullOrEmpty(_instance.ShardPrefixes))
            return true; // Not sharded, handles all

        var firstChar = fileId.ToString()[0];
        return ShardRoutingUtil.ShardHandlesPrefix(_instance.ShardPrefixes, firstChar);
    }

    public string GetMountPath(Guid fileId)
    {
        if (_instance.IsShardEnabled && _instance.ShardIndex.HasValue)
        {
            // Shard-specific path
            var idStr = fileId.ToString();
            var prefix = string.Join("/", idStr.Take(5).Select(c => c.ToString()));
            return $"/.ids-s{_instance.ShardIndex}/{prefix}/{idStr}";
        }
        else
        {
            // Legacy path
            var idStr = fileId.ToString();
            var prefix = string.Join("/", idStr.Take(5).Select(c => c.ToString()));
            return $"/.ids/{prefix}/{idStr}";
        }
    }
}
```

---

### 5. Update Symlink Generation

**File: `backend/Utils/OrganizedLinksUtil.cs`**

When generating symlinks/STRM files, use shard-aware paths:

```csharp
public static string GetRcloneLinkPath(DavItem item, ConfigManager config)
{
    var mountDir = config.GetRcloneMountDir();
    var shardingEnabled = config.IsShardRoutingEnabled();

    if (shardingEnabled)
    {
        var shardIndex = ShardRoutingUtil.GetShardIndex(item.Id, config.GetTotalShards());
        var idStr = item.Id.ToString();
        var prefix = string.Join("/", idStr.Take(5).Select(c => c.ToString()));
        return Path.Combine(mountDir, $".ids-s{shardIndex}", prefix, idStr);
    }
    else
    {
        // Legacy path
        var idStr = item.Id.ToString();
        var prefix = string.Join("/", idStr.Take(5).Select(c => c.ToString()));
        return Path.Combine(mountDir, ".ids", prefix, idStr);
    }
}
```

---

### 6. Update Cache Status Checking

**File: `backend/Services/HealthCheckService.cs`**

When checking cache status, only query the shard that handles the file:

```csharp
private async Task<RcloneCacheCheckResult> CheckRcloneCacheStatusAsync(DavItem davItem, DavDatabaseContext db, CancellationToken ct)
{
    var instances = await db.RcloneInstances
        .AsNoTracking()
        .Where(i => i.IsEnabled)
        .ToListAsync(ct);

    foreach (var instance in instances)
    {
        // Skip instances that don't handle this file's shard
        if (instance.IsShardEnabled && !string.IsNullOrEmpty(instance.ShardPrefixes))
        {
            var firstChar = davItem.Id.ToString()[0];
            if (!ShardRoutingUtil.ShardHandlesPrefix(instance.ShardPrefixes, firstChar))
            {
                continue; // This shard doesn't handle this file
            }
        }

        // Check this instance's cache...
        using var client = new RcloneClient(instance);
        // ... existing cache check logic
    }
}
```

---

### 7. Update vfs/refresh Calls

**File: `backend/Services/RcloneRefreshService.cs`** (or wherever refresh is called)

```csharp
public async Task RefreshPathAsync(DavItem item)
{
    var instances = await _db.RcloneInstances
        .Where(i => i.IsEnabled && i.EnableDirRefresh)
        .ToListAsync();

    foreach (var instance in instances)
    {
        // Only refresh on the shard that handles this file
        if (instance.IsShardEnabled)
        {
            var firstChar = item.Id.ToString()[0];
            if (!ShardRoutingUtil.ShardHandlesPrefix(instance.ShardPrefixes ?? "", firstChar))
            {
                continue;
            }
        }

        using var client = new RcloneClient(instance);
        var path = client.GetMountPath(item.Id);
        await client.VfsRefreshAsync(path);
    }
}
```

---

### 8. Frontend: Add Shard Configuration UI

**File: `frontend/app/routes/settings/rclone/rclone.tsx`**

Add fields for shard configuration:

```tsx
<Form.Group>
    <Form.Check
        type="switch"
        label="Enable Shard Routing"
        checked={instance.isShardEnabled}
        onChange={e => updateInstance(instance.id, 'isShardEnabled', e.target.checked)}
    />
</Form.Group>

{instance.isShardEnabled && (
    <>
        <Form.Group>
            <Form.Label>Shard Index</Form.Label>
            <Form.Control
                type="number"
                value={instance.shardIndex ?? 0}
                onChange={e => updateInstance(instance.id, 'shardIndex', parseInt(e.target.value))}
            />
            <Form.Text muted>
                Unique index for this shard (0, 1, 2, 3...)
            </Form.Text>
        </Form.Group>

        <Form.Group>
            <Form.Label>ID Prefixes</Form.Label>
            <Form.Control
                type="text"
                value={instance.shardPrefixes ?? ''}
                onChange={e => updateInstance(instance.id, 'shardPrefixes', e.target.value)}
                placeholder="0-3 or 0,1,2,3"
            />
            <Form.Text muted>
                Hex prefixes this shard handles. Examples: "0-3", "4-7", "8-b", "c-f"
            </Form.Text>
        </Form.Group>
    </>
)}
```

---

### 9. Add Global Sharding Config

**File: `backend/Config/ConfigManager.cs`**

```csharp
public bool IsShardRoutingEnabled()
{
    return GetConfigValue("rclone.shard-routing-enabled", "false") == "true";
}

public int GetTotalShards()
{
    return int.Parse(GetConfigValue("rclone.total-shards", "4"));
}
```

---

## Migration Path

### Phase 1: Add Infrastructure (No Breaking Changes)
1. Add shard columns to RcloneInstance
2. Add ShardRoutingUtil
3. Add shard path detection to WebDAV (but still serve all files on legacy path)

### Phase 2: Enable Shard Paths
1. Expose `/.ids-s{N}/` virtual directories
2. Add UI for shard configuration
3. Add global toggle for shard routing

### Phase 3: Migrate Existing Symlinks
1. Add migration script to update existing symlinks to shard paths
2. Run during container startup if shard routing enabled
3. Old symlinks continue to work via legacy `/.ids/` path

### Phase 4: Optimize
1. Remove legacy path support (optional)
2. Add shard rebalancing when adding/removing shards
3. Add per-shard cache statistics in UI

---

## Files to Modify Summary

| File | Change Type | Description |
|------|-------------|-------------|
| `Database/Models/RcloneInstance.cs` | Modify | Add shard fields |
| `Database/Migrations/*` | New | Migration for shard columns |
| `Utils/ShardRoutingUtil.cs` | New | Shard routing logic |
| `WebDav/DatabaseStore.cs` | Modify | Parse shard paths |
| `WebDav/Stores/IdsStoreCollection.cs` | Modify | Filter by shard |
| `Clients/RcloneClient.cs` | Modify | Shard-aware methods |
| `Utils/OrganizedLinksUtil.cs` | Modify | Shard-aware symlink paths |
| `Services/HealthCheckService.cs` | Modify | Query correct shard |
| `Config/ConfigManager.cs` | Modify | Shard config getters |
| `frontend/.../rclone.tsx` | Modify | Shard config UI |
| `frontend/app/types/rclone.ts` | Modify | Add shard types |

---

## Estimated Effort

- **Phase 1**: 2-3 hours (infrastructure)
- **Phase 2**: 3-4 hours (shard paths + UI)
- **Phase 3**: 1-2 hours (migration script)
- **Phase 4**: 2-3 hours (optimization)

**Total: ~10-12 hours of development**

Would you like me to start implementing this?
