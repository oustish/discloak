namespace Bdxl;

/// <summary>disc.json on a disc: lets the disc identify itself.</summary>
public record DiscInfo(string Label, DateTime CreatedUtc, int FileCount, long UsedBytes);
