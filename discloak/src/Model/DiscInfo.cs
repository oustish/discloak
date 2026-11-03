namespace Discloak;

/// <summary>
/// disc.json on a disc: lets the disc identify itself before anything is decrypted ('restore', 'verify', 'scandisc').
/// Holds nothing about the files - not even how many there are: that is in the encrypted manifest. Format, PackSize and
/// ChunkSize describe the packs; CatalogId and KeyId name the key in the disc's discloak.key. CreatedBy = the discloak build that
/// wrote it (its latest session): "discloak 0.8.0+3f2a1c9.20261104T1200Z" ("bdxl 0.x.y+…" from before 0.8.0; none on discs staged before 0.6.0).
/// </summary>
public record DiscInfo(string Label, DateTime CreatedUtc, string Format, int Sessions, string? CatalogId, string? KeyId,
                       long PackSize, int ChunkSize, string? CreatedBy = null)
{
    public static DiscInfo For(DiscRecord d, string catalogId) =>
        new(d.Label, d.CreatedUtc, DiscFormat.Name, d.Sessions, catalogId, d.KeyId, DiscFormat.PackData, DiscFormat.ChunkSize, BuildInfo.CreatedBy);

    /// <summary>Why this program cannot read the disc's packs, or null if it can.</summary>
    public string? Unreadable() =>
        !DiscFormat.IsName(Format) ? $"it is not a disc this version of discloak reads (format '{Format}', expected '{DiscFormat.Name}')"
        : PackSize != DiscFormat.PackData || ChunkSize != DiscFormat.ChunkSize ? $"its packs are {PackSize:N0} bytes in chunks of {ChunkSize:N0}; this discloak reads {DiscFormat.PackData:N0} in chunks of {DiscFormat.ChunkSize:N0}"
        : null;
}
