namespace Discloak;

/// <summary>
/// Deleting files so that their content is not left on the disk for an undelete tool ('catalog import' wipes the
/// plain export): one pass of random bytes over the whole file, flushed to the device, then a random name (the
/// folder entry keeps nothing telling), length 0, and deleted. More passes add nothing on today's drives. What this
/// cannot reach: an SSD may keep the old flash blocks until it reclaims them, and shadow copies, synced folders,
/// NAS snapshots and editors' own backups keep copies of their own.
/// </summary>
public static class SecureDelete
{
    /// <summary>Why the files of `dir` should not be wiped in one go, or null: only plain files, no subfolders or links.</summary>
    public static string? Unsafe(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists) return $"{dir} does not exist";
        if (Path.GetPathRoot(info.FullName) == info.FullName) return $"{dir} is the root of a drive";
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return $"{dir} is a link to another folder";
        if (info.EnumerateDirectories().Any()) return $"{dir} has subfolders (an export has none)";
        if (info.EnumerateFiles().FirstOrDefault(f => f.Attributes.HasFlag(FileAttributes.ReparsePoint)) is { } link)
            return $"{link.Name} is a link (wiping it would overwrite what it points to)";
        return null;
    }

    /// <summary>Wipes every file in dir (see Unsafe first), then removes dir. Returns the files that could not be wiped, with why.</summary>
    public static List<string> Folder(string dir)
    {
        var failed = new List<string>();
        foreach (string path in Directory.GetFiles(dir))
        {
            try { Erase(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (failed.Count == 0) Directory.Delete(dir);
        return failed;
    }

    /// <summary>Overwrites the file and deletes it.</summary>
    public static void Erase(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        Overwrite(path);
        string renamed = Path.Combine(Path.GetDirectoryName(path)!, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
        File.Move(path, renamed);
        using (var empty = new FileStream(renamed, FileMode.Truncate, FileAccess.Write, FileShare.None)) { }
        File.Delete(renamed);
    }

    /// <summary>Random bytes over the whole file (its length stays), written through to the device.</summary>
    public static void Overwrite(string path)
    {
        using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        var buffer = new byte[1 << 20];
        for (long left = output.Length; left > 0;)
        {
            int n = (int)Math.Min(buffer.Length, left);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, n));
            output.Write(buffer, 0, n);
            left -= n;
        }
        output.Flush(flushToDisk: true);
    }
}
