namespace NzbWebDAV.Utils;

/// <summary>
/// Utility for shard-based routing of files across multiple Rclone instances.
/// Sharding distributes files by their UUID's first hex character (0-f) across
/// configurable shard groups for cache isolation and load balancing.
/// </summary>
public static class ShardRoutingUtil
{
    /// <summary>
    /// All valid hex characters for UUID prefixes.
    /// </summary>
    private static readonly char[] HexChars = "0123456789abcdef".ToCharArray();

    /// <summary>
    /// Get the shard index for a given UUID based on its first hex character.
    /// Default: 4 shards (0-3→S0, 4-7→S1, 8-b→S2, c-f→S3)
    /// </summary>
    public static int GetShardIndex(Guid id, int totalShards = 4)
    {
        if (totalShards <= 0) totalShards = 1;
        if (totalShards > 16) totalShards = 16;

        // First character of UUID determines shard
        var firstChar = id.ToString()[0];
        var hexValue = GetHexValue(firstChar);
        return hexValue * totalShards / 16;
    }

    /// <summary>
    /// Check if a shard configuration handles a given ID prefix character.
    /// </summary>
    public static bool ShardHandlesPrefix(string? shardPrefixes, char idFirstChar)
    {
        if (string.IsNullOrEmpty(shardPrefixes))
            return true; // No filter = handles all

        var prefixes = ParseShardPrefixes(shardPrefixes);
        return prefixes.Contains(char.ToLower(idFirstChar));
    }

    /// <summary>
    /// Check if a shard configuration handles a given file ID.
    /// </summary>
    public static bool ShardHandlesId(string? shardPrefixes, Guid id)
    {
        if (string.IsNullOrEmpty(shardPrefixes))
            return true;

        var firstChar = id.ToString()[0];
        return ShardHandlesPrefix(shardPrefixes, firstChar);
    }

    /// <summary>
    /// Parse shard prefix configuration like "0,1,2,3" or "0-3" or "8-b".
    /// Returns the set of hex characters this shard handles.
    /// </summary>
    public static HashSet<char> ParseShardPrefixes(string config)
    {
        var result = new HashSet<char>();

        if (string.IsNullOrWhiteSpace(config))
            return result;

        var parts = config.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var trimmed = part.Trim().ToLower();

            if (trimmed.Contains('-'))
            {
                // Range like "0-3" or "a-f"
                var range = trimmed.Split('-');
                if (range.Length == 2 && range[0].Length == 1 && range[1].Length == 1)
                {
                    var start = GetHexValue(range[0][0]);
                    var end = GetHexValue(range[1][0]);

                    if (start >= 0 && end >= 0)
                    {
                        for (var i = Math.Min(start, end); i <= Math.Max(start, end); i++)
                        {
                            result.Add(HexChars[i]);
                        }
                    }
                }
            }
            else if (trimmed.Length == 1 && IsHexChar(trimmed[0]))
            {
                result.Add(trimmed[0]);
            }
        }

        return result;
    }

    /// <summary>
    /// Get the default prefix configuration for a shard index.
    /// Example: Shard 0 of 4 = "0-3", Shard 1 of 4 = "4-7"
    /// </summary>
    public static string GetDefaultPrefixesForShard(int shardIndex, int totalShards = 4)
    {
        if (totalShards <= 0) totalShards = 1;
        if (totalShards > 16) totalShards = 16;
        if (shardIndex < 0) shardIndex = 0;
        if (shardIndex >= totalShards) shardIndex = totalShards - 1;

        var prefixesPerShard = 16 / totalShards;
        var start = shardIndex * prefixesPerShard;
        var end = start + prefixesPerShard - 1;

        // Handle remainder for non-power-of-2 shard counts
        if (shardIndex == totalShards - 1)
        {
            end = 15; // Last shard gets remaining prefixes
        }

        return $"{HexChars[start]}-{HexChars[end]}";
    }

    /// <summary>
    /// Get the .ids path prefix structure for a file ID.
    /// Returns the path like "1/2/3/4/5/{full-guid}"
    /// </summary>
    public static string GetIdsPathForId(Guid id)
    {
        var idStr = id.ToString();
        var prefix = string.Join("/", idStr.Take(5).Select(c => c.ToString()));
        return $"{prefix}/{idStr}";
    }

    /// <summary>
    /// Convert a hex character to its numeric value (0-15).
    /// Returns -1 for invalid characters.
    /// </summary>
    private static int GetHexValue(char c)
    {
        c = char.ToLower(c);
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return -1;
    }

    /// <summary>
    /// Check if a character is a valid hex digit.
    /// </summary>
    private static bool IsHexChar(char c)
    {
        return GetHexValue(c) >= 0;
    }
}
