namespace Discloak;

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
        // "bdxl-catalog-integrity-1": a label from before the program was renamed to Discloak (0.8.0), kept on purpose. It
        // is part of what the HMAC covers, not a name anyone sees: every catalog's hash list was signed with it, and
        // changing it would make every existing catalog fail its integrity check.
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
/// The catalog: files, discs, blanks and settings, each stored encrypted with the current master key (BDXLENC2, purpose
/// "catalog"; read with whichever key of the ring wrote it) as &lt;name&gt;.enc in one folder, with discloak.key next to them (see ENCRYPTION.md).
///  - integrity: catalog.hash.json holds a SHA-256 for each stored file and an HMAC over the list; checked on every load;
///  - versioning: before the first change a command makes, the previous state is copied to history/ (once per command).
/// The plaintext never touches a disk.
/// </summary>
public sealed class Catalog
{
    /// <summary>The catalog's parts by their plaintext names (export/import, old plaintext catalogs).</summary>
    public static readonly string[] CoveredFiles = { "files.jsonl", "discs.jsonl", "blanks.jsonl", "settings.json" };
    public const string Suffix = ".enc";
    public static readonly string[] StoredFiles = CoveredFiles.Select(n => n + Suffix).ToArray();   // files.jsonl.enc: before the file list was split
    public const int CompressionLevel = 3;                // zstd, for every stored catalog file (JSON shrinks ~3x)
    static readonly Regex ShardName = new(@"^files\.(\d{3,})\.jsonl\.enc$", RegexOptions.CultureInvariant);

    /// <summary>
    /// files.jsonl.enc held the whole file list before it was split. It now holds only this line, so that an older discloak,
    /// which would see an empty file list (and could save it so), finds it unreadable and refuses to work instead.
    /// </summary>
    const string UnsplitFiles = "files.jsonl" + Suffix;
    // It says "bdxl" on purpose: it is written for the old versions, which were called bdxl before 0.8.0.
    static ReadOnlySpan<byte> TooOldMarker => "bdxl: the file list is in files.NNN.jsonl.enc - this version of bdxl is too old for this catalog."u8;

    /// <summary>A stored catalog file: one of the fixed ones, or a part of the file list (files.NNN.jsonl.enc).</summary>
    public static bool IsStoredName(string name) => StoredFiles.Contains(name, StringComparer.Ordinal) || ShardName.IsMatch(name);

    /// <summary>The stored catalog files in dir (a catalog, a snapshot, a backup), in name order.</summary>
    public static List<string> StoredFilesIn(string dir) =>
        !Directory.Exists(dir) ? new List<string>()
            : Directory.EnumerateFiles(dir).Select(p => Path.GetFileName(p)).Where(IsStoredName).OrderBy(n => n, StringComparer.Ordinal).ToList();
    public const string IntegrityFile = "catalog.hash.json";
    public const string HistoryFolder = "history";

    public string Dir { get; private set; } = "";
    public string CommandName { get; private set; } = "";
    public KeyRing? Keys { get; private set; }   // null only for a catalog that is never saved (tests); saved with Keys.Current
    public List<FileRecord> Files { get; private set; } = new();
    public List<DiscRecord> Discs { get; private set; } = new();
    public List<BlankDisc> Blanks { get; private set; } = new();
    public Settings Settings { get; private set; } = new();
    public IntegrityReport Integrity { get; private set; } = new();

    /// <summary>Records per part of the file list; a part never mixes locations. Tests make it small.</summary>
    public int FilesPerShard { get; set; } = 200_000;

    /// <summary>false: loaded without the file list (Load(files: false)); Files is empty and Save keeps its stored files.</summary>
    public bool FilesLoaded { get; private set; } = true;

    /// <summary>Set when the catalog may not be saved in this run (its mirror is not in step): Save refuses with this.</summary>
    public string? SaveBlockedBy { get; set; }

    bool snapshotDone;
    long generation;
    readonly Dictionary<string, string> plainHashes = new(StringComparer.Ordinal);   // stored name -> SHA-256 of its plaintext, as loaded / last saved

    public static Catalog CreateEmpty(string dir, string commandName, KeyRing? keys = null) => new() { Dir = dir, CommandName = commandName, Keys = keys };

    public static CatalogKind Detect(string dir)
    {
        if (KeyFile.ExistsIn(dir)) return CatalogKind.Encrypted;
        return CoveredFiles.Any(n => File.Exists(Path.Combine(dir, n))) ? CatalogKind.Plain : CatalogKind.None;
    }

    /// <summary>
    /// Loads, verifies and decrypts the catalog. A broken catalog is returned EMPTY with State = Broken (unless
    /// ignoreIntegrity). files = false: the file list (by far the largest part) is not read, for commands that never use
    /// it; Files stays empty and Save leaves its stored files as they are.
    /// </summary>
    public static Catalog Load(string dir, string commandName, KeyRing key, bool ignoreIntegrity = false, bool files = true)
    {
        var cat = new Catalog { Dir = dir, CommandName = commandName, Keys = key, FilesLoaded = files };
        cat.Integrity = CheckIntegrity(dir, key, out var manifest);
        cat.generation = manifest?.Generation ?? 0;
        if (cat.Integrity.State == IntegrityState.Broken && !ignoreIntegrity) return cat;

        try
        {
            cat.Files = files ? cat.ReadFileList(dir, key) : new List<FileRecord>();
            cat.Discs = cat.ReadLines<DiscRecord>(dir, "discs.jsonl" + Suffix, key);
            cat.Blanks = cat.ReadLines<BlankDisc>(dir, "blanks.jsonl" + Suffix, key);
            using var settings = cat.ReadPlain(dir, "settings.json" + Suffix, key);
            cat.Settings = settings == null ? new Settings() : JsonStore.ParseObject<Settings>(settings.ToArray(), "settings.json") ?? new Settings();
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

    /// <summary>
    /// The file list: its parts decrypted and parsed in parallel (each is independent), kept in name order. Lines are
    /// parsed as they are decrypted, so no part is ever held whole in memory.
    /// </summary>
    List<FileRecord> ReadFileList(string dir, KeyRing key)
    {
        var names = StoredFilesIn(dir).Where(n => ShardName.IsMatch(n) || n == UnsplitFiles).ToArray();
        var parts = new List<FileRecord>[names.Length];
        try
        {
            Parallel.For(0, names.Length, i =>
            {
                // files.jsonl.enc: the whole file list of an older catalog, or (since the split) only the marker
                var sink = new JsonLinesSink<FileRecord>(names[i][..^Suffix.Length], names[i] == UnsplitFiles ? TooOldMarker : default);
                string hash;
                using (var input = new FileStream(Path.Combine(dir, names[i]), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                {
                    try { hash = FileCrypto.Decrypt(input, sink, key, KeyPurpose.Catalog); }
                    catch (InvalidDataException ex) { throw new InvalidDataException($"{names[i]}: {ex.Message}"); }
                }
                lock (plainHashes) plainHashes[names[i]] = hash;
                parts[i] = sink.Complete();
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.FirstOrDefault(e => e is JsonException or InvalidDataException) is { } first)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
        var all = new List<FileRecord>(parts.Sum(p => p.Count));
        foreach (var p in parts) all.AddRange(p);
        ShareStrings(all);
        return all;
    }

    List<T> ReadLines<T>(string dir, string name, KeyRing key)
    {
        using var plain = ReadPlain(dir, name, key);
        return plain == null ? new List<T>() : JsonStore.ParseLines<T>(plain, name[..^Suffix.Length]);
    }

    /// <summary>Decrypts a stored file into memory (null if it does not exist) and remembers the plaintext's hash.</summary>
    MemoryStream? ReadPlain(string dir, string name, KeyRing key)
    {
        string path = Path.Combine(dir, name);
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var plain = new MemoryStream();
        try { FileCrypto.Decrypt(input, plain, key, KeyPurpose.Catalog); }
        catch (InvalidDataException ex) { plain.Dispose(); throw new InvalidDataException($"{name}: {ex.Message}"); }
        string hash = Convert.ToHexString(SHA256.HashData(plain.GetBuffer().AsSpan(0, (int)plain.Length)));
        lock (plainHashes) plainHashes[name] = hash;
        plain.Position = 0;
        return plain;
    }

    /// <summary>
    /// Every record of a location repeats its path and alias, every copy its disc label, and a copy of the current version
    /// the file's own hash: one string each in memory.
    /// </summary>
    static void ShareStrings(List<FileRecord> files)
    {
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string One(string s) { if (pool.TryGetValue(s, out var known)) return known; pool[s] = s; return s; }
        foreach (var f in files)
        {
            f.Root = One(f.Root);
            f.RootName = One(f.RootName);
            foreach (var b in f.Backups)
            {
                b.Disc = One(b.Disc);
                if (string.Equals(b.Sha256, f.Sha256, StringComparison.Ordinal)) b.Sha256 = f.Sha256;
            }
        }
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
    public static IntegrityReport CheckIntegrity(string dir, KeyRing? key) => CheckIntegrity(dir, key, out _);

    static IntegrityReport CheckIntegrity(string dir, KeyRing? key, out IntegrityManifest? manifest)
    {
        manifest = null;
        var report = new IntegrityReport();
        var existing = StoredFilesIn(dir);
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
                report.Problems.Add($"{entry.Key}: contents do not match the recorded hash (damaged, or changed outside discloak)");
        }
        var listed = manifest.Files;
        foreach (string name in existing.Where(n => !listed.ContainsKey(n)))
            report.Problems.Add($"{name}: exists but is not covered by {IntegrityFile}");
        if (manifest.Unverified == true)
            report.Problems.Add("this version was already damaged when the catalog was encrypted");

        if (key != null)
        {
            var signer = key.Find(manifest.KeyId);   // a version saved before a key rotation names an older key
            if (signer == null)
                report.Problems.Add($"written with another key (key id {manifest.KeyId}, this catalog's are {string.Join(", ", key.All.Select(k => k.KeyId))})");
            else if (manifest.Mac == null || !CryptographicOperations.FixedTimeEquals(
                         Convert.FromHexString(manifest.Mac), Convert.FromHexString(manifest.ComputeMac(signer))))
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
        foreach (string name in StoredFilesIn(dir)) manifest.Files[name] = Hashing.HashFile(Path.Combine(dir, name));
        manifest.Mac = manifest.ComputeMac(key);
        JsonStore.WriteObject(Path.Combine(dir, IntegrityFile), manifest);
    }

    // ---- saving + versioning --------------------------------------------------------------

    public void Save()
    {
        if (Integrity.State == IntegrityState.Broken)
            throw new InvalidOperationException("The catalog is broken; refusing to overwrite it. Run 'catalog check'.");
        if (Keys == null) throw new InvalidOperationException("This catalog has no key and cannot be saved.");
        if (SaveBlockedBy != null) throw new UserException(SaveBlockedBy);

        if (!snapshotDone)
        {
            CreateSnapshot(Dir, CommandName, Math.Max(1, Settings.HistoryKeep));
            snapshotDone = true;   // one copy per command, however many times it saves
        }

        using var _ = Interrupt.Critical();   // Ctrl+C waits until all files and their hashes are written
        Directory.CreateDirectory(Dir);
        KeyFile.MigrateName(Dir);   // a catalog from before 0.8.0: its bdxl.key becomes discloak.key (see KeyFile.LegacyFileName)
        // the file list in parts of at most FilesPerShard records, one location never sharing a part with another, so a
        // change to one location leaves the parts of the others as they are (and unchanged parts are not rewritten).
        // Each part is serialized, compressed and encrypted on its own, in parallel.
        var written = new HashSet<string>(StringComparer.Ordinal);
        if (FilesLoaded)
        {
            var shards = Files.GroupBy(f => f.Root, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .SelectMany(g => g.OrderBy(f => f.RelPath, StringComparer.Ordinal).Chunk(Math.Max(1, FilesPerShard))).ToList();
            var names = new string[shards.Count];
            Parallel.For(0, shards.Count, i => names[i] = WriteEncrypted($"files.{i:D3}.jsonl", ms => JsonStore.WriteLinesTo(ms, shards[i])));
            written.UnionWith(names);
            written.Add(WriteEncrypted("files.jsonl", ms => ms.Write(TooOldMarker)));
        }
        else
        {
            var kept = StoredFilesIn(Dir).Where(n => ShardName.IsMatch(n) || n == UnsplitFiles).ToList();   // not read: kept as they are,
            Parallel.ForEach(kept, ReEncryptLegacy);                                                           // in today's format
            written.UnionWith(kept);
        }
        written.Add(WriteEncrypted("discs.jsonl", ms => JsonStore.WriteLinesTo(ms, Discs.OrderBy(d => d.Label, StringComparer.Ordinal))));
        written.Add(WriteEncrypted("blanks.jsonl", ms => JsonStore.WriteLinesTo(ms, Blanks.OrderBy(b => b.Id, StringComparer.Ordinal))));
        written.Add(WriteEncrypted("settings.json", ms => ms.Write(JsonStore.ObjectToBytes(Settings))));
        foreach (string stale in StoredFilesIn(Dir).Where(n => !written.Contains(n)))   // parts no longer needed
        {
            File.Delete(Path.Combine(Dir, stale));
            lock (plainHashes) plainHashes.Remove(stale);
        }
        WriteIntegrity(Dir, Keys.Current, ++generation);
        Integrity = new IntegrityReport { State = IntegrityState.Ok };
        Mirror.Sync(Dir);   // the mirror (if any) gets the same files
    }

    /// <summary>
    /// Serializes into memory and encrypts (compressed) straight into &lt;name&gt;.enc, via a temp file, then renamed -
    /// unless the file already holds exactly this, encrypted with the current key. Returns the stored name.
    /// </summary>
    /// <summary>
    /// A stored file this save keeps as it is (its part of the file list was not read) but which is still in the
    /// format from before 0.8.0 (BDXLENC2): decrypted and encrypted again in today's (DCLKENC2), content unchanged.
    /// </summary>
    void ReEncryptLegacy(string stored)
    {
        string path = Path.Combine(Dir, stored), tmp = path + ".tmp";
        if (!FileCrypto.IsLegacy(path)) return;
        byte[] plain = FileCrypto.Open(File.ReadAllBytes(path), Keys!, KeyPurpose.Catalog);
        try
        {
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                FileCrypto.Encrypt(new MemoryStream(plain, writable: false), output, Keys!.Current, KeyPurpose.Catalog, level: CompressionLevel);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    string WriteEncrypted(string name, Action<Stream> write)
    {
        // 1. only hash what would be written: an unchanged file is neither kept in memory nor rewritten
        string stored = name + Suffix, path = Path.Combine(Dir, stored), tmp = path + ".tmp";
        string hash;
        using (var hashing = new HashingSink()) { write(hashing); hash = hashing.Hex(); }
        string? known;
        lock (plainHashes) plainHashes.TryGetValue(stored, out known);
        // unchanged: not written again - unless it is still in the format from before 0.8.0 (BDXLENC2), so that one
        // save brings the whole catalog to today's (DCLKENC2)
        if (known == hash && File.Exists(path) && FileCrypto.KeyIdOf(path) == Keys!.Current.KeyId && !FileCrypto.IsLegacy(path))
            return stored;

        // 2. serialize again, straight into the encryption through a pipe (never the whole file in memory)
        var pipe = new System.IO.Pipelines.Pipe();
        var producer = Task.Factory.StartNew(() =>   // its own thread: the saving threads wait on it
        {
            try
            {
                using (var s = pipe.Writer.AsStream(leaveOpen: true)) write(s);
                pipe.Writer.Complete();
            }
            catch (Exception ex) { pipe.Writer.Complete(ex); throw; }
        }, TaskCreationOptions.LongRunning);
        string written;
        try
        {
            using var input = pipe.Reader.AsStream(leaveOpen: true);
            using var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            written = FileCrypto.Encrypt(input, output, Keys!.Current, KeyPurpose.Catalog, level: CompressionLevel);
        }
        catch
        {
            pipe.Reader.Complete();   // lets a producer waiting for room fail instead of waiting forever
            try { producer.Wait(); } catch (AggregateException) { }
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
        pipe.Reader.Complete();
        producer.GetAwaiter().GetResult();
        if (written != hash)
        {
            File.Delete(tmp);
            throw new InvalidOperationException($"{stored}: the catalog changed while it was being written; nothing was replaced.");
        }
        File.Move(tmp, path, overwrite: true);
        lock (plainHashes) plainHashes[stored] = hash;
        return stored;
    }

    /// <summary>Copies the stored catalog files (and their hash file) to history/&lt;time&gt;-&lt;command&gt;/ and prunes old versions.</summary>
    public static string? CreateSnapshot(string dir, string commandName, int keep)
    {
        var present = StoredFilesIn(dir);
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
    public static List<string> VerifySnapshot(string dir, string id, KeyRing key)
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
    public static void RestoreSnapshot(string dir, string id, string commandName, int keep, KeyRing key)
    {
        string snap = Path.Combine(dir, HistoryFolder, id);
        bool trusted = VerifySnapshot(dir, id, key).Count == 0;
        CreateSnapshot(dir, commandName, keep + 1);

        var inSnapshot = StoredFilesIn(snap);
        foreach (string name in inSnapshot) File.Copy(Path.Combine(snap, name), Path.Combine(dir, name), overwrite: true);
        foreach (string name in StoredFilesIn(dir).Except(inSnapshot, StringComparer.Ordinal))
            File.Delete(Path.Combine(dir, name));   // that version did not have this file
        if (trusted) File.Copy(Path.Combine(snap, IntegrityFile), Path.Combine(dir, IntegrityFile), overwrite: true);
        else WriteIntegrity(dir, key.Current, 0);
    }
}
