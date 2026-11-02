namespace Bdxl;

/// <summary>
/// disc.json on a disc: lets the disc identify itself before anything is decrypted ('restore', 'verify', 'scandisc').
/// Holds nothing about the files - not even how many there are: that is in the encrypted manifest. Format, PackSize and
/// ChunkSize describe the packs; CatalogId and KeyId name the key in the disc's bdxl.key.
/// </summary>
public record DiscInfo(string Label, DateTime CreatedUtc, string Format, int Sessions, string? CatalogId, string? KeyId,
                       long PackSize, int ChunkSize)
{
    public static DiscInfo For(DiscRecord d, string catalogId) =>
        new(d.Label, d.CreatedUtc, DiscFormat.Name, d.Sessions, catalogId, d.KeyId, DiscFormat.PackData, DiscFormat.ChunkSize);

    /// <summary>Why this program cannot read the disc's packs, or null if it can.</summary>
    public string? Unreadable() =>
        Format != DiscFormat.Name ? $"it is not a disc this version of bdxl reads (format '{Format}', expected '{DiscFormat.Name}')"
        : PackSize != DiscFormat.PackData || ChunkSize != DiscFormat.ChunkSize ? $"its packs are {PackSize:N0} bytes in chunks of {ChunkSize:N0}; this bdxl reads {DiscFormat.PackData:N0} in chunks of {DiscFormat.ChunkSize:N0}"
        : null;
}
