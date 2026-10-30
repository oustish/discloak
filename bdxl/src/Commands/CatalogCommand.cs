namespace Bdxl;

/// <summary>
/// catalog check | accept | history | rollback &lt;id&gt; | export &lt;dir&gt; | import &lt;dir&gt; |
/// backup create &lt;dir&gt; | backup verify &lt;dir&gt; [--repair] | backup restore &lt;dir&gt; [--force]
/// Integrity and versions of the catalog itself, plaintext export/import, and catalog backups with Reed-Solomon repair data.
/// </summary>
public sealed class CatalogCommand : ICommand
{
    public string Name => "catalog";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Integrity check, older versions, export/import, backups with repair data";
    public string Usage => "catalog check | accept | history | rollback <id> | export <dir> | import <dir> | backup create <dir> | backup verify <dir> [--repair] | backup restore <dir> [--force]";
    public string Details => """
        The catalog is stored encrypted. catalog.hash.json holds a hash of every stored file and an HMAC over that
        list, checked every time the catalog is loaded; if a file is damaged, altered or mixed in from another save,
        other commands refuse to run.
          catalog check              verify only, nothing else
          catalog accept             every file still decrypts but the hash list does not match: record it anew (previous state kept)
          catalog history            older versions (one copy is kept before each command that changes the catalog)
          catalog rollback <id>      go back to one of them (the current state is saved first; --force for a damaged one)
          catalog export <dir>       write the catalog as plain JSON into an empty folder (to read or edit it - it is NOT encrypted)
          catalog import <dir>       replace the catalog with plain JSON from such a folder (the current state is kept in history)
          catalog backup create <dir>            copy of the encrypted catalog + bdxl.key + Reed-Solomon repair data (--parity-percent 25)
          catalog backup verify <dir> [--repair] check / fix a backup (no password needed)
          catalog backup restore <dir> [--force] put a backup back (password, or --recovery for the recovery key, when restoring
                                                 into an empty folder; into this catalog, the current bdxl.key and password stay)
        Every staged disc with --with-catalog carries the same kind of backup in _catalog.
        """;
    public bool AllowBrokenCatalog => true;

    public bool NeedsCatalogFor(Options opt) =>
        !(opt.Args.Count > 1 && opt.Args[0].Equals("backup", StringComparison.OrdinalIgnoreCase) &&
          (opt.Args[1].Equals("verify", StringComparison.OrdinalIgnoreCase) || opt.Args[1].Equals("restore", StringComparison.OrdinalIgnoreCase)));

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
        var after = Catalog.Load(c.Cat.Dir, "catalog-rollback", c.Keys);
        Console.WriteLine($"Restored version {id}: {after.Files.Count:N0} files, {after.Discs.Count:N0} discs (integrity: {after.Integrity.State}).");
        return after.Integrity.State == IntegrityState.Broken ? 4 : 0;
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
        Console.WriteLine("These files are NOT encrypted: they show every path and hash. Delete them when you are done, or bring");
        Console.WriteLine($"edits back with:  catalog import \"{dir}\"");
        return 0;
    }

    static int Import(Ctx c, string dirArg)
    {
        string dir = Path.GetFullPath(dirArg);
        if (!Catalog.CoveredFiles.Any(n => File.Exists(Path.Combine(dir, n))))
            throw new UserException($"{dir} has none of {string.Join(", ", Catalog.CoveredFiles)}.");
        var files = JsonStore.ReadLines<FileRecord>(Path.Combine(dir, "files.jsonl"));
        var discs = JsonStore.ReadLines<DiscRecord>(Path.Combine(dir, "discs.jsonl"));
        var blanks = JsonStore.ReadLines<BlankDisc>(Path.Combine(dir, "blanks.jsonl"));
        var settings = JsonStore.ReadObject<Settings>(Path.Combine(dir, "settings.json")) ?? new Settings();

        Console.WriteLine($"{dir}: {files.Count:N0} files, {discs.Count:N0} discs, {blanks.Count:N0} blank discs.");
        Console.WriteLine($"The catalog now has {c.Cat.Files.Count:N0} files, {c.Cat.Discs.Count:N0} discs, {c.Cat.Blanks.Count:N0} blank discs" +
                          (c.Cat.Integrity.State == IntegrityState.Broken ? " (it is broken)." : "."));
        if (!Ui.Confirm(c.Opt, "Replace the catalog with the imported files? (the current one is kept in history)")) { Console.WriteLine("Cancelled."); return 1; }
        c.Cat.ReplaceContents(files, discs, blanks, settings);
        c.Cat.TrustCurrentContents();
        c.Cat.Save();
        Console.WriteLine($"Imported. The plain JSON in {dir} is still there - delete it when you no longer need it.");
        return 0;
    }

    // ---- backup (copy of the encrypted catalog + bdxl.key + repair data) ---------------------

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

    /// <summary>The files a catalog backup holds: the encrypted catalog, its hash list and bdxl.key.</summary>
    static IEnumerable<string> BackupFiles => Catalog.StoredFiles.Append(Catalog.IntegrityFile).Append(KeyFile.FileName);

    /// <summary>
    /// Copies the encrypted catalog, its hash list and bdxl.key into dir and adds .repair.json + .par (Reed-Solomon)
    /// next to each. Also used by 'stage --with-catalog'. Nothing is decrypted: creating, checking and repairing a
    /// backup need no password.
    /// </summary>
    public static void WriteBackup(Ctx c, string dir)
    {
        int pct = c.Opt.GetInt("parity-percent", 25);
        if (pct < 1 || pct > 50) throw new UserException("--parity-percent must be between 1 and 50.");
        Directory.CreateDirectory(dir);

        foreach (string name in BackupFiles)
        {
            string src = Path.Combine(c.Cat.Dir, name);
            if (!File.Exists(src)) continue;
            byte[] data = File.ReadAllBytes(src);
            var meta = Rs.Protect(name, data, pct, out byte[] parity);
            File.WriteAllBytes(Path.Combine(dir, name), data);
            File.WriteAllBytes(Path.Combine(dir, name + ".par"), parity);
            JsonStore.WriteObject(Path.Combine(dir, name + ".repair.json"), meta);
            Console.WriteLine($"  {name}: {SizeText.Bytes(data.Length)} bytes + {SizeText.Bytes(parity.Length)} bytes repair data " +
                              $"(can rebuild up to {meta.ParityBlocks} damaged blocks of {SizeText.Bytes(meta.BlockSize)} bytes)");
        }

        File.WriteAllText(Path.Combine(dir, "README.txt"),
            $"BDXL catalog backup, created {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.\n\n" +
            "files.jsonl.enc, discs.jsonl.enc, blanks.jsonl.enc, settings.json.enc   the catalog, ENCRYPTED with the catalog's\n" +
            "    master key (AES-256-GCM, bdxl's BDXLENC2 format)\n" +
            "catalog.hash.json          SHA-256 of each file above and an HMAC over that list\n" +
            "bdxl.key                   the master key, wrapped by the password (Argon2id) and, if one was made, by the recovery key\n" +
            "*.repair.json, *.par       repair data: per-block SHA-256 + Reed-Solomon parity blocks (GF(256), Cauchy matrix),\n" +
            "    computed over the stored bytes: checking and repairing need no password\n\n" +
            "Check:    bdxl catalog backup verify <this folder>\n" +
            "Repair:   bdxl catalog backup verify <this folder> --repair\n" +
            "Restore:  bdxl catalog backup restore <this folder> --catalog <catalog folder> [--force] [--recovery]\n");
    }

    static string[] RepairNames(string dir)
    {
        const string suffix = ".repair.json";
        if (!Directory.Exists(dir)) throw new UserException($"Not a folder: {dir}");
        var names = Directory.GetFiles(dir, "*" + suffix).Select(p => Path.GetFileName(p)[..^suffix.Length])
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (names.Length == 0) throw new UserException($"No *.repair.json files in {dir}.");
        return names;
    }

    static (RsStatus Status, byte[] Data, byte[]? Parity, int BadData, int BadParity) Load(string dir, string name)
    {
        var meta = JsonStore.ReadObject<RepairMeta>(Path.Combine(dir, name + ".repair.json"))
                   ?? throw new UserException($"Unreadable repair file for {name}.");
        string dataPath = Path.Combine(dir, name), parPath = Path.Combine(dir, name + ".par");
        byte[]? data = File.Exists(dataPath) ? File.ReadAllBytes(dataPath) : null;
        byte[]? par = File.Exists(parPath) ? File.ReadAllBytes(parPath) : null;
        return Rs.Check(meta, data, par);
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
    /// Puts a backup back. Into an existing catalog (same catalog id): its current bdxl.key and password stay. Into
    /// an empty folder: the backup's bdxl.key is used (password, or --recovery). Everything is repaired, written to a
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
        if (!loaded.TryGetValue(KeyFile.FileName, out var keyBytes) || !loaded.ContainsKey(Catalog.IntegrityFile))
            throw new UserException($"{dir} is not a backup of an encrypted catalog (no {KeyFile.FileName} or {Catalog.IntegrityFile}).");
        var backupKeyFile = JsonStore.ParseObject<KeyFile>(keyBytes.Data, KeyFile.FileName)
                            ?? throw new UserException($"{KeyFile.FileName} in the backup is empty.");

        CatalogSetup.RecoverInterruptedMigration(target);
        var kind = Catalog.Detect(target);
        if (kind == CatalogKind.Plain)
            throw new UserException($"{target} holds a catalog that is not encrypted; let bdxl encrypt it first (any command), or move it away.");
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
            if (backupKeyFile.Format != KeyFile.CurrentFormat) throw new UserException($"{KeyFile.FileName} in the backup is not one this version understands.");
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
            foreach (var (name, item) in loaded.Where(e => e.Key != KeyFile.FileName))
                File.WriteAllBytes(Path.Combine(side, name), item.Data);
            var check = Catalog.Load(side, "catalog-backup-restore", key);
            if (check.Integrity.State == IntegrityState.Broken)
                throw new UserException("The backup does not check out: " + string.Join("; ", check.Integrity.Problems) + ". Nothing was restored.");

            if (existing) Catalog.CreateSnapshot(target, "catalog-backup-restore", Math.Max(1, check.Settings.HistoryKeep));
            foreach (string name in Catalog.StoredFiles.Append(Catalog.IntegrityFile))
            {
                string from = Path.Combine(side, name), to = Path.Combine(target, name);
                if (File.Exists(from))
                {
                    File.Copy(from, to, overwrite: true);
                    Console.WriteLine($"restored {name}" + (loaded[name].Status == RsStatus.Repaired ? "  (repaired from parity data)" : ""));
                }
                else if (File.Exists(to)) File.Delete(to);   // the backup did not have this file
            }
            if (!existing) File.WriteAllBytes(KeyFile.PathIn(target), keyBytes.Data);
        }
        finally { Directory.Delete(side, true); }

        var after = Catalog.Load(target, "catalog-backup-restore", key);
        Console.WriteLine($"Catalog restored into {target}: {after.Files.Count:N0} files, {after.Discs.Count:N0} discs (integrity: {after.Integrity.State})." +
                          (existing ? " Its password stays as it was." : ""));
        return after.Integrity.State == IntegrityState.Broken ? 4 : 0;
    }
}
