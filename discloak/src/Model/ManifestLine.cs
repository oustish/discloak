namespace Discloak;

/// <summary>
/// One line of a disc's encrypted manifest: a stored copy. Source = the file's catalog path (alias/relative path);
/// At/Size = where its stored form lies in the disc's data stream; PlainSize/Sha256 = what it holds (the whole file, or
/// one part of it); Compression = level and Codec = what compressed it ("zstd"; none on discs staged before 0.6.0, which
/// are zstd too) - both null: stored as it is; Modified = the file's time when it was staged.
/// A part also says which one it is, where it belongs in the whole file, and the whole file's size and hash.
/// </summary>
public record ManifestLine(string Source, long At, long Size, long PlainSize, string Sha256, DateTime? Modified = null,
                           int? Compression = null, int? Part = null, int? Parts = null, long? Offset = null,
                           long? FileSize = null, string? FileSha256 = null, string? Codec = null)
{
    public bool IsPart => Part is > 0 && Parts is > 0 && Offset != null && FileSize != null && FileSha256 != null;

    public static ManifestLine For(BackupRef b, FileRecord f) =>
        new($"{f.RootName}/{f.RelPath}", b.At, b.StoredSize, b.Length, b.ContentSha256,
            Hashing.SameHash(b.Sha256, f.Sha256) ? f.ModifiedUtc : null,
            b.Compression > 0 ? b.Compression : null,
            b.IsPart ? b.Part : null, b.IsPart ? b.Parts : null, b.IsPart ? b.Offset : null,
            b.IsPart ? f.Size : null, b.IsPart ? b.Sha256 : null, Discloak.Compression.CodecOf(b.Compression, b.Codec));
}
