namespace Bdxl;

/// <summary>
/// disc.json on a disc: lets the disc identify itself. Always readable (also on encrypted discs, where it says so),
/// so 'restore' can recognise a disc before any password is known. Holds no file names. Sessions is only written
/// for a disc that 'stage --append' added to.
/// </summary>
public record DiscInfo(string Label, DateTime CreatedUtc, int FileCount, long UsedBytes,
                       [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Encrypted = false,
                       [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Sessions = 0)
{
    public static DiscInfo For(DiscRecord d) =>
        new(d.Label, d.CreatedUtc, d.FileCount, d.UsedBytes, d.Encrypted, d.Sessions > 1 ? d.Sessions : 0);
}
