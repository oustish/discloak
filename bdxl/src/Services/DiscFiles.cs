namespace Bdxl;

/// <summary>
/// The files a disc carries besides its packs (see DiscFormat), so it can be read with nothing but itself and the
/// password:
///   manifest.enc        the stored copies of the first session: where each lies in the data stream, its real path
///                       and hashes - encrypted, and padded to whole packs of data so its size says nothing either.
///                       A session appended later ('stage --append') adds manifest.sN.enc listing only its own copies.
///   packs.sha256        SHA-256 of every pack (sha256sum format): checks the disc without bdxl and without the password
///   disc.json           label, date, sessions, format and key id - nothing about the files
///   bdxl.key            the master key wrapped by the password of the day (and the recovery key, if there was one)
///   README.txt          the formats
/// </summary>
public static class DiscFiles
{
    public const string InfoName = "disc.json";
    public const string ManifestName = "manifest.enc";
    public const string PackListName = "packs.sha256";
    public const string ReadmeName = "README.txt";
    static readonly Regex SessionManifest = new(@"^manifest\.s(\d+)\.enc$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex PackListLine = new(@"^([0-9a-fA-F]{64}) [ *]data/\d{3}/(\d{6})\.pack$", RegexOptions.CultureInvariant);

    /// <summary>The manifest name of a session: manifest.enc for the first, manifest.sN.enc for an appended one.</summary>
    public static string ManifestFor(int session) => session <= 1 ? ManifestName : $"manifest.s{session}.enc";

    /// <summary>A disc's manifests, in session order (full paths).</summary>
    public static List<string> Manifests(string mount)
    {
        var list = new List<(int Session, string Path)>();
        if (!Directory.Exists(mount)) return [];
        foreach (string path in Directory.EnumerateFiles(mount))
        {
            string name = Path.GetFileName(path);
            if (name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)) list.Add((1, path));
            else if (SessionManifest.Match(name) is { Success: true } m) list.Add((int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), path));
        }
        return list.OrderBy(x => x.Session).Select(x => x.Path).ToList();
    }

    /// <summary>
    /// Writes a staged session's metadata into its folder: its manifest (lines = its own copies), packs.sha256 (every
    /// pack of the disc, so the last session's copy is complete), disc.json, bdxl.key and README.txt.
    /// </summary>
    public static void Write(string dir, DiscRecord disc, IEnumerable<ManifestLine> lines, MasterKey key, string catalogDir, int session)
    {
        File.WriteAllBytes(Path.Combine(dir, ManifestFor(session)), SealManifest(lines, key));
        File.WriteAllLines(Path.Combine(dir, PackListName), disc.Packs.Select((sha, i) => $"{sha.ToLowerInvariant()} *{DiscFormat.PackPath(i + 1)}"));
        File.Copy(KeyFile.PathIn(catalogDir), KeyFile.PathIn(dir), overwrite: true);
        File.WriteAllText(Path.Combine(dir, ReadmeName), Readme);
        JsonStore.WriteObject(Path.Combine(dir, InfoName), DiscInfo.For(disc, key.CatalogId));
    }

    /// <summary>The manifest's JSON lines padded with empty lines to whole packs of data, encrypted.</summary>
    public static byte[] SealManifest(IEnumerable<ManifestLine> lines, MasterKey key)
    {
        byte[] text = JsonStore.LinesToBytes(lines);
        var padded = new byte[DiscFormat.PaddedManifestSize(text.Length)];
        text.CopyTo(padded, 0);
        padded.AsSpan(text.Length).Fill((byte)'\n');
        return FileCrypto.Seal(padded, key, KeyPurpose.Manifest);
    }

    /// <summary>The space a session's manifest takes on the disc.</summary>
    public static long ManifestSpace(IEnumerable<ManifestLine> lines) => DiscFormat.ManifestOnDisc(JsonStore.LinesToBytes(lines).Length);

    /// <summary>
    /// Reads a disc's manifests (every session's, in session order) with the catalog's keys. null = the disc has none.
    /// A disc encrypted with another catalog's key is explained, with the way to read it.
    /// </summary>
    public static List<ManifestLine>? ReadManifest(string mount, KeyRing keys)
    {
        var manifests = Manifests(mount);
        if (manifests.Count == 0) return null;
        var lines = new List<ManifestLine>();
        var at = new Dictionary<long, int>();   // position in the stream -> index in lines (a later line replaces an earlier one)
        foreach (string path in manifests)
        {
            string name = Path.GetFileName(path);
            byte[] plain;
            try { plain = FileCrypto.Open(File.ReadAllBytes(path), keys, KeyPurpose.Manifest); }
            catch (InvalidDataException ex)
            {
                throw new UserException($"Cannot read {name}: {ex.Message}." + (ex.Message.Contains("another key") ? " " + OtherCatalogHint(mount) : ""));
            }
            foreach (var line in JsonStore.ParseLines<ManifestLine>(plain, name))
            {
                if (at.TryGetValue(line.At, out int i)) lines[i] = line;
                else { at[line.At] = lines.Count; lines.Add(line); }
            }
        }
        return lines;
    }

    /// <summary>packs.sha256 of a disc: pack number -> SHA-256 (empty if it has none).</summary>
    public static Dictionary<int, string> ReadPackList(string mount)
    {
        var result = new Dictionary<int, string>();
        string path = Path.Combine(mount, PackListName);
        if (!File.Exists(path)) return result;
        foreach (string line in File.ReadLines(path))
            if (PackListLine.Match(line.Trim()) is { Success: true } m)
                result[int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)] = m.Groups[1].Value.ToUpperInvariant();
        return result;
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

    /// <summary>README.txt of a disc: enough to write a decryptor without bdxl.</summary>
    public const string Readme = """
        BDXL disc (encrypted) - how to read it
        =======================================

        disc.json             label, date, sessions, format ("bdxl-pack-1"), packSize and chunkSize; catalogId + keyId
                              name the key in bdxl.key (plain JSON). Nothing about the files.
        bdxl.key              the master key, wrapped (plain JSON, safe to keep openly)
        packs.sha256          SHA-256 of every pack: check the disc with any sha256sum tool, no password needed
        data/NNN/NNNNNN.pack  the data: every file, laid end to end in one stream, cut into packs of equal size
                              (64 MiB of data each), each pack encrypted on its own
        manifest.enc          which file lies where in that stream: one JSON object per line, encrypted
        manifest.sN.enc       (a disc with several sessions) the files session N added - read every manifest
        _catalog/             (optional) a backup of the whole catalog, see its own README.txt

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
           Every pack and .enc file names its key by the key id in its header (below), so take that key from the ring.
           Envelope "kind": "recovery" (if present): wrapping key = HKDF-SHA256 (RFC 5869) of the 32-byte recovery key,
           salt "salt", info "bdxl recovery key", 32 bytes; the rest as above with ...|recovery. The recovery key is
           written as Crockford base32 (34 bytes: the key + the first 2 bytes of its SHA-256), dashes ignored.
           (byte arrays in bdxl.key are base64)

        2. A pack (data/NNN/NNNNNN.pack, pack number n = the file name, from 1):
             "BDXLPAK1" (8 bytes) | key id (8) | salt (32) | nonce prefix (8) | 64 chunks of: ciphertext (1 MiB) | tag (16)
           pack key   = HKDF-SHA256 (master key, salt = the pack's salt, info = "bdxl-enc2 pack"), 32 bytes
           chunk c (from 0): AES-256-GCM with nonce = prefix (8) + c (uint32 LE), associated data = n (uint32 LE) + c (uint32 LE)
           The data stream is the plaintext of pack 1, then pack 2, ...: byte X of it is in pack X / 64 MiB + 1.

        3. manifest*.enc (format BDXLENC2):
             "BDXLENC2" (8 bytes) | key id (8) | salt (32) | nonce prefix (8) | chunks...
             chunk: length L (uint32, little endian) | ciphertext (L bytes) | tag (16)
           key        = HKDF-SHA256 (master key, salt, info = "bdxl-enc2 manifest"); "bdxl-enc2 catalog" for _catalog/*.enc
           chunk n (from 0): AES-256-GCM with nonce = prefix (8) + n (uint32 LE), associated data = n (uint32 LE) +
                        1 byte: 1 for the last chunk of the file, else 0
           plaintext of a chunk = 1 mode byte + data; mode 0 = data as it is, 1 = zstd-compressed (RFC 8878)
           The plaintext is JSON lines, one per stored file, then empty lines (padding):
             "source"      the file's real path (alias/relative path)
             "at", "size"  where its stored bytes are in the data stream, and how many
             "plainSize", "sha256"   size and SHA-256 of the original content
             "compression" if present: the stored bytes are zstd frames (RFC 8878; 'zstd -d' reads them)
             "modified"    the file's time when it was staged
           A file split across discs: "part"/"parts"/"offset" (where the part belongs), "fileSize"/"fileSha256" (the
           whole file); join the parts in order.
        """;
}
