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
