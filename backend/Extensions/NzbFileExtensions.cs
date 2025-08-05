using System.Text.RegularExpressions;
using Usenet.Nzb;

namespace NzbWebDAV.Extensions;

public static class NzbFileExtensions
{
    public static string[] GetOrderedSegmentIds(this NzbFile file)
    {
        return file.Segments
            .OrderBy(x => x.Number)
            .Select(x => x.MessageId.Value)
            .ToArray();
    }

    public static string GetSubjectFileName(this NzbFile file)
    {
        return GetFirstValidNonEmptyFilename(
            () => TryParseFilename1(file),
            () => TryParseFilename2(file),
            () => TryParseFilename3(file),
            () => TryParseFilename4(file)
        );
    }

    private static string TryParseFilename1(this NzbFile file)
    {
        // example: `[1/8] - "file.mkv" yEnc 12345 (1/54321)`
        var match = Regex.Match(file.Subject, "\\\"(.*)\\\"");
        if (match.Success) return match.Groups[1].Value;
        return "";
    }

    private static string TryParseFilename2(this NzbFile file)
    {
        // example: `Some release [file.mkv] [release]`
        var matches = Regex.Matches(file.Subject, @"\[([^\[\]]*)\]");
        return matches
            .Select(x => x.Groups[1].Value)
            .Where(x => Path.GetExtension(x).StartsWith("."))
            .FirstOrDefault(x => Path.GetExtension(x).Length < 6) ?? "";
    }

    private static string TryParseFilename3(this NzbFile file)
    {
        // example: `file.mkv (1/0)`
        var match = Regex.Match(file.Subject, @"^(.*?\.\w{2,6})\s+\(.*\)$");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string TryParseFilename4(this NzbFile file)
    {
        // example: `file.mkv` — no spaces allowed
        var match = Regex.Match(file.Subject, @"^[^\s]+\.\w{2,6}$");
        return match.Success ? match.Value : "";
    }

    private static string GetFirstValidNonEmptyFilename(params Func<string>[] funcs)
    {
        return funcs
            .Select(x => x.Invoke())
            .Where(x => x == Path.GetFileName(x))
            .FirstOrDefault(x => x != "") ?? "";
    }
}