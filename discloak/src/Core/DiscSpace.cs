namespace Discloak;

/// <summary>
/// Space files take on a disc, not just their bytes: optical discs (UDF) store data in 2 KiB sectors, so every file
/// ends on a sector boundary, and every file also has a file entry (one sector) and an entry in its folder's list.
/// A discloak disc holds few, large files (its packs, see DiscFormat), so for it this is a fixed, small cost per pack;
/// the fixed overhead of the file system itself (volume descriptors, metadata partition) is what the blank discs'
/// reserve is for.
/// </summary>
public static class DiscSpace
{
    public const int Sector = 2048;
    public const int FolderEntry = 64;   // a UDF file identifier (38 bytes + the name), rounded generously

    public static long RoundUp(long bytes) => (bytes + Sector - 1) / Sector * Sector;

    /// <summary>Space one file of this size takes, its folder-list entry included (for planning).</summary>
    public static long OnDisc(long bytes) => RoundUp(bytes) + Sector + FolderEntry;

    /// <summary>
    /// Bytes the piece's line takes in the disc's manifest (it lists every stored copy with its path and hashes, ~300
    /// bytes each). Measured on a line with the longest values a field can have, plus the file's own path - so never
    /// less than the real line.
    /// </summary>
    public static long ManifestShare(Piece p) =>
        (p.IsPart ? ManifestPartLine : ManifestWholeLine) + Encoding.UTF8.GetByteCount(p.File.RootName) + 1 +
        Encoding.UTF8.GetByteCount(p.File.RelPath) + p.File.RelPath.Count(ch => ch is '"' or '\\');

    static readonly long ManifestWholeLine = SampleLine(part: false), ManifestPartLine = SampleLine(part: true);

    static long SampleLine(bool part)
    {
        string hash = new('F', 64);
        var line = new ManifestLine("", long.MaxValue, long.MaxValue, long.MaxValue, hash, DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), 22,
            part ? 99_999 : null, part ? 99_999 : null, part ? long.MaxValue : null, part ? long.MaxValue : null, part ? hash : null);
        return JsonStore.LinesToBytes([line]).Length + 6 + 1;   // + a time zone offset instead of "Z"; + encryption: 21 bytes per MiB
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
