namespace Bdxl;

public enum IntegrityState
{
    Fresh,    // nothing there yet
    Legacy,   // catalog files exist but were written before integrity hashes existed
    Ok,       // every catalog file matches the recorded hash
    Broken    // a file is missing, changed outside this tool, unreadable or unlisted
}

public sealed class IntegrityReport
{
    public IntegrityState State { get; set; } = IntegrityState.Fresh;
    public List<string> Problems { get; } = new();
    public DateTime? RecordedUtc { get; set; }
}

public sealed class IntegrityManifest
{
    public DateTime CreatedUtc { get; set; }
    public Dictionary<string, string> Files { get; set; } = new();   // file name -> SHA-256
}

/// <summary>
/// The catalog: files.jsonl, discs.jsonl, blanks.jsonl and settings.json in one folder.
///  - integrity: catalog.hash.json holds a SHA-256 for each file and is checked on every load;
///  - versioning: before the first change a command makes, the previous state is copied to history/ (once per command).
/// </summary>
public sealed class Catalog
{
    public static readonly string[] CoveredFiles = { "files.jsonl", "discs.jsonl", "blanks.jsonl", "settings.json" };
    public const string IntegrityFile = "catalog.hash.json";
    public const string HistoryFolder = "history";

    public string Dir { get; private set; } = "";
    public string CommandName { get; private set; } = "";
    public List<FileRecord> Files { get; private set; } = new();
    public List<DiscRecord> Discs { get; private set; } = new();
    public List<BlankDisc> Blanks { get; private set; } = new();
    public Settings Settings { get; private set; } = new();
    public IntegrityReport Integrity { get; private set; } = new();

    bool snapshotDone;

    public static Catalog CreateEmpty(string dir, string commandName) => new() { Dir = dir, CommandName = commandName };

    /// <summary>Loads and verifies the catalog. A broken catalog is returned EMPTY with State = Broken (unless ignoreIntegrity).</summary>
    public static Catalog Load(string dir, string commandName, bool ignoreIntegrity = false)
    {
        var cat = new Catalog { Dir = dir, CommandName = commandName };
        cat.Integrity = CheckIntegrity(dir);
        if (cat.Integrity.State == IntegrityState.Broken && !ignoreIntegrity) return cat;

        try
        {
            cat.Files = JsonStore.ReadLines<FileRecord>(Path.Combine(dir, "files.jsonl"));
            cat.Discs = JsonStore.ReadLines<DiscRecord>(Path.Combine(dir, "discs.jsonl"));
            cat.Blanks = JsonStore.ReadLines<BlankDisc>(Path.Combine(dir, "blanks.jsonl"));
            cat.Settings = JsonStore.ReadObject<Settings>(Path.Combine(dir, "settings.json")) ?? new Settings();
        }
        catch (JsonException ex)
        {
            cat.Integrity.State = IntegrityState.Broken;
            cat.Integrity.Problems.Add("Cannot be parsed: " + ex.Message);
            cat.Files = new List<FileRecord>();
            cat.Discs = new List<DiscRecord>();
            cat.Blanks = new List<BlankDisc>();
            cat.Settings = new Settings();
        }
        return cat;
    }

    /// <summary>Used by 'catalog accept': the user vouches for the current contents.</summary>
    public void TrustCurrentContents()
    {
        Integrity = new IntegrityReport { State = IntegrityState.Ok };
    }

    // ---- integrity ------------------------------------------------------------------------

    public static IntegrityReport CheckIntegrity(string dir)
    {
        var report = new IntegrityReport();
        var existing = CoveredFiles.Where(n => File.Exists(Path.Combine(dir, n))).ToList();
        string manifestPath = Path.Combine(dir, IntegrityFile);

        if (!File.Exists(manifestPath))
        {
            report.State = existing.Count == 0 ? IntegrityState.Fresh : IntegrityState.Legacy;
            return report;
        }

        IntegrityManifest? manifest;
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
                report.Problems.Add($"{entry.Key}: contents do not match the recorded hash (edited outside bdxl, or damaged)");
        }
        foreach (string name in existing.Where(n => !manifest.Files.ContainsKey(n)))
            report.Problems.Add($"{name}: exists but is not covered by {IntegrityFile}");

        report.State = report.Problems.Count == 0 ? IntegrityState.Ok : IntegrityState.Broken;
        return report;
    }

    /// <summary>Writes catalog.hash.json for the files that are currently in the folder.</summary>
    public static void WriteIntegrity(string dir)
    {
        var manifest = new IntegrityManifest { CreatedUtc = DateTime.UtcNow };
        foreach (string name in CoveredFiles)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) manifest.Files[name] = Hashing.HashFile(path);
        }
        JsonStore.WriteObject(Path.Combine(dir, IntegrityFile), manifest);
    }

    // ---- saving + versioning --------------------------------------------------------------

    public void Save()
    {
        if (Integrity.State == IntegrityState.Broken)
            throw new InvalidOperationException("The catalog is broken; refusing to overwrite it. Run 'catalog check'.");

        if (!snapshotDone)
        {
            CreateSnapshot(Dir, CommandName, Math.Max(1, Settings.HistoryKeep));
            snapshotDone = true;   // one copy per command, however many times it saves
        }

        Directory.CreateDirectory(Dir);
        JsonStore.WriteLines(Path.Combine(Dir, "files.jsonl"),
            Files.OrderBy(f => f.Root, StringComparer.Ordinal).ThenBy(f => f.RelPath, StringComparer.Ordinal));
        JsonStore.WriteLines(Path.Combine(Dir, "discs.jsonl"), Discs.OrderBy(d => d.Label, StringComparer.Ordinal));
        JsonStore.WriteLines(Path.Combine(Dir, "blanks.jsonl"), Blanks.OrderBy(b => b.Id, StringComparer.Ordinal));
        JsonStore.WriteObject(Path.Combine(Dir, "settings.json"), Settings);
        WriteIntegrity(Dir);
        Integrity = new IntegrityReport { State = IntegrityState.Ok };
    }

    /// <summary>Copies the current catalog files (and their hash file) to history/&lt;time&gt;-&lt;command&gt;/ and prunes old versions.</summary>
    public static string? CreateSnapshot(string dir, string commandName, int keep)
    {
        var present = CoveredFiles.Where(n => File.Exists(Path.Combine(dir, n))).ToList();
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

    /// <summary>Checks a snapshot against its own hash file; returns problems (empty = fine).</summary>
    public static List<string> VerifySnapshot(string dir, string id)
    {
        string snap = Path.Combine(dir, HistoryFolder, id);
        if (!Directory.Exists(snap)) return new List<string> { $"No such version: {id}" };
        var problems = new List<string>();
        string manifestPath = Path.Combine(snap, IntegrityFile);
        if (!File.Exists(manifestPath)) { problems.Add("this version has no hash file (it was made before integrity hashes existed)"); return problems; }
        var manifest = JsonStore.ReadObject<IntegrityManifest>(manifestPath);
        if (manifest == null) { problems.Add("hash file is empty"); return problems; }
        foreach (var entry in manifest.Files)
        {
            string path = Path.Combine(snap, entry.Key);
            if (!File.Exists(path)) problems.Add($"{entry.Key}: missing");
            else if (!Hashing.SameHash(Hashing.HashFile(path), entry.Value)) problems.Add($"{entry.Key}: hash mismatch");
        }
        return problems;
    }

    /// <summary>Puts an older version back. The current state is snapshotted first, so a rollback can be undone.</summary>
    public static void RestoreSnapshot(string dir, string id, string commandName, int keep)
    {
        string snap = Path.Combine(dir, HistoryFolder, id);
        CreateSnapshot(dir, commandName, keep + 1);

        foreach (string name in CoveredFiles)
        {
            string from = Path.Combine(snap, name);
            string to = Path.Combine(dir, name);
            if (File.Exists(from)) File.Copy(from, to, overwrite: true);
            else if (File.Exists(to)) File.Delete(to);   // that version did not have this file
        }
        WriteIntegrity(dir);
    }
}
