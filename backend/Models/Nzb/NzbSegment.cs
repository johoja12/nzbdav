namespace NzbWebDAV.Models.Nzb;

public class NzbSegment
{
    public required long Bytes { get; init; }
    public required string MessageId { get; init; }
    public int Number { get; init; }

    /// <summary>
    /// Alias for Bytes property for backwards compatibility.
    /// </summary>
    public long Size => Bytes;
}
