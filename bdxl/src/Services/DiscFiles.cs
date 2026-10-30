namespace Bdxl;

/// <summary>
/// Files every staged disc carries so it can describe itself, and writing them onto a mounted disc.
/// An encrypted disc never gets a readable manifest: its manifest (the only place, besides the catalog, that links
/// the hidden file names to real paths) is stored as manifest.jsonl.enc, encrypted with the catalog's master key. A
/// session appended later ('stage --append') adds manifest.sN.jsonl.enc listing only its own files; a disc's list is
/// all of them read in session order. It also carries bdxl.key (the master key wrapped by the password of the day, and the recovery key if there was
/// one) and README.txt describing the formats, so the disc can be read with nothing but itself and the password.
/// </summary>
public static class DiscFiles
{
    public const string InfoName = "disc.json";
    public const string ManifestName = "manifest.jsonl";
    public const string EncryptedManifestName = ManifestName + FileCrypto.Suffix;
    public const string ReadmeName = "README.txt";
    static readonly Regex SessionManifest = new(@"^manifest\.s(\d+)\.jsonl\.enc$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The manifest name of a session: manifest.jsonl.enc for the first, manifest.sN.jsonl.enc for an appended one.</summary>
    public static string EncryptedManifestFor(int session) => session <= 1 ? EncryptedManifestName : $"manifest.s{session}.jsonl{FileCrypto.Suffix}";

    /// <summary>A disc's encrypted manifests, in session order (full paths).</summary>
    public static List<string> EncryptedManifests(string mount)
    {
        var list = new List<(int Session, string Path)>();
        if (!Directory.Exists(mount)) return [];
        foreach (string path in Directory.EnumerateFiles(mount))
        {
            string name = Path.GetFileName(path);
            if (name.Equals(EncryptedManifestName, StringComparison.OrdinalIgnoreCase)) list.Add((1, path));
            else if (SessionManifest.Match(name) is { Success: true } m) list.Add((int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), path));
        }
        return list.OrderBy(x => x.Session).Select(x => x.Path).ToList();
    }

    /// <summary>A manifest file of the disc itself (disc metadata, not data).</summary>
    public static bool IsManifestName(string name) =>
        name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || name.Equals(EncryptedManifestName, StringComparison.OrdinalIgnoreCase) ||
        SessionManifest.IsMatch(name);

    /// <summary>Stored name of the n-th file on an encrypted disc: says nothing about the file (1000 per folder).</summary>
    public static string HiddenName(int n) =>
        $"data/{n / 1000:D3}/{n:D6}{FileCrypto.Suffix}";

    /// <summary>n of a HiddenName, or 0 for any other path.</summary>
    public static int HiddenNumber(string discPath)
    {
        var m = Regex.Match(discPath, @"^data/\d{3}/(\d{6})\.enc$", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Writes manifest + disc.json (and, for an encrypted disc, bdxl.key + README.txt) into a staged disc folder.</summary>
    public static void Write(string dir, DiscRecord disc, List<ManifestLine> manifest, MasterKey key, string catalogDir)
    {
        WriteManifest(dir, disc, manifest, key, direct: false);
        if (disc.Encrypted) WriteKeyFiles(dir, catalogDir);
        JsonStore.WriteObject(Path.Combine(dir, InfoName), DiscInfo.For(disc, key.CatalogId));
    }

    static void WriteManifest(string dir, DiscRecord disc, List<ManifestLine> manifest, MasterKey? key, bool direct)
    {
        if (!disc.Encrypted)
        {
            if (direct) File.WriteAllBytes(Path.Combine(dir, ManifestName), JsonStore.LinesToBytes(manifest));
            else JsonStore.WriteLines(Path.Combine(dir, ManifestName), manifest);
            return;
        }
        if (key == null) throw new InvalidOperationException($"{disc.Label} is encrypted; its manifest must not be written without the key.");
        // an appended session lists only its own files, in its own manifest; the earlier ones stay on the disc as they are
        string name = EncryptedManifestFor(disc.PendingAppend?.Number ?? 1);
        File.WriteAllBytes(Path.Combine(dir, name), FileCrypto.Seal(JsonStore.LinesToBytes(manifest), key, KeyPurpose.Manifest));
    }

    /// <summary>bdxl.key as the catalog has it now, and README.txt.</summary>
    static void WriteKeyFiles(string dir, string catalogDir)
    {
        File.Copy(KeyFile.PathIn(catalogDir), KeyFile.PathIn(dir), overwrite: true);
        File.WriteAllText(Path.Combine(dir, ReadmeName), Readme);
    }

    public static bool HasManifest(string dir) =>
        File.Exists(Path.Combine(dir, ManifestName)) || EncryptedManifests(dir).Count > 0;

    /// <summary>
    /// Writes disc.json (and the manifest if the disc has none; bdxl.key and README.txt if an encrypted disc lacks
    /// them) straight onto a mounted disc, if it can still be written to.
    /// </summary>
    public static bool TryWriteDiscJson(string mount, DiscRecord disc, List<ManifestLine> manifest, bool overwrite,
                                        MasterKey key, string catalogDir, out string why)
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
            JsonStore.WriteObjectDirect(infoPath, DiscInfo.For(disc, key.CatalogId));
            if (!HasManifest(mount) && manifest.Count > 0) WriteManifest(mount, disc, manifest, key, direct: true);
            if (disc.Encrypted && !File.Exists(KeyFile.PathIn(mount))) WriteKeyFiles(mount, catalogDir);
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
    /// Reads a disc's manifest (an encrypted one with the catalog's master key): every session's, in session order, a
    /// later line replacing an earlier one for the same stored file (discs appended before the manifests were split
    /// list every earlier file again). null = the disc has none. A disc encrypted with another catalog's key is
    /// explained, with the way to read it.
    /// </summary>
    public static List<ManifestLine>? ReadManifest(string mount, KeyRing key)
    {
        var encrypted = EncryptedManifests(mount);
        string plainPath = Path.Combine(mount, ManifestName);
        if (encrypted.Count == 0) return File.Exists(plainPath) ? JsonStore.ReadLines<ManifestLine>(plainPath) : null;

        var lines = new List<ManifestLine>();
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // stored path -> its index in lines
        foreach (string path in encrypted)
        {
            string name = Path.GetFileName(path);
            byte[] plain;
            try { plain = FileCrypto.Open(File.ReadAllBytes(path), key, KeyPurpose.Manifest); }
            catch (InvalidDataException ex)
            {
                throw new UserException($"Cannot read {name}: {ex.Message}." +
                                        (ex.Message.Contains("another key") ? " " + OtherCatalogHint(mount) : ""));
            }
            foreach (var line in JsonStore.ParseLines<ManifestLine>(plain, name))
            {
                if (at.TryGetValue(line.Path, out int i)) lines[i] = line;
                else { at[line.Path] = lines.Count; lines.Add(line); }
            }
        }
        return lines;
    }

    /// <summary>What to do with a disc of another catalog.</summary>
    public static string OtherCatalogHint(string mount) =>
        "This disc belongs to another catalog. Use that catalog, or make one from the disc's own key: " +
        $"bdxl init --key-from \"{mount}\" --catalog <new folder>   (asks for the password the disc was staged with)";

    /// <summary>disc.json of a mounted disc, or null.</summary>
    public static DiscInfo? ReadInfo(string mount)
    {
        try { return JsonStore.ReadObject<DiscInfo>(Path.Combine(mount, InfoName)); }
        catch (JsonException) { return null; }
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

    /// <summary>README.txt of an encrypted disc: enough to write a decryptor without bdxl.</summary>
    public const string Readme = """
        BDXL disc (encrypted) - how to read it
        =======================================

        disc.json            label, file count, size; catalogId + keyId name the key in bdxl.key (plain JSON)
        bdxl.key             the master key, wrapped (plain JSON, safe to keep openly)
        manifest.jsonl.enc   the list of files: one JSON object per line, encrypted (purpose "manifest")
        manifest.sN.jsonl.enc   (a disc with several sessions) the files session N added - read every manifest, in
                             session order; a later line for the same "path" replaces an earlier one
        data/NNN/NNNNNN.enc  the files, encrypted one by one under names that say nothing (purpose "file")
        _catalog/            (optional) a backup of the whole catalog, see its own README.txt

        With bdxl:  bdxl init --key-from <this disc> --catalog <new folder>   then   bdxl scandisc <this disc>
                    and  bdxl restore <folder> "*" --mount <this disc>      (or: catalog backup restore <disc>/_catalog)

        Without bdxl, all of this is standard cryptography:

        1. The master keys (32 bytes each) from bdxl.key, envelope "kind": "password":
             wrapping key = Argon2id (RFC 9106, version 0x13) of the password (UTF-8), with "salt", parallelism 1,
                            "memoryKiB" KiB of memory, "passes" iterations, 32 bytes of output
             AES-256-GCM decryption of "wrapped" (ciphertext + the last 16 bytes: tag) with that key, nonce "nonce",
             associated data the ASCII text:
               "ring": true    bdxl-key-2|<catalogId>|<the envelope's keyId>|password   and the plaintext is a key
                               ring: for every key, oldest first, its 8-byte key id + the 32-byte key; the last one
                               is the newest (a catalog gets a new key when its key is rotated; keys are never dropped)
               otherwise       bdxl-key-1|<catalogId>|<the file's keyId>|password   and the plaintext is one key
           Every .enc file names its key by the key id in its header (below), so take that key from the ring.
           Envelope "kind": "recovery" (if present): wrapping key = HKDF-SHA256 (RFC 5869) of the 32-byte recovery key,
           salt "salt", info "bdxl recovery key", 32 bytes; the rest as above with ...|recovery. The recovery key is
           written as Crockford base32 (34 bytes: the key + the first 2 bytes of its SHA-256), dashes ignored.
           (byte arrays in bdxl.key are base64)

        2. Every .enc file (format BDXLENC2):
             "BDXLENC2" (8 bytes) | key id (8) | salt (32) | nonce prefix (8) | chunks...
             chunk: length L (uint32, little endian) | ciphertext (L bytes) | tag (16)
           file key   = HKDF-SHA256 (master key, salt = the file's salt, info = "bdxl-enc2 " + purpose), 32 bytes;
                        purpose is "file" for data/..., "manifest" for manifest*.jsonl.enc, "catalog" for _catalog/*.enc
           chunk n (from 0): AES-256-GCM with nonce = prefix (8) + n (uint32 LE), associated data = n (uint32 LE) +
                        1 byte: 1 for the last chunk of the file, else 0
           plaintext of a chunk = 1 mode byte + data; mode 0 = data as it is, 1 = zstd-compressed (RFC 8878)
           The file is the data of all chunks in order. Chunks hold up to 1 MiB of data each.

        3. manifest*.jsonl.enc lines: "path" (stored name), "source" (the file's real path: alias/relative path),
           "plainSha256" (SHA-256 of the original content), "size"/"sha256" (of the stored .enc file); split files
           also "part"/"parts"/"offset"/"length" (join the parts in order).
        """;
}
