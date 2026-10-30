namespace Bdxl;

/// <summary>A file found on an already burned disc by 'scandisc'. Sha256 is the hash of the whole file, also for a part.</summary>
public record DiscFile(string Path, long Size, DateTime Mtime, string? Sha256, bool Unverified, bool Encrypted = false, string? StoredSha256 = null)
{
    public ManifestLine? SplitLine { get; init; }   // set when the manifest says this is one part of a split file
    public string? Source { get; init; }            // catalog path from the manifest when the disc hides file names
    public int Compression { get; init; }           // zstd level of the stored copy (from the manifest)
}
