namespace Bdxl;

/// <summary>
/// catalog check | accept | history | rollback &lt;id&gt; | backup create &lt;dir&gt; | backup verify &lt;dir&gt; [--repair] | backup restore &lt;dir&gt; [--force]
/// Integrity and versions of the catalog itself, and catalog backups with Reed-Solomon repair data.
/// </summary>
public sealed class CatalogCommand : ICommand
{
    public string Name => "catalog";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Integrity check, older versions, backups with repair data";
    public string Usage => "catalog check | accept | history | rollback <id> | backup create <dir> | backup verify <dir> [--repair] | backup restore <dir> [--force]";
    public string Details => """
        The catalog is checked against catalog.hash.json every time it is loaded; if it was changed outside bdxl or
        is damaged, other commands refuse to run.
          catalog check              verify only, nothing else
          catalog accept             you edited the .jsonl files by hand: record the new hashes (previous state kept)
          catalog history            older versions (one copy is kept before each command that changes the catalog)
          catalog rollback <id>      go back to one of them (the current state is saved first)
          catalog backup create <dir>            copy + Reed-Solomon repair data (--parity-percent 25)
          catalog backup verify <dir> [--repair] check / fix a backup
          catalog backup restore <dir> [--force] put a backup back
        """;
    public bool AllowBrokenCatalog => true;

    public int Run(Ctx c)
    {
        string sub = c.Args.Count > 0 ? c.Args[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "check": return Check(c);
            case "accept": return Accept(c);
            case "history": return History(c);
            case "rollback": return Rollback(c);
            case "backup":
                string action = c.Args.Count > 1 ? c.Args[1].ToLowerInvariant() : "";
                if (c.Args.Count != 3) throw new UserException("Usage: catalog backup create <dir> | verify <dir> [--repair] | restore <dir> [--force]");
                if (action == "create") return BackupCreate(c, c.Args[2]);
                if (action == "verify") return BackupVerify(c, c.Args[2]);
                if (action == "restore") return BackupRestore(c, c.Args[2]);
                throw new UserException("Usage: catalog backup create <dir> | verify <dir> [--repair] | restore <dir> [--force]");
            default:
                throw new UserException("Usage: catalog check | accept | history | rollback <id> | backup create|verify|restore <dir>");
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
                Console.WriteLine("No catalog yet (nothing to check).");
                return 0;
            case IntegrityState.Legacy:
                Console.WriteLine("The catalog has no integrity hashes yet (it was created by an older version). They will be written on the next change.");
                Console.WriteLine($"{c.Cat.Files.Count:N0} files, {c.Cat.Discs.Count:N0} discs, {c.Cat.Blanks.Count:N0} blank discs loaded fine.");
                return 0;
            case IntegrityState.Ok:
                Console.WriteLine($"OK - every catalog file matches the hash recorded on {report.RecordedUtc:yyyy-MM-dd HH:mm} UTC.");
                Console.WriteLine($"{c.Cat.Files.Count:N0} files, {c.Cat.Discs.Count:N0} discs, {c.Cat.Blanks.Count:N0} blank discs, {c.Settings.IgnoreRules.Count} ignore rules.");
                return 0;
            default:
                Console.WriteLine("BROKEN - commands that use the catalog are blocked until this is resolved:");
                foreach (string p in report.Problems) Console.WriteLine("  " + p);
                var versions = Catalog.ListHistory(c.Cat.Dir);
                Console.WriteLine("\nWays out:");
                Console.WriteLine("  catalog history / catalog rollback <id>    go back to an earlier version" + (versions.Count == 0 ? " (none exist yet)" : $" ({versions.Count} available)"));
                Console.WriteLine("  catalog backup restore <dir> --force       restore from a catalog backup");
                Console.WriteLine("  catalog accept                             you edited the files by hand and they are fine");
                return 4;
        }
    }

    /// <summary>The user vouches for hand-made edits: parse the files, then record new hashes.</summary>
    static int Accept(Ctx c)
    {
        var cat = Catalog.Load(c.Cat.Dir, "catalog-accept", ignoreIntegrity: true);
        if (cat.Integrity.Problems.Any(p => p.StartsWith("Cannot be parsed", StringComparison.Ordinal)))
            throw new UserException(cat.Integrity.Problems.First(p => p.StartsWith("Cannot be parsed", StringComparison.Ordinal)));
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
            var problems = Catalog.VerifySnapshot(c.Cat.Dir, id);
            Console.WriteLine($"  {id}   {(problems.Count == 0 ? "hash OK" : "PROBLEM: " + problems[0])}");
        }
        Console.WriteLine("\nThe name shows when it was saved and which command was about to change the catalog.");
        return 0;
    }

    static int Rollback(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: catalog rollback <id>   (ids: catalog history)");
        string id = c.Args[1];
        var problems = Catalog.VerifySnapshot(c.Cat.Dir, id);
        if (problems.Count > 0 && !c.Opt.Has("force"))
            throw new UserException($"Version {id} cannot be trusted: {problems[0]}. Use --force to restore it anyway.");
        if (!Ui.Confirm(c.Opt, $"Replace the current catalog with version {id}? (the current one is saved to history first)")) { Console.WriteLine("Cancelled."); return 1; }

        Catalog.RestoreSnapshot(c.Cat.Dir, id, "catalog-rollback", Math.Max(1, c.Settings.HistoryKeep));
        var after = Catalog.Load(c.Cat.Dir, "catalog-rollback");
        Console.WriteLine($"Restored version {id}: {after.Files.Count:N0} files, {after.Discs.Count:N0} discs (integrity: {after.Integrity.State}).");
        return after.Integrity.State == IntegrityState.Broken ? 4 : 0;
    }

    // ---- backup (copy of the catalog + repair data) ---------------------------------------

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

    /// <summary>Copies the catalog files into dir and adds .repair.json + .par (Reed-Solomon) next to each. Also used by 'stage --with-catalog'.</summary>
    public static void WriteBackup(Ctx c, string dir)
    {
        int pct = c.Opt.GetInt("parity-percent", 25);
        if (pct < 1 || pct > 50) throw new UserException("--parity-percent must be between 1 and 50.");
        Directory.CreateDirectory(dir);

        foreach (string name in Catalog.CoveredFiles)
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
            "files.jsonl, discs.jsonl, blanks.jsonl, settings.json   the catalog (JSON, readable in any text editor)\n" +
            "*.repair.json, *.par       repair data: per-block SHA-256 + Reed-Solomon parity blocks (GF(256), Cauchy matrix)\n\n" +
            "Check:    bdxl catalog backup verify <this folder>\n" +
            "Repair:   bdxl catalog backup verify <this folder> --repair\n" +
            "Restore:  bdxl catalog backup restore <this folder> --catalog <catalog folder> [--force]\n");
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

    static int BackupRestore(Ctx c, string dirArg)
    {
        string dir = Path.GetFullPath(dirArg);
        bool force = c.Opt.Has("force");
        string target = c.Cat.Dir;
        var loaded = new List<(string Name, byte[] Data, RsStatus Status)>();

        foreach (string name in RepairNames(dir))
        {
            var r = Load(dir, name);
            if (r.Status == RsStatus.Unrecoverable) throw new UserException($"{name} is damaged beyond repair; nothing was restored.");
            loaded.Add((name, r.Data, r.Status));
        }

        bool existing = Catalog.CoveredFiles.Any(n => File.Exists(Path.Combine(target, n)));
        if (existing && !force)
            throw new UserException($"A catalog already exists in {target}; use --force to replace it (the current one is kept in history).");

        if (existing) Catalog.CreateSnapshot(target, "catalog-backup-restore", Math.Max(1, c.Settings.HistoryKeep));
        Directory.CreateDirectory(target);
        foreach (string name in Catalog.CoveredFiles)
        {
            var item = loaded.FirstOrDefault(x => x.Name == name);
            string path = Path.Combine(target, name);
            if (item.Name == null) { if (File.Exists(path)) File.Delete(path); continue; }   // the backup did not have this file
            File.WriteAllBytes(path, item.Data);
            Console.WriteLine($"restored {name}" + (item.Status == RsStatus.Repaired ? "  (repaired from parity data)" : ""));
        }
        Catalog.WriteIntegrity(target);

        var after = Catalog.Load(target, "catalog-backup-restore");
        Console.WriteLine($"Catalog restored into {target}: {after.Files.Count:N0} files, {after.Discs.Count:N0} discs (integrity: {after.Integrity.State}).");
        return after.Integrity.State == IntegrityState.Broken ? 4 : 0;
    }
}
