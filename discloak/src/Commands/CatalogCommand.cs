namespace Discloak;

/// <summary>
/// catalog check | accept | history | rollback &lt;id&gt; | export &lt;dir&gt; | import &lt;dir&gt; [--keep] |
/// backup create &lt;dir&gt; | backup verify &lt;dir&gt; [--repair] | backup restore &lt;dir&gt; [--force]
/// Integrity and versions of the catalog itself, plaintext export/import, and catalog backups with Reed-Solomon repair data.
/// </summary>
public sealed class CatalogCommand : ICommand
{
    public string Name => "catalog";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Integrity check, older versions, export/import, backups with repair data, the mirror";
    public string Usage => "catalog check | accept | history | rollback <id> | export <dir> | import <dir> [--keep] | backup create <dir> | backup verify <dir> [--repair] | backup restore <dir> [--force] | mirror status | mirror sync | mirror restore [--recovery]";
    public string Details => """
        The catalog is stored encrypted. catalog.hash.json holds a hash of every stored file and an HMAC over that
        list, checked every time the catalog is loaded; if a file is damaged, altered or mixed in from another save,
        other commands refuse to run.
          catalog check              verify only, nothing else
          catalog accept             every file still decrypts but the hash list does not match: record it anew (previous state kept)
          catalog history            older versions (one copy is kept before each command that changes the catalog)
          catalog rollback <id>      go back to one of them (the current state is saved first; --force for a damaged one)
          catalog export <dir>       write the catalog as plain JSON into an empty folder (to read or edit it - it is NOT encrypted)
          catalog import <dir>       replace the catalog with plain JSON from such a folder (the current state is kept in
                                     history), then wipe the folder: every file in it overwritten with random bytes and
                                     deleted, and the folder removed - only once the import is saved; --keep leaves it.
                                     A folder with subfolders or links, or one that looks like a catalog, is refused.
                                     The wipe reaches the files themselves; an SSD may keep the old blocks until it
                                     reclaims them, and shadow copies, synced folders, NAS snapshots and editors' own
                                     backups (Windows 11 Notepad keeps its open tabs) keep copies of their own - best
                                     export onto an encrypted drive (BitLocker).
          catalog backup create <dir>            copy of the encrypted catalog + discloak.key + Reed-Solomon repair data (--parity-percent 25)
          catalog backup verify <dir> [--repair] check / fix a backup (no password needed)
          catalog backup restore <dir> [--force] put a backup back (password, or --recovery for the recovery key, when restoring
                                                 into an empty folder; into this catalog, the current discloak.key and password stay)
        Every staged disc with --with-catalog carries the same kind of backup in _catalog.
        The mirror ('settings set mirror <folder>') is a second copy kept in step by every save:
          catalog mirror status      is it there and in step?
          catalog mirror sync        copy the catalog over the mirror (after it was not connected, or a save did not reach it)
          catalog mirror restore     take the catalog from the mirror (the catalog is damaged or behind; its current state goes
                                     to history; the mirror's discloak.key comes along, so its password applies)
        """;
    public bool AllowBrokenCatalog => true;

    public bool NeedsCatalogFor(Options opt) =>
        !(Is(opt, "backup", "verify") || Is(opt, "backup", "restore") || Is(opt, "mirror", "restore"));

    public bool ReadOnlyFor(Options opt) =>
        opt.Args.Count > 0 && opt.Args[0].ToLowerInvariant() is "check" or "history" or "export" ||
        Is(opt, "backup", "create") || Is(opt, "mirror", "status");

    public bool FixesMirror(Options opt) => Is(opt, "mirror", "sync") || Is(opt, "mirror", "restore");

    static bool Is(Options opt, string sub, string action) =>
        opt.Args.Count > 1 && opt.Args[0].Equals(sub, StringComparison.OrdinalIgnoreCase) && opt.Args[1].Equals(action, StringComparison.OrdinalIgnoreCase);

    public int Run(Ctx c)
    {
        string sub = c.Args.Count > 0 ? c.Args[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "check": return Check(c);
            case "accept": return Accept(c);
            case "history": return History(c);
            case "rollback": return Rollback(c);
            case "export": return Export(c, c.Arg(1, "catalog export <dir>"));
            case "import": return Import(c, c.Arg(1, "catalog import <dir>"));
            case "backup":
                string action = c.Args.Count > 1 ? c.Args[1].ToLowerInvariant() : "";
                if (c.Args.Count != 3) throw new UserException("Usage: catalog backup create <dir> | verify <dir> [--repair] | restore <dir> [--force]");
                if (action == "create") return BackupCreate(c, c.Args[2]);
                if (action == "verify") return BackupVerify(c, c.Args[2]);
                if (action == "restore") return BackupRestore(c, c.Args[2]);
                throw new UserException("Usage: catalog backup create <dir> | verify <dir> [--repair] | restore <dir> [--force]");
            case "mirror":
                switch (c.Args.Count > 1 ? c.Args[1].ToLowerInvariant() : "status")
                {
                    case "status": return MirrorStatus(c);
                    case "sync": return MirrorSync(c);
                    case "restore": return MirrorRestore(c);
                    default: throw new UserException("Usage: catalog mirror status | sync | restore [--recovery]");
                }
            default:
                throw new UserException("Usage: catalog check | accept | history | rollback <id> | export <dir> | import <dir> | backup create|verify|restore <dir>");
        }
    }

    // ---- check / accept -------------------------------------------------------------------

    /// <summary>Verifies the catalog against its recorded hashes. Does no other work.</summary>
    static int Check(Ctx c)
    {
        var report = c.Cat.Integrity;
        Console.WriteLine($"Catalog: {c.Cat.Dir}");
        switch (report.State)
        {
            case IntegrityState.Fresh:
                Console.WriteLine("The catalog is empty (nothing to check).");
                return 0;
            case IntegrityState.Ok:
                Console.WriteLine($"OK - every catalog file matches the hash list recorded on {report.RecordedUtc:yyyy-MM-dd HH:mm} UTC, and the list its HMAC.");
                Console.WriteLine($"{c.Cat.Files.Count:N0} files, {c.Cat.Discs.Count:N0} discs, {c.Cat.Blanks.Count:N0} blank discs, {c.Settings.IgnoreRules.Count} ignore rules.");
                return 0;
            default:
                Console.WriteLine("BROKEN - commands that use the catalog are blocked until this is resolved:");
                foreach (string p in report.Problems) Console.WriteLine("  " + p);
                var versions = Catalog.ListHistory(c.Cat.Dir);
                Console.WriteLine("\nWays out:");
                Console.WriteLine("  catalog history / catalog rollback <id>    go back to an earlier version" + (versions.Count == 0 ? " (none exist yet)" : $" ({versions.Count} available)"));
                Console.WriteLine("  catalog backup restore <dir> --force       restore from a catalog backup");
                Console.WriteLine("  catalog accept                             every file still decrypts and its contents are fine");
                return 4;
        }
    }

    /// <summary>The user vouches for the current contents: every file must still decrypt and parse; then the hash list is written anew.</summary>
    static int Accept(Ctx c)
    {
        var cat = Catalog.Load(c.Cat.Dir, "catalog-accept", c.Keys, ignoreIntegrity: true);
        var unreadable = cat.Integrity.Problems.FirstOrDefault(p => p.StartsWith("Cannot be read", StringComparison.Ordinal));
        if (unreadable != null) throw new UserException(unreadable + " - it cannot be accepted; roll back or restore a backup.");
        if (cat.Integrity.State == IntegrityState.Ok) { Console.WriteLine("The catalog is already consistent; nothing to accept."); return 0; }

        if (!Ui.Confirm(c.Opt, "Record the current contents of the catalog files as correct?")) { Console.WriteLine("Cancelled."); return 1; }
        cat.TrustCurrentContents();
        cat.Save();   // keeps the previous (unverified) state in history first
        Console.WriteLine($"Accepted: {cat.Files.Count:N0} files, {cat.Discs.Count:N0} discs. New hashes recorded.");
        return 0;
    }

    // ---- history / rollback ---------------------------------------------------------------

    static int History(Ctx c)
    {
        var versions = Catalog.ListHistory(c.Cat.Dir);
        if (versions.Count == 0) { Console.WriteLine("No older versions yet. One is kept automatically before each command that changes the catalog."); return 0; }
        Console.WriteLine($"Older catalog versions (newest first; the last {c.Settings.HistoryKeep} are kept):");
        foreach (string id in versions)
        {
            var problems = Catalog.VerifySnapshot(c.Cat.Dir, id, c.Keys);
            Console.WriteLine($"  {id}   {(problems.Count == 0 ? "hash OK" : "PROBLEM: " + problems[0])}");
        }
        Console.WriteLine("\nThe name shows when it was saved and which command was about to change the catalog.");
        return 0;
    }

    static int Rollback(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: catalog rollback <id>   (ids: catalog history)");
        string id = c.Args[1];
        var problems = Catalog.VerifySnapshot(c.Cat.Dir, id, c.Keys);
        if (problems.Any(p => p.StartsWith("No such version", StringComparison.Ordinal) || p.StartsWith("written with another key", StringComparison.Ordinal)))
            throw new UserException($"Version {id}: {problems[0]}.");
        if (problems.Count > 0 && !c.Opt.Has("force"))
            throw new UserException($"Version {id} cannot be trusted: {problems[0]}. Use --force to restore it anyway.");
        if (!Ui.Confirm(c.Opt, $"Replace the current catalog with version {id}? (the current one is saved to history first)")) { Console.WriteLine("Cancelled."); return 1; }

        Catalog.RestoreSnapshot(c.Cat.Dir, id, "catalog-rollback", Math.Max(1, c.Settings.HistoryKeep), c.Keys);
        Mirror.Sync(c.Cat.Dir);
        var after = Catalog.Load(c.Cat.Dir, "catalog-rollback", c.Keys);
        Console.WriteLine($"Restored version {id}: {after.Files.Count:N0} files, {after.Discs.Count:N0} discs (integrity: {after.Integrity.State}).");
        return after.Integrity.State == IntegrityState.Broken ? 4 : 0;
    }

    // ---- mirror ------------------------------------------------------------------------------

    static int MirrorStatus(Ctx c)
    {
        var r = Mirror.Check(c.Cat.Dir, c.Keys);
        if (r.State == Mirror.State.None) { Console.WriteLine("No mirror. Set one with:  settings set mirror <folder on another drive>"); return 0; }
        Console.WriteLine($"Mirror: {r.Path}");
        if (r.Ok) { Console.WriteLine("In step with the catalog."); return 0; }
        Console.WriteLine(Mirror.Advice(r));
        return 2;
    }

    /// <summary>catalog mirror sync: the catalog (which must check out) is copied over the mirror.</summary>
    static int MirrorSync(Ctx c)
    {
        string dir = c.Cat.Dir;
        if (c.Cat.Integrity.State == IntegrityState.Broken)
            throw new UserException("The catalog itself fails its check, so it is not copied over the mirror. See 'catalog check' (or 'catalog mirror restore').");
        var r = Mirror.Check(dir, c.Keys);
        if (r.State == Mirror.State.None) throw new UserException("No mirror is set (settings set mirror <folder>).");
        if (r.Ok) { Console.WriteLine($"The mirror {r.Path} is already in step."); return 0; }
        if (r.State == Mirror.State.Unreachable) throw new UserException(r.Message + ".");
        if (r.MirrorGeneration > r.MainGeneration &&
            !Ui.Confirm(c.Opt, $"The mirror is NEWER (save {r.MirrorGeneration}, the catalog is at save {r.MainGeneration}). Overwrite it with the catalog anyway?"))
        {
            Console.WriteLine("Cancelled. To take the mirror's version instead: catalog mirror restore");
            return 1;
        }
        Mirror.CopyCatalog(dir, r.Path!);
        var after = Mirror.Check(dir, c.Keys);
        Console.WriteLine(after.Ok ? $"The mirror {r.Path} is in step again." : $"Copied, but: {after.Message}.");
        return after.Ok ? 0 : 1;
    }

    /// <summary>
    /// catalog mirror restore: the mirror's catalog (checked with its own discloak.key) replaces the catalog; the catalog's
    /// current state is kept in history first.
    /// </summary>
    static int MirrorRestore(Ctx c)
    {
        string dir = c.Cat.Dir;
        string path = Mirror.PathFor(dir) ?? throw new UserException($"No mirror is set for {dir}.");
        if (!Directory.Exists(path)) throw new UserException($"The mirror {path} is not reachable.");
        var mirrorKey = KeyFile.ReadFrom(path) ?? throw new UserException($"The mirror {path} holds no {KeyFile.FileName}.");
        KeyFile? mainKey;
        try { mainKey = KeyFile.ReadFrom(dir); }
        catch (InvalidDataException) { mainKey = null; }   // damaged: the mirror's replaces it
        if (mainKey != null && mainKey.CatalogId != mirrorKey.CatalogId)
            throw new UserException($"The mirror holds another catalog (id {mirrorKey.CatalogId}, not {mainKey.CatalogId}).");

        using var keys = c.Opt.Has("recovery") ? CatalogSetup.UnlockWithRecoveryKey(mirrorKey)
            : AgentProtocol.Enabled && new AgentClient().TryGetKey(mirrorKey.CatalogId, mirrorKey.KeyId) is { } fromAgent ? fromAgent
            : CatalogSetup.UnlockWithPassword(mirrorKey, c.Opt, "the mirror's key file");
        var check = Catalog.Load(path, "catalog-mirror-restore", keys);
        if (check.Integrity.State != IntegrityState.Ok)
            throw new UserException($"The mirror fails its check too ({string.Join("; ", check.Integrity.Problems)}); nothing was changed.");
        if (!Ui.Confirm(c.Opt, $"Replace the catalog in {dir} with the mirror's version ({check.Files.Count:N0} files, {check.Discs.Count:N0} discs)? " +
                               "(the current one is kept in history)"))
        {
            Console.WriteLine("Cancelled.");
            return 1;
        }

        Catalog.CreateSnapshot(dir, "catalog-mirror-restore", Math.Max(1, check.Settings.HistoryKeep));
        Mirror.CopyCatalog(path, dir);
        var after = Catalog.Load(dir, "catalog-mirror-restore", keys);
        Console.WriteLine($"Took the catalog from the mirror: {after.Files.Count:N0} files, {after.Discs.Count:N0} discs (integrity: {after.Integrity.State}).");
        return after.Integrity.State == IntegrityState.Ok ? 0 : 4;
    }

    // ---- export / import (plaintext) --------------------------------------------------------

    static int Export(Ctx c, string dirArg)
    {
        if (c.Cat.Integrity.State == IntegrityState.Broken)
            throw new UserException("The catalog is broken; 'catalog accept' (if every file still decrypts), roll back or restore a backup first.");
        string dir = Path.GetFullPath(dirArg);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) throw new UserException($"{dir} is not empty.");
        JsonStore.WriteLines(Path.Combine(dir, "files.jsonl"), c.Cat.Files);
        JsonStore.WriteLines(Path.Combine(dir, "discs.jsonl"), c.Cat.Discs);
        JsonStore.WriteLines(Path.Combine(dir, "blanks.jsonl"), c.Cat.Blanks);
        JsonStore.WriteObject(Path.Combine(dir, "settings.json"), c.Settings);
        Console.WriteLine($"Exported {c.Cat.Files.Count:N0} files, {c.Cat.Discs.Count:N0} discs, {c.Cat.Blanks.Count:N0} blank discs as plain JSON to {dir}.");
        Console.WriteLine("These files are NOT encrypted: they show every path and hash. Bring edits back with");
        Console.WriteLine($"  catalog import \"{dir}\"     (which then wipes the folder; --keep leaves it)");
        return 0;
    }

    /// <summary>
    /// Replaces the catalog with a plain export, then (unless --keep) wipes the export: it shows every path and hash.
    /// Whether the folder can be wiped is decided before anything is imported, and it is wiped only once the import
    /// is saved.
    /// </summary>
    static int Import(Ctx c, string dirArg)
    {
        string dir = Path.GetFullPath(dirArg);
        if (!Catalog.CoveredFiles.Any(n => File.Exists(Path.Combine(dir, n))))
            throw new UserException($"{dir} has none of {string.Join(", ", Catalog.CoveredFiles)}.");
        bool wipe = !c.Opt.Has("keep");
        string[] toWipe = [];
        if (wipe)
        {
            string? why = SecureDelete.Unsafe(dir)
                ?? (Path.TrimEndingDirectorySeparator(dir).Equals(Path.TrimEndingDirectorySeparator(c.Cat.Dir), StringComparison.OrdinalIgnoreCase)
                    ? "it is the catalog's own folder" : null)
                ?? (Directory.EnumerateFiles(dir).Select(Path.GetFileName).FirstOrDefault(n => n is KeyFile.FileName or KeyFile.LegacyFileName || n!.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)) is { } odd
                    ? $"it holds {odd}, so it looks like a catalog, not an export" : null);
            if (why != null) throw new UserException($"The folder would be wiped after the import, but {why}. Nothing was imported; add --keep to import without wiping.");
            toWipe = Directory.GetFiles(dir).Select(p => Path.GetFileName(p)!).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        var files = JsonStore.ReadLines<FileRecord>(Path.Combine(dir, "files.jsonl"));
        var discs = JsonStore.ReadLines<DiscRecord>(Path.Combine(dir, "discs.jsonl"));
        var blanks = JsonStore.ReadLines<BlankDisc>(Path.Combine(dir, "blanks.jsonl"));
        var settings = JsonStore.ReadObject<Settings>(Path.Combine(dir, "settings.json")) ?? new Settings();

        Console.WriteLine($"{dir}: {files.Count:N0} files, {discs.Count:N0} discs, {blanks.Count:N0} blank discs.");
        Console.WriteLine($"The catalog now has {c.Cat.Files.Count:N0} files, {c.Cat.Discs.Count:N0} discs, {c.Cat.Blanks.Count:N0} blank discs" +
                          (c.Cat.Integrity.State == IntegrityState.Broken ? " (it is broken)." : "."));
        if (wipe)
            Console.WriteLine($"Then {dir} is wiped: {string.Join(", ", toWipe)} overwritten and deleted, and the folder removed (--keep leaves it).");
        if (!Ui.Confirm(c.Opt, "Replace the catalog with the imported files? (the current one is kept in history)")) { Console.WriteLine("Cancelled."); return 1; }
        c.Cat.ReplaceContents(files, discs, blanks, settings);
        c.Cat.TrustCurrentContents();
        c.Cat.Save();
        if (!wipe)
        {
            Console.WriteLine($"Imported. The plain JSON in {dir} is still there - delete it when you no longer need it.");
            return 0;
        }

        var failed = SecureDelete.Folder(dir);
        if (failed.Count == 0)
        {
            Console.WriteLine($"Imported, and {dir} wiped ({toWipe.Length:N0} file(s) overwritten with random bytes and deleted, the folder removed).");
            return 0;
        }
        Console.WriteLine($"Imported, but not everything in {dir} could be wiped (is a file open in another program?):");
        foreach (string f in failed) Console.WriteLine("  " + f);
        Console.WriteLine("Close it and delete what is left yourself; the rest was overwritten and deleted.");
        return 2;
    }

    // ---- backup (copy of the encrypted catalog + discloak.key + repair data) ---------------------

    static int BackupCreate(Ctx c, string destArg)
    {
        if (c.Cat.Integrity.State == IntegrityState.Broken)
            throw new UserException("The catalog is broken; a backup of it would be worthless. Run 'catalog check'.");
        string dir = Path.Combine(Path.GetFullPath(destArg), $"catalog-{DateTime.Now:yyyyMMdd-HHmmss}");
        Console.WriteLine($"Catalog backup -> {dir}");
        WriteBackup(c, dir);
        Console.WriteLine("Keep it on a different medium than the catalog (or burn it onto the next disc: stage --with-catalog).");
        return 0;
    }

    /// <summary>The files a catalog backup holds: the encrypted catalog, its hash list and discloak.key.</summary>
    static IEnumerable<string> BackupFiles(string catalogDir) => Catalog.StoredFilesIn(catalogDir).Append(Catalog.IntegrityFile).Append(KeyFile.FileName);

    /// <summary>
    /// Copies the encrypted catalog, its hash list and discloak.key into dir and adds .repair.json + .par (Reed-Solomon)
    /// next to each. Also used by 'stage --with-catalog'. Nothing is decrypted: creating, checking and repairing a
    /// backup need no password.
    /// </summary>
    public static void WriteBackup(Ctx c, string dir, bool quiet = false)
    {
        int pct = c.Opt.GetInt("parity-percent", 25);
        if (pct < 1 || pct > 50) throw new UserException("--parity-percent must be between 1 and 50.");
        Directory.CreateDirectory(dir);

        foreach (string name in BackupFiles(c.Cat.Dir))
        {
            // the key file under today's name, also from a catalog that still holds bdxl.key (written before 0.8.0)
            string src = name == KeyFile.FileName ? KeyFile.PathIn(c.Cat.Dir) : Path.Combine(c.Cat.Dir, name);
            if (!File.Exists(src)) continue;
            byte[] data = File.ReadAllBytes(src);
            var meta = Rs.Protect(name, data, pct, out byte[] parity);
            File.WriteAllBytes(Path.Combine(dir, name), data);
            File.WriteAllBytes(Path.Combine(dir, name + ".par"), parity);
            JsonStore.WriteObject(Path.Combine(dir, name + ".repair.json"), meta);
            if (!quiet) Console.WriteLine($"  {name}: {SizeText.Bytes(data.Length)} bytes + {SizeText.Bytes(parity.Length)} bytes repair data " +
                              $"(can rebuild up to {meta.ParityBlocks} damaged blocks of {SizeText.Bytes(meta.BlockSize)} bytes)");
        }

        File.WriteAllText(Path.Combine(dir, "README.txt"),
            $"Discloak catalog backup, created {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.\n\n" +
            "files.jsonl.enc, discs.jsonl.enc, blanks.jsonl.enc, settings.json.enc   the catalog, ENCRYPTED with the catalog's\n" +
            "    master key (AES-256-GCM, format DCLKENC2 - BDXLENC2 in backups from before 0.8.0, when Discloak was bdxl)\n" +
            "catalog.hash.json          SHA-256 of each file above and an HMAC over that list\n" +
            "discloak.key               the master key, wrapped by the password (Argon2id) and, if one was made, by the recovery\n" +
            "                           key (bdxl.key in backups from before 0.8.0)\n" +
            "*.repair.json, *.par       repair data: per-block SHA-256 + Reed-Solomon parity blocks (GF(256), Cauchy matrix),\n" +
            "    computed over the stored bytes: checking and repairing need no password\n\n" +
            $"Discloak: {BuildInfo.ProjectUrl}\n\n" +
            "Check:    discloak catalog backup verify <this folder>\n" +
            "Repair:   discloak catalog backup verify <this folder> --repair\n" +
            "Restore:  discloak catalog backup restore <this folder> --catalog <catalog folder> [--force] [--recovery]\n");
    }

    internal static string[] RepairNames(string dir)
    {
        const string suffix = ".repair.json";
        if (!Directory.Exists(dir)) throw new UserException($"Not a folder: {dir}");
        var names = Directory.GetFiles(dir, "*" + suffix).Select(p => Path.GetFileName(p)[..^suffix.Length])
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (names.Length == 0) throw new UserException($"No *.repair.json files in {dir}.");
        return names;
    }

    /// <summary>A backed-up file checked against its repair data (noCache: read from the disc itself, see UncachedFile).</summary>
    internal static (RsStatus Status, byte[] Data, byte[]? Parity, int BadData, int BadParity) Load(string dir, string name, bool noCache = false)
    {
        RepairMeta? meta;
        string metaPath = Path.Combine(dir, name + ".repair.json");
        try { meta = File.Exists(metaPath) ? JsonStore.ParseObject<RepairMeta>(DiscRead.AllBytes(metaPath, noCache), name + ".repair.json") : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { throw new UserException($"Unreadable repair file for {name} ({ex.Message})."); }
        if (meta == null) throw new UserException($"Unreadable repair file for {name}.");
        int block = Math.Max(UncachedFile.Alignment, meta.BlockSize);   // the repair blocks: whole multiples of 4 KiB
        return Rs.Check(meta, ReadBlocks(Path.Combine(dir, name), block, noCache), ReadBlocks(Path.Combine(dir, name + ".par"), block, noCache));
    }

    /// <summary>
    /// A file read block by block: a block that cannot be read (a bad spot on a disc) is left as zeros, so its hash
    /// marks it damaged and the repair data rebuilds it. null if the file is missing or cannot be opened.
    /// </summary>
    static byte[]? ReadBlocks(string path, int block, bool noCache)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using Stream input = noCache ? new UncachedFile(path, block)
                                         : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            var data = new byte[input.Length];
            for (int at = 0; at < data.Length; at += block)
            {
                int n = Math.Min(block, data.Length - at);
                try
                {
                    input.Position = at;
                    input.ReadExactly(data, at, n);
                }
                catch (IOException) { Array.Clear(data, at, n); }
            }
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static int BackupVerify(Ctx c, string dirArg)
    {
        string dir = Path.GetFullPath(dirArg);
        bool repair = c.Opt.Has("repair");
        int damaged = 0, lostForever = 0;

        foreach (string name in RepairNames(dir))
        {
            var r = Load(dir, name);
            if (r.Status == RsStatus.Ok) { Console.WriteLine($"{name}: OK"); continue; }
            if (r.Status == RsStatus.Unrecoverable)
            {
                Console.WriteLine($"{name}: UNRECOVERABLE ({r.BadData} damaged data block(s), {r.BadParity} damaged repair block(s) - more than the repair data can fix)");
                lostForever++;
                continue;
            }

            Console.WriteLine($"{name}: damaged but repairable ({r.BadData} data block(s), {r.BadParity} repair block(s))");
            if (repair)
            {
                File.WriteAllBytes(Path.Combine(dir, name), r.Data);
                if (r.Parity != null) File.WriteAllBytes(Path.Combine(dir, name + ".par"), r.Parity);
                Console.WriteLine("  repaired in place.");
            }
            else damaged++;
        }

        if (damaged > 0) Console.WriteLine("\nRun again with --repair to fix it in place.");
        return lostForever > 0 ? 3 : damaged > 0 ? 1 : 0;
    }

    /// <summary>
    /// Puts a backup back. Into an existing catalog (same catalog id): its current discloak.key and password stay. Into
    /// an empty folder: the backup's discloak.key is used (password, or --recovery). Everything is repaired, written to a
    /// side folder and checked with the key before the catalog is touched.
    /// </summary>
    static int BackupRestore(Ctx c, string dirArg)
    {
        string dir = Path.GetFullPath(dirArg);
        bool force = c.Opt.Has("force");
        string target = c.Cat.Dir;

        var loaded = new Dictionary<string, (byte[] Data, RsStatus Status)>(StringComparer.Ordinal);
        foreach (string name in RepairNames(dir))
        {
            var r = Load(dir, name);
            if (r.Status == RsStatus.Unrecoverable) throw new UserException($"{name} is damaged beyond repair; nothing was restored.");
            loaded[name] = (r.Data, r.Status);
        }
        // a backup made before 0.8.0 holds the key file under its old name, bdxl.key (see KeyFile.LegacyFileName)
        string keyName = loaded.ContainsKey(KeyFile.FileName) ? KeyFile.FileName : KeyFile.LegacyFileName;
        if (!loaded.TryGetValue(keyName, out var keyBytes) || !loaded.ContainsKey(Catalog.IntegrityFile))
            throw new UserException($"{dir} is not a backup of an encrypted catalog (no {KeyFile.FileName} or {Catalog.IntegrityFile}).");
        var backupKeyFile = JsonStore.ParseObject<KeyFile>(keyBytes.Data, KeyFile.FileName)
                            ?? throw new UserException($"{KeyFile.FileName} in the backup is empty.");

        CatalogSetup.RecoverInterruptedMigration(target);
        var kind = Catalog.Detect(target);
        if (kind == CatalogKind.Plain)
            throw new UserException($"{target} holds a catalog that is not encrypted; let discloak encrypt it first (any command), or move it away.");
        bool existing = kind == CatalogKind.Encrypted;
        KeyRing key;
        if (existing)
        {
            var current = KeyFile.ReadFrom(target)!;
            if (current.CatalogId != backupKeyFile.CatalogId)
                throw new UserException($"The backup belongs to another catalog (id {backupKeyFile.CatalogId}, not {current.CatalogId}); restore it into an empty folder.");
            if (!force) throw new UserException($"A catalog already exists in {target}; use --force to replace it (the current one is kept in history).");
            key = c.Opt.Has("recovery") ? CatalogSetup.UnlockWithRecoveryKey(current) : CatalogSetup.Unlock(target, c.Opt);
        }
        else
        {
            if (!KeyFile.IsRingFormat(backupKeyFile.Format)) throw new UserException($"{KeyFile.FileName} in the backup is not one this version understands.");
            key = c.Opt.Has("recovery")
                ? CatalogSetup.UnlockWithRecoveryKey(backupKeyFile)
                : CatalogSetup.UnlockWithPassword(backupKeyFile, c.Opt, "the backup's key file");
        }

        // write to a side folder and check it there with the key, so a bad backup leaves the catalog as it was
        string side = Path.Combine(target, ".restoring");
        if (Directory.Exists(side)) Directory.Delete(side, true);
        Directory.CreateDirectory(side);
        try
        {
            foreach (var (name, item) in loaded.Where(e => e.Key != keyName))
                File.WriteAllBytes(Path.Combine(side, name), item.Data);
            var check = Catalog.Load(side, "catalog-backup-restore", key);
            if (check.Integrity.State == IntegrityState.Broken)
                throw new UserException("The backup does not check out: " + string.Join("; ", check.Integrity.Problems) + ". Nothing was restored.");

            if (existing) Catalog.CreateSnapshot(target, "catalog-backup-restore", Math.Max(1, check.Settings.HistoryKeep));
            var inBackup = Catalog.StoredFilesIn(side).Append(Catalog.IntegrityFile).ToList();
            foreach (string name in inBackup)
            {
                File.Copy(Path.Combine(side, name), Path.Combine(target, name), overwrite: true);
                Console.WriteLine($"restored {name}" + (loaded[name].Status == RsStatus.Repaired ? "  (repaired from parity data)" : ""));
            }
            foreach (string name in Catalog.StoredFilesIn(target).Except(inBackup, StringComparer.Ordinal))
                File.Delete(Path.Combine(target, name));   // the backup did not have this file
            if (!existing) File.WriteAllBytes(KeyFile.NewPathIn(target), keyBytes.Data);
        }
        finally { Directory.Delete(side, true); }

        var after = Catalog.Load(target, "catalog-backup-restore", key);
        Console.WriteLine($"Catalog restored into {target}: {after.Files.Count:N0} files, {after.Discs.Count:N0} discs (integrity: {after.Integrity.State})." +
                          (existing ? " Its password stays as it was." : ""));
        if (after.Integrity.State == IntegrityState.Broken) return 4;
        try { if (Mirror.Sync(target)) Console.WriteLine("Its mirror was brought up to date too."); }
        catch (UserException ex) { Console.WriteLine(ex.Message); }   // not reachable: the next command says what to do
        return 0;
    }
}
