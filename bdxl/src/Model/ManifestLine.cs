namespace Bdxl;

/// <summary>
/// One line of manifest.jsonl on a disc. For encrypted files Sha256/Size describe the stored file. A part of a split
/// file also says which part it is, where it belongs in the whole file, and the whole file's size and hash.
/// </summary>
public record ManifestLine(string Path, long Size, string Sha256, bool Encrypted = false, string? PlainSha256 = null,
                           int? Part = null, int? Parts = null, long? Offset = null, long? Length = null,
                           long? FileSize = null, string? FileSha256 = null)
{
    public static ManifestLine For(BackupRef b, long storedSize, long fileSize) =>
        new(b.DiscPath, storedSize, b.Encrypted ? b.StoredSha256 ?? "" : b.ContentSha256, b.Encrypted,
            b.Encrypted ? b.ContentSha256 : null,
            b.IsPart ? b.Part : null, b.IsPart ? b.Parts : null, b.IsPart ? b.Offset : null, b.IsPart ? b.Length : null,
            b.IsPart ? fileSize : null, b.IsPart ? b.Sha256 : null);
}
