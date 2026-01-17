using System.Text.RegularExpressions;

namespace NzbWebDAV.Utils;

/// <summary>
/// Utility for matching filenames across different naming conventions.
/// Used by Plex and Emby verification services to handle metadata-based renaming
/// where quality tags, separators, and release groups may differ.
/// </summary>
public static partial class FilenameMatchingUtil
{
    // Regex for season/episode patterns: S01E01, S1E1, 1x01, etc.
    [GeneratedRegex(@"[Ss](\d{1,2})[Ee](\d{1,2})|(\d{1,2})[Xx](\d{2})", RegexOptions.Compiled)]
    private static partial Regex SeasonEpisodeRegex();

    /// <summary>
    /// Check if two filenames match after normalization.
    /// Handles Plex/Emby metadata-based renaming where punctuation and ordering differ.
    ///
    /// Example match:
    ///   "The.Night.Manager.S02E02.1080p.WEB.h264-InChY" matches
    ///   "The.Night.Manager.-.S02E02.WEBDL-1080p.mkv"
    /// </summary>
    public static bool NormalizedMatch(string filename1, string filename2)
    {
        var normalized1 = NormalizeForMatching(filename1);
        var normalized2 = NormalizeForMatching(filename2);

        // Exact normalized match
        if (normalized1 == normalized2)
            return true;

        // Extract season/episode and title portions
        var (title1, se1) = ExtractTitleAndSeasonEpisode(filename1);
        var (title2, se2) = ExtractTitleAndSeasonEpisode(filename2);

        // If both have season/episode info, they must match
        if (!string.IsNullOrEmpty(se1) && !string.IsNullOrEmpty(se2))
        {
            if (se1 != se2)
                return false;

            // Season/episode matches, now check if titles are similar
            // Normalize titles and check if one contains the other
            var normTitle1 = NormalizeForMatching(title1);
            var normTitle2 = NormalizeForMatching(title2);

            // Titles match if one starts with or contains the other
            return normTitle1.StartsWith(normTitle2) ||
                   normTitle2.StartsWith(normTitle1) ||
                   normTitle1.Contains(normTitle2) ||
                   normTitle2.Contains(normTitle1);
        }

        return false;
    }

    /// <summary>
    /// Normalize a filename by removing all non-alphanumeric characters and converting to lowercase.
    /// </summary>
    public static string NormalizeForMatching(string filename)
    {
        // Remove all non-alphanumeric characters and convert to lowercase
        return Regex.Replace(filename, @"[^a-zA-Z0-9]", "").ToLowerInvariant();
    }

    /// <summary>
    /// Extract the title portion (before season/episode) and the normalized season/episode string.
    /// Returns (title, "s##e##") or (filename, "") if no season/episode found.
    /// </summary>
    public static (string title, string seasonEpisode) ExtractTitleAndSeasonEpisode(string filename)
    {
        var match = SeasonEpisodeRegex().Match(filename);
        if (!match.Success)
            return (filename, "");

        var title = filename[..match.Index].TrimEnd('.', '-', '_', ' ');

        // Normalize to S##E## format
        string season, episode;
        if (!string.IsNullOrEmpty(match.Groups[1].Value))
        {
            // S01E01 format
            season = match.Groups[1].Value.PadLeft(2, '0');
            episode = match.Groups[2].Value.PadLeft(2, '0');
        }
        else
        {
            // 1x01 format
            season = match.Groups[3].Value.PadLeft(2, '0');
            episode = match.Groups[4].Value.PadLeft(2, '0');
        }

        return (title, $"s{season}e{episode}");
    }
}
