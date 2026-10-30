namespace Bdxl;

public enum IntegrityState
{
    Fresh,    // nothing there yet
    Ok,       // every catalog file matches the recorded hash, and the record its HMAC
    Broken    // a file is missing, damaged, altered, from another save or another key, or unlisted
}

public sealed class IntegrityReport
{
    public IntegrityState State { get; set; } = IntegrityState.Fresh;
    public List<string> Problems { get; } = new();
    public DateTime? RecordedUtc { get; set; }
}

/// <summary>catalog.hash.json: SHA-256 of each stored (encrypted) catalog file, and an HMAC over all of it.</summary>
public sealed class IntegrityManifest
{
    public DateTime CreatedUtc { get; set; }
    public string? KeyId { get; set; }
    public long Generation { get; set; }                                  // save counter
    public Dictionary<string, string> Files { get; set; } = new();       // stored file name -> SHA-256
    public string? Mac { get; set; }                                      // HMAC-SHA256 (key from the master key)
    public bool? Unverified { get; set; }                                 // a plaintext snapshot that was already damaged when it was encrypted

    public string ComputeMac(MasterKey key)
    {
        var text = new StringBuilder($"bdxl-catalog-integrity-1\n{KeyId}\n{Generation}\n{CreatedUtc:O}\n{Unverified == true}\n");
        foreach (var (name, hash) in Files.OrderBy(e => e.Key, StringComparer.Ordinal)) text.Append(name).Append('=').Append(hash).Append('\n');
        byte[] macKey = key.IntegrityKey();
        try { return Convert.ToHexString(HMACSHA256.HashData(macKey, Encoding.UTF8.GetBytes(text.ToString()))); }
        finally { CryptographicOperations.ZeroMemory(macKey); }
    }
}

/// <summary>What a catalog folder holds.</summary>
public enum CatalogKind
{
    None,        // no catalog
    Plain,       // a catalog from before encryption: to be migrated
    Encrypted
}

/// <summary>
/// The catalog: files, discs, blanks and settings, each stored encrypted with the master key (BDXLENC2, purpose
/// "catalog") as &lt;name&gt;.enc in one folder, with bdxl.key next to them (see ENCRYPTION.md).
///  - integrity: catalog.hash.json holds a SHA-256 for each stored file and an HMAC over the list; checked on every load;
///  - versioning: before the first change a command makes, the previous state is copied to history/ (once per command).
/// The plaintext never touches a disk.
/// </summary>
public sealed class Catalog
{
    /// <summary>The catalog's parts by their plaintext names (export/import, old plaintext catalogs).</summary>
    public static readonly string[] CoveredFiles = { "files.jsonl", "discs.jsonl", "blanks.jsonl", "settings.json" };
    public const string Suffix = ".enc";
    public static readonly string[] StoredFiles = CoveredFiles.Select(n => n + Suffix).ToArray();
    public const string IntegrityFile = "catalog.hash.json";
    public const string HistoryFolder = "history";

    public string Dir { get; private set; } = "";
    public string CommandName { get; private set; } = "";
    public MasterKey? Key { get; private set; }   // null only for a catalog that is never saved (tests)
    public List<FileRecord> Files { get; private set; } = new();
    public List<DiscRecord> Discs { get; private set; } = new();
    public List<BlankDisc> Blanks { get; private set; } = new();
    public Settings Settings { get; private set; } = new();
    public IntegrityReport Integrity { get; private set; } = new();

    bool snapshotDone;
    long generation;

    public static Catalog CreateEmpty(string dir, string commandName, MasterKey? key = null) => new() { Dir = dir, CommandName = commandName, Key = key };

    public static CatalogKind Detect(string dir)
    {
        if (File.Exists(KeyFile.PathIn(dir))) return CatalogKind.Encrypted;
        return CoveredFiles.Any(n => File.Exists(Path.Combine(dir, n))) ? CatalogKind.Plain : CatalogKind.None;
    }

    /// <summary>Loads, verifies and decrypts the catalog. A broken catalog is returned EMPTY with State = Broken (unless ignoreIntegrity).</summary>
    public static Catalog Load(string dir, string commandName, MasterKey key, bool ignoreIntegrity = false)
    {
        var cat = new Catalog { Dir = dir, CommandName = commandName, Key = key };
        cat.Integrity = CheckIntegrity(dir, key, out var manifest);
        cat.generation = manifest?.Generation ?? 0;
        if (cat.Integrity.State == IntegrityState.Broken && !ignoreIntegrity) return cat;

        try
        {
            cat.Files = ReadLines<FileRecord>(dir, "files.jsonl", key);
            cat.Discs = ReadLines<DiscRecord>(dir, "discs.jsonl", key);
            cat.Blanks = ReadLines<BlankDisc>(dir, "blanks.jsonl", key);
            string settings = Path.Combine(dir, "settings.json" + Suffix);
            cat.Settings = File.Exists(settings)
                ? JsonStore.ParseObject<Settings>(FileCrypto.Open(File.ReadAllBytes(settings), key, KeyPurpose.Catalog), "settings.json") ?? new Settings()
                : new Settings();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            cat.Integrity.State = IntegrityState.Broken;
            cat.Integrity.Problems.Add("Cannot be read: " + ex.Message);
            cat.Files = new List<FileRecord>();
            cat.Discs = new List<DiscRecord>();
            cat.Blanks = new List<BlankDisc>();
            cat.Settings = new Settings();
        }
        return cat;
    }

    static List<T> ReadLines<T>(string dir, string name, MasterKey key)
    {
        string path = Path.Combine(dir, name + Suffix);
        if (!File.Exists(path)) return new List<T>();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var plain = new MemoryStream();
        try { FileCrypto.Decrypt(input, plain, key, KeyPurpose.Catalog); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{name}{Suffix}: {ex.Message}"); }
        plain.Position = 0;
        return JsonStore.ParseLines<T>(plain, name);
    }

    /// <summary>Used by 'catalog accept': the user vouches for the current contents.</summary>
    public void TrustCurrentContents()
    {
        Integrity = new IntegrityReport { State = IntegrityState.Ok };
    }

    /// <summary>Used by 'catalog import': everything is replaced (the previous state goes to history on Save).</summary>
    public void ReplaceContents(List<FileRecord> files, List<DiscRecord> discs, List<BlankDisc> blanks, Settings settings)
    {
        Files = files;
        Discs = discs;
        Blanks = blanks;
        Settings = settings;
    }

    // ---- integrity ------------------------------------------------------------------------

    /// <summary>
    /// Checks the stored files of a catalog (or a snapshot / backup folder) against catalog.hash.json. Damage shows
    /// without a key; with the key, the HMAC also proves the list itself was written by this catalog in one save.
    /// </summary>
    public static IntegrityReport CheckIntegrity(string dir, MasterKey? key) => CheckIntegrity(dir, key, out _);

    static IntegrityReport CheckIntegrity(string dir, MasterKey? key, out IntegrityManifest? manifest)
    {
        manifest = null;
        var report = new IntegrityReport();
        var existing = StoredFiles.Where(n => File.Exists(Path.Combine(dir, n))).ToList();
        string manifestPath = Path.Combine(dir, IntegrityFile);

        if (!File.Exists(manifestPath))
        {
            if (existing.Count == 0) return report;   // Fresh
            report.State = IntegrityState.Broken;
            report.Problems.Add($"{IntegrityFile} is missing.");
            return report;
        }

        try { manifest = JsonStore.ReadObject<IntegrityManifest>(manifestPath); }
        catch (JsonException ex)
        {
            report.State = IntegrityState.Broken;
            report.Problems.Add($"{IntegrityFile} cannot be read: {ex.Message}");
            return report;
        }
        if (manifest == null)
        {
            report.State = IntegrityState.Broken;
            report.Problems.Add($"{IntegrityFile} is empty.");
            return report;
        }

        report.RecordedUtc = manifest.CreatedUtc;
        foreach (var entry in manifest.Files.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            string path = Path.Combine(dir, entry.Key);
            if (!File.Exists(path)) { report.Problems.Add($"{entry.Key}: missing"); continue; }
            if (!Hashing.SameHash(Hashing.HashFile(path), entry.Value))
                report.Problems.Add($"{entry.Key}: contents do not match the recorded hash (damaged, or changed outside bdxl)");
        }
        var listed = manifest.Files;
        foreach (string name in existing.Where(n => !listed.ContainsKey(n)))
            report.Problems.Add($"{name}: exists but is not covered by {IntegrityFile}");
        if (manifest.Unverified == true)
            report.Problems.Add("this version was already damaged when the catalog was encrypted");

        if (key != null)
        {
            if (!string.Equals(manifest.KeyId, key.KeyId, StringComparison.OrdinalIgnoreCase))
                report.Problems.Add($"written with another key (key id {manifest.KeyId}, this catalog's is {key.KeyId})");
            else if (manifest.Mac == null || !CryptographicOperations.FixedTimeEquals(
                         Convert.FromHexString(manifest.Mac), Convert.FromHexString(manifest.ComputeMac(key))))
                report.Problems.Add($"{IntegrityFile} fails its HMAC check (altered, or files mixed from different saves)");
        }

        report.State = report.Problems.Count == 0 ? IntegrityState.Ok : IntegrityState.Broken;
        return report;
    }

    /// <summary>Writes catalog.hash.json (with its HMAC) for the stored files that are currently in the folder.</summary>
    public static void WriteIntegrity(string dir, MasterKey key, long generation, bool unverified = false)
    {
        var manifest = new IntegrityManifest
        {
            CreatedUtc = DateTime.UtcNow, KeyId = key.KeyId, Generation = generation, Unverified = unverified ? true : null
        };
        foreach (string name in StoredFiles)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) manifest.Files[name] = Hashing.HashFile(path);
        }
        manifest.Mac = manifest.ComputeMac(key);
        JsonStore.WriteObject(Path.Combine(dir, IntegrityFile), manifest);
    }

    // ---- saving + versioning --------------------------------------------------------------

    public void Save()
    {
        if (Integrity.State == IntegrityState.Broken)
            throw new InvalidOperationException("The catalog is broken; refusing to overwrite it. Run 'catalog check'.");
        if (Key == null) throw new InvalidOperationException("This catalog has no key and cannot be saved.");

        if (!snapshotDone)
        {
            CreateSnapshot(Dir, CommandName, Math.Max(1, Settings.HistoryKeep));
            snapshotDone = true;   // one copy per command, however many times it saves
        }

        using var _ = Interrupt.Critical();   // Ctrl+C waits until all files and their hashes are written
        Directory.CreateDirectory(Dir);
        WriteEncrypted("files.jsonl", ms => JsonStore.WriteLinesTo(ms,
            Files.OrderBy(f => f.Root, StringComparer.Ordinal).ThenBy(f => f.RelPath, StringComparer.Ordinal)));
        WriteEncrypted("discs.jsonl", ms => JsonStore.WriteLinesTo(ms, Discs.OrderBy(d => d.Label, StringComparer.Ordinal)));
        WriteEncrypted("blanks.jsonl", ms => JsonStore.WriteLinesTo(ms, Blanks.OrderBy(b => b.Id, StringComparer.Ordinal)));
        WriteEncrypted("settings.json", ms => ms.Write(JsonStore.ObjectToBytes(Settings)));
        WriteIntegrity(Dir, Key, ++generation);
        Integrity = new IntegrityReport { State = IntegrityState.Ok };
    }

    /// <summary>Serializes into memory and encrypts straight into &lt;name&gt;.enc (via a temp file, then renamed).</summary>
    void WriteEncrypted(string name, Action<MemoryStream> write)
    {
        using var plain = new MemoryStream();
        write(plain);
        plain.Position = 0;
        string path = Path.Combine(Dir, name + Suffix), tmp = path + ".tmp";
        using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            FileCrypto.Encrypt(plain, output, Key!, KeyPurpose.Catalog);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Copies the stored catalog files (and their hash file) to history/&lt;time&gt;-&lt;command&gt;/ and prunes old versions.</summary>
    public static string? CreateSnapshot(string dir, string commandName, int keep)
    {
        var present = StoredFiles.Where(n => File.Exists(Path.Combine(dir, n))).ToList();
        if (present.Count == 0) return null;

        string historyDir = Path.Combine(dir, HistoryFolder);
        string safeName = new string(commandName.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray());
        string baseId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}";
        string id = baseId;
        for (int n = 2; Directory.Exists(Path.Combine(historyDir, id)); n++) id = baseId + "-" + n;

        string target = Path.Combine(historyDir, id);
        Directory.CreateDirectory(target);
        foreach (string name in present) File.Copy(Path.Combine(dir, name), Path.Combine(target, name));
        string manifestPath = Path.Combine(dir, IntegrityFile);
        if (File.Exists(manifestPath)) File.Copy(manifestPath, Path.Combine(target, IntegrityFile));   // the hashes stay next to the old files

        foreach (string old in ListHistory(dir).Skip(keep))
            Directory.Delete(Path.Combine(historyDir, old), recursive: true);
        return id;
    }

    /// <summary>Snapshot ids, newest first.</summary>
    public static List<string> ListHistory(string dir)
    {
        string historyDir = Path.Combine(dir, HistoryFolder);
        if (!Directory.Exists(historyDir)) return new List<string>();
        return Directory.GetDirectories(historyDir).Select(d => Path.GetFileName(d)!)
            .OrderByDescending(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>Checks a snapshot against its own hash file (and HMAC); returns problems (empty = fine).</summary>
    public static List<string> VerifySnapshot(string dir, string id, MasterKey key)
    {
        string snap = Path.Combine(dir, HistoryFolder, id);
        if (!Directory.Exists(snap)) return new List<string> { $"No such version: {id}" };
        if (!File.Exists(Path.Combine(snap, IntegrityFile))) return new List<string> { "this version has no hash file" };
        return CheckIntegrity(snap, key).Problems;
    }

    /// <summary>
    /// Puts an older version back. The current state is snapshotted first, so a rollback can be undone. A version
    /// that failed its check (restored with --force) gets a new hash file: the user vouched for it.
    /// </summary>
    public static void RestoreSnapshot(string dir, string id, string commandName, int keep, MasterKey key)
    {
        string snap = Path.Combine(dir, HistoryFolder, id);
        bool trusted = VerifySnapshot(dir, id, key).Count == 0;
        CreateSnapshot(dir, commandName, keep + 1);

        foreach (string name in StoredFiles)
        {
            string from = Path.Combine(snap, name);
            string to = Path.Combine(dir, name);
            if (File.Exists(from)) File.Copy(from, to, overwrite: true);
            else if (File.Exists(to)) File.Delete(to);   // that version did not have this file
        }
        if (trusted) File.Copy(Path.Combine(snap, IntegrityFile), Path.Combine(dir, IntegrityFile), overwrite: true);
        else WriteIntegrity(dir, key, 0);
    }
}
