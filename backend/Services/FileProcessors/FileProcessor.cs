using NzbWebDAV.Clients;
using NzbWebDAV.Extensions;
using Usenet.Nzb;

namespace NzbWebDAV.Services.FileProcessors;

public class FileProcessor(NzbFile nzbFile, string filename, UsenetStreamingClient usenet, CancellationToken ct) : BaseProcessor
{
    public static bool CanProcess(string filename)
    {
        // skip par2 files
        return !filename.EndsWith(".par2", StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<BaseProcessor.Result> ProcessAsync()
    {
        var firstSegment = nzbFile.Segments[0].MessageId.Value;
        var header = await usenet.GetSegmentYencHeaderAsync(firstSegment, default);

        return new Result()
        {
            NzbFile = nzbFile,
            FileName = filename,
            FileSize = header.FileSize,
        };
    }

    public new class Result : BaseProcessor.Result
    {
        public NzbFile NzbFile { get; init; } = null!;
        public string FileName { get; init; } = null!;
        public long FileSize { get; init; }
    }
}