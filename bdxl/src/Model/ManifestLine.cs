namespace Bdxl;

/// <summary>
/// One line of manifest.jsonl on a disc. For encrypted files Sha256/Size describe the stored file. A part of a split
/// file also says which part it is, where it belongs in the whole file, and the whole file's size and hash.
/// Source is the file's catalog path (alias/relative path) when the disc stores it under another name: a hidden name
/// (encrypted discs, whose manifest is itself stored encrypted as manifest.jsonl.enc), or name~2.ext for a newer
/// version appended to a disc that holds the older one under the plain name.
/// </summary>
public record ManifestLine(string Path, long Size, string Sha256, bool Encrypted = false, string? PlainSha256 = null,
                           int? Part = null, int? Parts = null, long? Offset = null, long? Length = null,
                           long? FileSize = null, string? FileSha256 = null, string? Source = null)
{
    public static ManifestLine For(BackupRef b, long storedSize, FileRecord f) =>
        new(b.DiscPath, storedSize, b.Encrypted ? b.StoredSha256 ?? "" : b.ContentSha256, b.Encrypted,
            b.Encrypted ? b.ContentSha256 : null,
            b.IsPart ? b.Part : null, b.IsPart ? b.Parts : null, b.IsPart ? b.Offset : null, b.IsPart ? b.Length : null,
            b.IsPart ? f.Size : null, b.IsPart ? b.Sha256 : null,
            b.Encrypted || b.DiscPath != NaturalName(b, f) ? $"{f.RootName}/{f.RelPath}" : null);

    /// <summary>The same, with the stored size from the catalog.</summary>
    public static ManifestLine For(BackupRef b, FileRecord f) =>
        For(b, b.StoredSize ?? EstimatedStoredSize(b, f), f);

    /// <summary>The name a copy gets on a disc unless it is hidden or renamed: alias/path (+ part suffix).</summary>
    static string NaturalName(BackupRef b, FileRecord f) =>
        $"{f.RootName}/{f.RelPath}" + (b.IsPart ? SplitLayout.Suffix(b.Part, b.Parts) : "");

    // copies recorded before StoredSize existed; right unless the copy holds an older version of the file
    static long EstimatedStoredSize(BackupRef b, FileRecord f)
    {
        long plain = b.IsPart ? b.Length : f.Size;
        return b.Encrypted ? FileCrypto.StoredSize(plain) : plain;
    }
}
