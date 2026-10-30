namespace Bdxl;

/// <summary>
/// disc.json on a disc: lets the disc identify itself. Always readable (also on encrypted discs, where it says so),
/// so 'restore' can recognise a disc before anything is decrypted. Holds no file names. Sessions is only written
/// for a disc that 'stage --append' added to; CatalogId/KeyId only for an encrypted disc (whose bdxl.key they name).
/// </summary>
public record DiscInfo(string Label, DateTime CreatedUtc, int FileCount, long UsedBytes,
                       [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Encrypted = false,
                       [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Sessions = 0,
                       string? CatalogId = null, string? KeyId = null)
{
    public static DiscInfo For(DiscRecord d, string? catalogId = null) =>
        new(d.Label, d.CreatedUtc, d.FileCount, d.UsedBytes, d.Encrypted, d.Sessions > 1 ? d.Sessions : 0,
            d.Encrypted ? catalogId : null, d.Encrypted ? d.KeyId : null);
}
