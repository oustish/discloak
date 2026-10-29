namespace Bdxl;

/// <summary>Files every staged disc carries so it can describe itself, and writing them onto a mounted disc.</summary>
public static class DiscFiles
{
    public static void Write(string dir, DiscRecord disc, List<ManifestLine> manifest)
    {
        JsonStore.WriteLines(Path.Combine(dir, "manifest.jsonl"), manifest);
        JsonStore.WriteObject(Path.Combine(dir, "disc.json"), new DiscInfo(disc.Label, disc.CreatedUtc, disc.FileCount, disc.UsedBytes));
    }

    /// <summary>Writes disc.json (and manifest.jsonl if missing) straight onto a mounted disc, if it can still be written to.</summary>
    public static bool TryWriteDiscJson(string mount, DiscRecord disc, List<ManifestLine> manifest, bool overwrite, out string why)
    {
        string infoPath = Path.Combine(mount, "disc.json");
        if (File.Exists(infoPath) && !overwrite) { why = "disc.json is already there (use --overwrite to replace it)"; return false; }

        var drive = FindDrive(mount);
        try
        {
            if (drive != null && drive.AvailableFreeSpace < (1L << 20)) { why = "the disc has no free space left (it looks finalized)"; return false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        try
        {
            JsonStore.WriteObjectDirect(infoPath, new DiscInfo(disc.Label, disc.CreatedUtc, disc.FileCount, disc.UsedBytes));
            string manifestPath = Path.Combine(mount, "manifest.jsonl");
            if (!File.Exists(manifestPath) && manifest.Count > 0)
                File.WriteAllLines(manifestPath, manifest.Select(m => JsonSerializer.Serialize(m, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = "the disc cannot be written to (" + ex.Message + ")";
            return false;
        }
        why = "";
        return true;
    }

    /// <summary>The drive that holds path (longest matching root), or null.</summary>
    public static DriveInfo? FindDrive(string path)
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => path.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
