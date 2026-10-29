namespace Bdxl;

public sealed class CommandSpec
{
    public CommandSpec(string name, string group, string summary, string usage, string details, Func<Ctx, int> run,
        bool allowBrokenCatalog = false, bool needsCatalog = true)
    {
        Name = name; Group = group; Summary = summary; Usage = usage; Details = details; Run = run;
        AllowBrokenCatalog = allowBrokenCatalog; NeedsCatalog = needsCatalog;
    }

    public string Name { get; }
    public string Group { get; }
    public string Summary { get; }
    public string Usage { get; }
    public string Details { get; }
    public Func<Ctx, int> Run { get; }
    public bool AllowBrokenCatalog { get; }   // may run when the catalog failed its integrity check
    public bool NeedsCatalog { get; }         // false: does not read the catalog at all
}

/// <summary>Every command in one place: used for dispatch, "bdxl help" and "bdxl &lt;command&gt; --help".</summary>
public static class CommandTable
{
    public static readonly List<CommandSpec> All = new()
    {
        // ---- indexing ----
        new CommandSpec("scan", "Indexing and planning",
            "Index files (a folder, or every known location)",
            "scan [<folder>] [--name <alias>] [--threads N] [--full]",
            """
            With a folder: adds it to the catalog (first time) or rescans it. Every file gets a SHA-256 hash; unchanged
            files (same size and modified time) are not read again. Files that disappeared are marked as missing.
            Without a folder: rescans ALL known locations. A location that is not reachable (unplugged drive) is
            skipped, nothing is marked as missing.

              --name <alias>   name of the folder on discs and in the catalog (default: the folder name)
              --threads N      hash with N threads (default: 'settings' default-threads, 1)
              --full           ignore size/time and hash everything again
              --catalog <dir>  where the catalog lives (default ./catalog)

            Files matching an ignore rule (see 'ignore') are added to the catalog and marked as ignored, but are not
            hashed and never staged.
            """,
            ScanCommand.Run),

        new CommandSpec("plan", "Indexing and planning",
            "Show which files would go on which blank disc (changes nothing)",
            "plan [--encrypt] [--split]",
            """
            Allocates all pending files to the blank discs registered with 'blanks'. Prints how full each disc would be
            and, if the data does not fit on the designated discs, which files are left over and roughly how many
            more discs are needed. Exit code 2 = not everything fits.
              --encrypt   count the small overhead of encrypted files
              --split     cut files larger than every blank disc into parts (see 'stage --help')
            Pending = on the source, not ignored, and no live disc holds identical content (hash-based dedup).
            """,
            PlanCommand.Run),

        new CommandSpec("blanks", "Indexing and planning",
            "Register the blank discs you own (sizes, reserve, price)",
            "blanks [list] | add <count> [--size S] [--reserve R] [--cost P] | reserve <R> [--size S] | cost <P> [--size S] | remove <id|all> [--size S]",
            """
              blanks add 5 --size 96882130944 --reserve 200MB --cost 45    five discs; sizes accept 25GB, 90GiB, bytes ...
              blanks reserve 300MB            change the reserve of ALL blank discs   (--size S: only discs of that size)
              blanks cost 39.99               change the price of all blank discs
              blanks remove B0003 | all       forget a blank disc
            The reserve is space deliberately left free (filesystem overhead, a catalog backup with --with-catalog).
            A blank disc is used up when a disc is staged from it; 'discard' gives it back.
            """,
            BlankCommands.Run),

        new CommandSpec("ignore", "Indexing and planning",
            "Define ignored files and folders (.gitignore syntax)",
            "ignore [list] | add <pattern>... | remove <pattern|number>... | test <path> | apply",
            """
            Rules live in settings.json inside the catalog and are matched against paths relative to each scanned folder.
              ignore add "node_modules/" "*.tmp" "/cache/**" "!keep.tmp"
              ignore test photos/2020/Thumbs.db
            Ignored files stay in the catalog (marked 'ignored') but are not hashed and never staged. When a rule starts
            matching files that are already catalogued:
              - not on any disc: they are marked ignored and will not be staged;
              - already burned: no new backup is made, but 'restage' of the old disc still includes them.
            Changing rules re-evaluates the catalog immediately ('ignore apply' repeats that).
            """,
            IgnoreCommand.Run),

        // ---- making discs ----
        new CommandSpec("stage", "Making discs",
            "Copy the next disc's worth of files into a folder, ready to burn",
            "stage <destDir> [--max-discs N] [--min-fill 0.9] [--encrypt] [--password-env VAR] [--split] [--with-catalog]",
            """
            Creates <destDir>/<label>/ for each disc, copying files as they are (no archive, no compression) and checking
            every copy against its hash. Also writes manifest.jsonl and disc.json so the disc describes itself.
              --max-discs N    stage up to N discs (default 1)
              --min-fill 0.9   only stage discs that would be at least 90% full
              --encrypt        encrypt every file (AES-256-GCM, one .enc file per file). The password comes from the
                               environment variable BDXL_PASSWORD (or --password-env VAR) or is typed in. It needs at
                               least 12 characters and 3 of: lowercase, uppercase, digit, special character.
                               File NAMES stay readable in manifest.jsonl.
              --split          a file larger than every blank disc is cut into parts, one per disc: name.001, name.002 ...
                               (each part is encrypted on its own with --encrypt). Without bdxl the parts are simply
                               joined in order (copy /b a.001+a.002 a). 'restore' collects the parts disc by disc and
                               joins them; the file counts as backed up only when every part is on a disc. Once a file
                               has parts on discs, its remaining parts are staged even without --split.
              --with-catalog   also put a catalog backup (with repair data) into <disc>/_catalog
            Labels are <prefix><counter>, e.g. BDXL-0007 (see 'settings').
            """,
            StageCommand.Run),

        new CommandSpec("burned", "Making discs",
            "Mark a staged disc as burned",
            "burned <label> [--date yyyy-MM-dd]",
            "Records the burn date on the disc and on every file copy it holds. Run 'verify' first.",
            DiscCommands.Burned),

        new CommandSpec("verify", "Making discs",
            "Re-read a disc and compare it with the catalog",
            "verify <label> <mountedDiscPath> [--deep] [--password-env VAR]",
            """
            Hashes every file on the disc. Encrypted files are checked against the hash of the stored file, so no
            password is needed; --deep decrypts them as well and compares the original content.
            Entries recorded by 'scandisc' without hashing are marked 'unverified' until this has run.
            """,
            DiscCommands.Verify),

        new CommandSpec("discard", "Making discs",
            "Forget a staged disc that was never burned",
            "discard <label>",
            "Its files become pending again and its blank disc goes back to the inventory. The staged folder is not deleted.",
            DiscCommands.Discard),

        // ---- existing discs ----
        new CommandSpec("scandisc", "Existing discs",
            "Record an already burned disc in the catalog",
            "scandisc <mountPath> [--label L] [--method auto|manifest|hash|quick] [--date D] [--capacity S] [--cost P] [--update] [--dry-run] [--write-disc-json]",
            """
            Reads a mounted disc (or a folder laid out like one) and links its files to the catalog.
              --method manifest  trust the disc's manifest.jsonl (fast; entries stay 'unverified' until 'verify')
              --method hash      read and SHA-256 every file (slow, authoritative, finds files nobody listed)
              --method quick     no hashing: match path + size + time against the catalog
              --method auto      manifest if the disc has one, otherwise hash (default)
            The label comes from --label, else disc.json, else the drive's volume label.
            Files the catalog does not know yet get new entries. --update merges into a disc already in the catalog.
            --write-disc-json writes disc.json (+ manifest.jsonl) onto the disc if it can still be written to
            (open session / not finalized); otherwise the label is recorded in the catalog only.
            """,
            DiscImportCommands.ScanDisc),

        new CommandSpec("discjson", "Existing discs",
            "Write disc.json onto a disc that can still be written to",
            "discjson <label> <mountPath> [--overwrite]",
            "For a disc already in the catalog. Fails with an explanation if the disc is finalized or read-only.",
            DiscImportCommands.DiscJson),

        // ---- looking things up ----
        new CommandSpec("status", "Looking things up",
            "Summary of sources, pending data, blank discs and discs",
            "status", "Also warns about files that exist nowhere any more.",
            InfoCommands.Status),

        new CommandSpec("find", "Looking things up",
            "Where is this file backed up?",
            "find <text|hashPrefix|*>",
            "Matches part of a path, or the start of a SHA-256. Shows every disc holding the file, when, and whether it is the current version.",
            InfoCommands.Find),

        new CommandSpec("discs", "Looking things up",
            "List all discs and their state",
            "discs", "State is staged, burned, lost or destroyed. Shows encryption, cost and notes.",
            DiscCommands.Discs),

        new CommandSpec("cost", "Looking things up",
            "What the discs cost: per disc, per GB and per TB",
            "cost [report] | cost set <label> <amount>",
            """
            Prices come from 'blanks add --cost', 'blanks cost', 'scandisc --cost' and 'cost set <label> <amount>' (also for
            discs you bought earlier). 'cost report' shows cost per disc, per GB and per TB of data stored and of
            capacity, and the value of the blank discs in stock. GB = 10^9 bytes, TB = 10^12 bytes.
            """,
            CostCommand.Run),

        // ---- recovery ----
        new CommandSpec("lost", "Recovery",
            "Mark a disc as lost (it may turn up again)",
            "lost <label> [--reason text]",
            "Copies on it stop counting. Files still on the source become pending again; files that exist nowhere else are listed as DATA LOST.",
            c => DiscCommands.MarkDead(c, "lost")),

        new CommandSpec("destroyed", "Recovery",
            "Mark a disc as destroyed (final)",
            "destroyed <label> [--reason text]",
            "Same as 'lost' but cannot be undone.",
            c => DiscCommands.MarkDead(c, "destroyed")),

        new CommandSpec("found", "Recovery",
            "A disc marked lost turned up",
            "found <label>", "Puts a lost disc back to burned / staged. Check it with 'verify'.",
            DiscCommands.Found),

        new CommandSpec("restage", "Recovery",
            "Rebuild a disc's folder (same label, same files) to burn it again",
            "restage <label> <destDir> [--from dirA;dirB] [--allow-partial] [--password-env VAR]",
            """
            Takes the data from the current source files (if their hash still matches) or from the --from folders, checks
            every file against the catalog and writes the folder again. Files that are ignored now are included: the
            disc is rebuilt the way it was. Encrypted discs: intact encrypted copies are reused, other files are
            encrypted again with a password you type. Afterwards the disc is 'staged' until you run 'burned'.
            Without --allow-partial nothing is staged if any file cannot be rebuilt.
            """,
            RestageRestoreCommands.Restage),

        new CommandSpec("restore", "Recovery",
            "Copy files back from mounted discs into a folder",
            "restore <destDir> <text|hashPrefix|*> --mount F:\\;G:\\ [--disc label] [--source alias] [--flat] [--overwrite]",
            """
            Finds the files in the catalog, tells you which discs it needs, and copies them from the discs you mounted.
            Discs are recognised by disc.json (or --disc for a single mount). Every file is verified by hash; encrypted
            files are decrypted (password from BDXL_PASSWORD or typed). Layout: <destDir>/<alias>/<path>, or --flat.
            """,
            RestageRestoreCommands.Restore),

        // ---- catalog ----
        new CommandSpec("catalog", "The catalog itself",
            "Integrity check, older versions, backups with repair data",
            "catalog check | accept | history | rollback <id> | backup create <dir> | backup verify <dir> [--repair] | backup restore <dir> [--force]",
            """
            The catalog is checked against catalog.hash.json every time it is loaded; if it was changed outside bdxl or
            is damaged, other commands refuse to run.
              catalog check              verify only, nothing else
              catalog accept             you edited the .jsonl files by hand: record the new hashes (previous state kept)
              catalog history            older versions (one copy is kept before each command that changes the catalog)
              catalog rollback <id>      go back to one of them (the current state is saved first)
              catalog backup create <dir>            copy + Reed-Solomon repair data (--parity-percent 25)
              catalog backup verify <dir> [--repair] check / fix a backup
              catalog backup restore <dir> [--force] put a backup back
            """,
            CatalogCommand.Run, allowBrokenCatalog: true),

        new CommandSpec("purge", "The catalog itself",
            "Remove entries of files that are gone and were never burned",
            "purge [--dry-run] [--yes]",
            "Entries that have a copy on any disc are kept. The previous catalog version stays in history.",
            MaintenanceCommands.Purge),

        new CommandSpec("alias", "The catalog itself",
            "List or rename the names of scanned locations",
            "alias [list] | alias rename <old> <new>",
            "The alias is what 'scan --name' gave a folder. Renaming affects the catalog and future discs; discs already burned keep the old name on the disc.",
            MaintenanceCommands.Alias),

        new CommandSpec("settings", "The catalog itself",
            "Global settings (label prefix, currency, ...)",
            "settings [show] | settings set <key> <value>",
            """
            Keys: label-prefix, currency, history-keep, default-threads.
            label-prefix: new discs are named <prefix><counter>. The counter is global and never resets, so changing the
            prefix continues the numbering. Existing labels are not renamed.
            """,
            MaintenanceCommands.Settings),

        // ---- help ----
        new CommandSpec("workflow", "Help",
            "Show a sample workflow from first scan to a verified disc",
            "workflow", "Prints example commands only; nothing is executed.",
            WorkflowCommand.Run, allowBrokenCatalog: true, needsCatalog: false),

        new CommandSpec("help", "Help",
            "Show this list, or help for one command",
            "help [<command>]    (or: <command> --help)", "",
            HelpCommand.Run, allowBrokenCatalog: true, needsCatalog: false),
    };

    public static CommandSpec? Find(string name) =>
        All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public static class HelpCommand
{
    public static int Run(Ctx c)
    {
        if (c.Args.Count == 0) { PrintOverview(); return 0; }
        var spec = CommandTable.Find(c.Args[0]);
        if (spec == null)
        {
            Console.Error.WriteLine($"No command called '{c.Args[0]}'.");
            PrintOverview();
            return 1;
        }
        PrintCommand(spec);
        return 0;
    }

    public static void PrintOverview()
    {
        Console.WriteLine("bdxl - plan, stage and keep track of backups on BDXL discs (files are copied as they are: no archive, no compression)\n");
        Console.WriteLine("Usage:  bdxl <command> [arguments] [options]      bdxl <command> --help for details\n");
        foreach (var group in CommandTable.All.GroupBy(s => s.Group))
        {
            Console.WriteLine(group.Key + ":");
            foreach (var spec in group) Console.WriteLine($"  {spec.Name,-10} {spec.Summary}");
            Console.WriteLine();
        }
        Console.WriteLine("Option for every command:  --catalog <dir>   catalog folder (default ./catalog)");
        Console.WriteLine("New here? Run:  bdxl workflow");
    }

    public static void PrintCommand(CommandSpec spec)
    {
        Console.WriteLine($"bdxl {spec.Name} - {spec.Summary}\n");
        Console.WriteLine("Usage:  bdxl " + spec.Usage.Replace(" | ", "\n        bdxl " + spec.Name + " ") + "\n");
        if (spec.Details.Length > 0) Console.WriteLine(spec.Details.TrimEnd());
        Console.WriteLine("\nGeneral option:  --catalog <dir>   catalog folder (default ./catalog)");
    }
}
