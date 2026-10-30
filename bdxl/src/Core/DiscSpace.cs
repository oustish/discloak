namespace Bdxl;

/// <summary>
/// Space a file takes on a disc, not just its bytes: optical discs (UDF) store data in 2 KiB sectors, so every file
/// ends on a sector boundary, and every file also has a file entry (one sector) and an entry in its folder's list.
/// For a disc of large files this is negligible; for 100,000 small files it is ~300 MB, which a plan that only
/// counted bytes would not leave room for. The fixed overhead of the file system itself (volume descriptors,
/// metadata partition) is what the blank discs' reserve is for.
/// </summary>
public static class DiscSpace
{
    public const int Sector = 2048;
    public const int FolderEntry = 64;   // a UDF file identifier (38 bytes + the name), rounded generously

    public static long RoundUp(long bytes) => (bytes + Sector - 1) / Sector * Sector;

    /// <summary>Space one file of this size takes, its folder-list entry included (for planning).</summary>
    public static long OnDisc(long bytes) => RoundUp(bytes) + Sector + FolderEntry;

    /// <summary>
    /// Bytes the piece's line takes in the disc's manifest (manifest.jsonl.enc lists every stored file with its path and
    /// hashes, ~300 bytes each: a disc of a million tiny files has a 300 MB manifest). Measured on a line with the
    /// longest values a field can have, plus the file's own path - so never less than the real line.
    /// </summary>
    public static long ManifestShare(Piece p) =>
        (p.IsPart ? ManifestPartLine : ManifestWholeLine) + Encoding.UTF8.GetByteCount(p.File.RootName) + 1 +
        Encoding.UTF8.GetByteCount(p.File.RelPath) + p.File.RelPath.Count(ch => ch is '"' or '\\');

    static readonly long ManifestWholeLine = SampleLine(part: false), ManifestPartLine = SampleLine(part: true);

    static long SampleLine(bool part)
    {
        string hash = new('F', 64);
        var line = new ManifestLine("data/9999/9999999.enc", long.MaxValue, hash, true, hash,
            part ? 99_999 : null, part ? 99_999 : null, part ? long.MaxValue : null, part ? long.MaxValue : null,
            part ? long.MaxValue : null, part ? hash : null, "", 22);
        return JsonStore.LinesToBytes([line]).Length + 1;   // + encryption: 21 bytes per MiB of manifest
    }

    /// <summary>Space a whole folder takes when burned: every file and sub-folder with its entries.</summary>
    public static long Folder(string dir)
    {
        var info = new DirectoryInfo(dir);
        long total = 0;
        int entries = 0;
        foreach (var e in info.EnumerateFileSystemInfos())
        {
            entries++;
            total += e is FileInfo f ? RoundUp(f.Length) + Sector : Folder(e.FullName);
        }
        return total + Sector + RoundUp((long)entries * FolderEntry);   // the folder's own entry and its list
    }
}
