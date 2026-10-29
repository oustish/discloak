namespace Bdxl;

/// <summary>
/// Files every staged disc carries so it can describe itself, and writing them onto a mounted disc.
/// An encrypted disc never gets a readable manifest: its manifest (the only place, besides the catalog, that links
/// the hidden file names to real paths) is stored as manifest.jsonl.enc, encrypted with the disc's password.
/// </summary>
public static class DiscFiles
{
    public const string InfoName = "disc.json";
    public const string ManifestName = "manifest.jsonl";
    public const string EncryptedManifestName = ManifestName + FileCrypto.Suffix;

    /// <summary>Stored name of the n-th file on an encrypted disc: says nothing about the file (1000 per folder).</summary>
    public static string HiddenName(int n) =>
        $"data/{n / 1000:D3}/{n:D6}{FileCrypto.Suffix}";

    /// <summary>n of a HiddenName, or 0 for any other path.</summary>
    public static int HiddenNumber(string discPath)
    {
        var m = Regex.Match(discPath, @"^data/\d{3}/(\d{6})\.enc$", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Writes manifest + disc.json into a staged disc folder. keys/salt are required for an encrypted disc.</summary>
    public static void Write(string dir, DiscRecord disc, List<ManifestLine> manifest, KeyCache? keys = null, byte[]? salt = null)
    {
        WriteManifest(dir, disc, manifest, keys, salt, direct: false);
        JsonStore.WriteObject(Path.Combine(dir, InfoName), DiscInfo.For(disc));
    }

    static void WriteManifest(string dir, DiscRecord disc, List<ManifestLine> manifest, KeyCache? keys, byte[]? salt, bool direct)
    {
        if (!disc.Encrypted)
        {
            if (direct) File.WriteAllBytes(Path.Combine(dir, ManifestName), JsonStore.LinesToBytes(manifest));
            else JsonStore.WriteLines(Path.Combine(dir, ManifestName), manifest);
            return;
        }
        if (keys == null || salt == null)
            throw new InvalidOperationException($"{disc.Label} is encrypted; its manifest must not be written without the password.");
        File.WriteAllBytes(Path.Combine(dir, EncryptedManifestName), FileCrypto.EncryptBytes(JsonStore.LinesToBytes(manifest), keys, salt));
    }

    public static bool HasManifest(string dir) =>
        File.Exists(Path.Combine(dir, ManifestName)) || File.Exists(Path.Combine(dir, EncryptedManifestName));

    /// <summary>
    /// Writes disc.json (and the manifest if the disc has none) straight onto a mounted disc, if it can still be
    /// written to. For an encrypted disc the manifest is only written when keys are given (otherwise it is skipped).
    /// </summary>
    public static bool TryWriteDiscJson(string mount, DiscRecord disc, List<ManifestLine> manifest, bool overwrite,
                                        KeyCache? keys, byte[]? salt, out string why)
    {
        string infoPath = Path.Combine(mount, InfoName);
        if (File.Exists(infoPath) && !overwrite) { why = "disc.json is already there (use --overwrite to replace it)"; return false; }

        var drive = FindDrive(mount);
        try
        {
            if (drive != null && drive.AvailableFreeSpace < (1L << 20)) { why = "the disc has no free space left (it looks finalized)"; return false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        try
        {
            JsonStore.WriteObjectDirect(infoPath, DiscInfo.For(disc));
            if (!HasManifest(mount) && manifest.Count > 0 && (!disc.Encrypted || keys != null))
                WriteManifest(mount, disc, manifest, keys, salt, direct: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = "the disc cannot be written to (" + ex.Message + ")";
            return false;
        }
        why = "";
        return true;
    }

    /// <summary>
    /// Reads a disc's manifest; an encrypted one asks for the password (keys = the password that opened it).
    /// null = the disc has none.
    /// </summary>
    public static List<ManifestLine>? ReadManifest(string mount, Options opt, out KeyCache? keys)
    {
        keys = null;
        string plainPath = Path.Combine(mount, ManifestName), encPath = Path.Combine(mount, EncryptedManifestName);
        if (!File.Exists(encPath)) return File.Exists(plainPath) ? JsonStore.ReadLines<ManifestLine>(plainPath) : null;

        keys = new KeyCache(Ui.GetPassword(opt, forNewEncryption: false));
        byte[] plain;
        try { plain = FileCrypto.DecryptBytes(File.ReadAllBytes(encPath), keys); }
        catch (InvalidDataException ex)
        {
            throw new UserException($"Cannot read {EncryptedManifestName}: {ex.Message}.");
        }
        using var reader = new StringReader(Encoding.UTF8.GetString(plain));
        return JsonStore.ParseLines<ManifestLine>(ReadAllLines(reader), EncryptedManifestName);
    }

    static IEnumerable<string> ReadAllLines(TextReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
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
