namespace Discloak;

/// <summary>
/// 'stage refresh &lt;folder&gt;': brings staged disc folders that are not burned yet up to date - the catalog backup
/// (_catalog), the program (_program), README.txt and discloak.key - without touching their packs, manifest,
/// packs.sha256 or disc.json (whose createdBy names the build that wrote the packs, which stays true). A folder gets
/// what it already has; --add-catalog and --add-program add what it lacks. Removing either is done by hand.
/// </summary>
public static class StageRefresh
{
    public const string Usage = "stage refresh <discFolder|stagingFolder> [--add-catalog] [--add-program] [--dry-run]";

    enum Outcome { Refreshed, Skipped, TooBig }

    public static int Run(Ctx c)
    {
        if (c.Args.Count != 2)
            throw new UserException($"Usage: {Usage}\n(To stage into a folder named 'refresh', write it as .\\refresh.)");
        string root = Path.GetFullPath(c.Args[1]);
        if (!Directory.Exists(root)) throw new UserException($"Not a folder: {root}");
        bool dryRun = c.Opt.Has("dry-run");
        var folders = IsDiscFolder(root) ? [root]
            : Directory.GetDirectories(root).Where(IsDiscFolder).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count == 0)
            throw new UserException($"{root} is not a disc folder and holds none (a disc folder has a {DiscFiles.InfoName}).");

        int refreshed = 0, skipped = 0, tooBig = 0;
        bool changed = false;
        foreach (string dir in folders)
        {
            if (Interrupt.Requested) break;
            Console.WriteLine($"{Path.GetFileName(dir)}  ({dir})");
            switch (Refresh(c, dir, dryRun, ref changed))
            {
                case Outcome.Refreshed: refreshed++; break;
                case Outcome.Skipped: skipped++; break;
                default: tooBig++; break;
            }
        }
        if (changed && !dryRun) c.Cat.Save();   // the space the discs take, as stage records it
        Console.WriteLine($"\n{(dryRun ? "Would refresh" : "Refreshed")} {refreshed:N0} folder(s)" +
                          (skipped > 0 ? $", skipped {skipped:N0} (not staged discs, see above)" : "") +
                          (tooBig > 0 ? $", left {tooBig:N0} as they were (the new files would not fit)" : "") +
                          (dryRun ? " (--dry-run: nothing was changed)." : "."));
        if (Interrupt.Requested) return Interrupt.ExitCode;
        return tooBig > 0 ? 2 : 0;
    }

    static bool IsDiscFolder(string dir) => File.Exists(Path.Combine(dir, DiscFiles.InfoName));

    /// <summary>What a subfolder takes on a disc, as DiscSpace.Folder counts it inside its parent (0 if there is none).</summary>
    static long Space(string dir) => Directory.Exists(dir) ? DiscSpace.Folder(dir) + DiscSpace.FolderEntry : 0;

    /// <summary>What a file takes on a disc (as DiscSpace.Folder counts it; a new one also its entry), 0 if there is none.</summary>
    static long FileSpace(long? length) => length is long n ? DiscSpace.RoundUp(n) + DiscSpace.Sector + DiscSpace.FolderEntry : 0;

    static Outcome Refresh(Ctx c, string dir, bool dryRun, ref bool changed)
    {
        static Outcome Skip(string why)
        {
            Console.WriteLine($"  skipped: {why}");
            return Outcome.Skipped;
        }

        // which disc, and is this folder still the one to burn?
        if (DiscFiles.ReadInfo(dir) is not { } info) return Skip($"its {DiscFiles.InfoName} cannot be read");
        if (info.CatalogId != null && !info.CatalogId.Equals(c.Keys.CatalogId, StringComparison.OrdinalIgnoreCase))
            return Skip("a disc of another catalog");
        var disc = c.Cat.Discs.FirstOrDefault(d => d.Label.Equals(info.Label, StringComparison.OrdinalIgnoreCase));
        if (disc == null) return Skip($"{info.Label} is not in the catalog (discarded?)");
        int session = Math.Max(1, info.Sessions);
        if (session == 1 ? disc.State != "staged" : disc.PendingAppend?.Number != session)
            return Skip(session == 1
                ? $"{disc.Label} is {disc.State}, not staged - only a disc that is not burned yet can change"
                : $"session {session} of {disc.Label} is not the staged one - only a session that is not burned yet can change");
        var packList = File.Exists(Path.Combine(dir, DiscFiles.PackListName))
            ? DiscFiles.ParsePackList(File.ReadAllText(Path.Combine(dir, DiscFiles.PackListName))) : [];
        if (packList.Count != disc.Packs.Count || disc.Packs.Where((sha, i) => !(packList.TryGetValue(i + 1, out string? got) && Hashing.SameHash(got, sha))).Any())
            return Skip($"its {DiscFiles.PackListName} does not match the catalog: it is not the current folder of {disc.Label} (one from before a discard or restage?)");
        string manifest = Path.Combine(dir, DiscFiles.ManifestFor(session));
        if (!File.Exists(manifest)) return Skip($"{DiscFiles.ManifestFor(session)} is missing");

        // what this session takes on the disc, and the room it has (as stage counted them)
        var pending = disc.PendingAppend;
        long before = session == 1 ? 0
            : pending!.OccupiedBytesBefore > 0 ? pending.OccupiedBytesBefore
            : pending.PacksBefore * DiscFormat.PackOnDisc + (session - 1) * (disc.Reserve ?? 0);
        int packs = session == 1 ? disc.Packs.Count : disc.Packs.Count - pending!.PacksBefore;
        long sessionSpace = packs * DiscFormat.PackOnDisc + DiscSpace.OnDisc(new FileInfo(manifest).Length);
        long? room = disc.Capacity is long capacity ? capacity - before : null;

        string catalogDir = Path.Combine(dir, DiscFiles.CatalogBackupName), programDir = Path.Combine(dir, DiscProgram.FolderName);
        bool catalog = Directory.Exists(catalogDir), addCatalog = !catalog && c.Opt.Has("add-catalog");
        bool program = Directory.Exists(programDir), addProgram = !program && c.Opt.Has("add-program");
        string? oldVersion = program ? DiscProgram.VersionIn(dir) : null;
        bool programCurrent = program && DiscProgram.IsCurrent(dir);
        string? cannot = (program && !programCurrent) || addProgram ? DiscProgram.Missing() : null;
        bool writeProgram = (program && !programCurrent || addProgram) && cannot == null;

        string readme = Path.Combine(dir, DiscFiles.ReadmeName), keyFile = KeyFile.NewPathIn(dir), catalogKey = KeyFile.PathIn(c.Cat.Dir);
        long? readmeBefore = File.Exists(readme) ? new FileInfo(readme).Length : null;
        long? keyBefore = File.Exists(keyFile) ? new FileInfo(keyFile).Length : null;
        bool readmeOld = readmeBefore == null || File.ReadAllText(readme) != DiscFiles.Readme;
        bool keyOld = keyBefore == null || !File.ReadAllBytes(keyFile).AsSpan().SequenceEqual(File.ReadAllBytes(catalogKey));

        string temp = Path.Combine(dir, ".refresh");   // the new folders are written here first, then swapped in
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        long folderBefore = DiscSpace.Folder(dir);
        try
        {
            long catalogSpace = Space(catalogDir), programSpace = Space(programDir);
            long newCatalog = catalogSpace, newProgram = programSpace;
            if (catalog || addCatalog)
            {
                CatalogCommand.WriteBackup(c, Path.Combine(temp, DiscFiles.CatalogBackupName), quiet: true);
                newCatalog = Space(Path.Combine(temp, DiscFiles.CatalogBackupName));
            }
            if (writeProgram)
            {
                DiscProgram.Write(temp);
                newProgram = Space(Path.Combine(temp, DiscProgram.FolderName));
            }
            long newReadme = Encoding.UTF8.GetByteCount(DiscFiles.Readme), newKey = new FileInfo(catalogKey).Length;
            long folderAfter = folderBefore - catalogSpace - programSpace + newCatalog + newProgram
                               - FileSpace(readmeBefore) + FileSpace(newReadme) - FileSpace(keyBefore) + FileSpace(newKey);

            Console.WriteLine("  " + (catalog ? "_catalog       rewritten: the catalog as it is now"
                                     : addCatalog ? "_catalog       added (--add-catalog): the catalog as it is now"
                                     : "-  _catalog     none in this folder (--add-catalog adds one)"));
            Console.WriteLine("  " + (programCurrent ? $"_program       already this program ({oldVersion ?? BuildInfo.Version})"
                                     : cannot != null ? $"_program       left as it is: this exe cannot write it ({cannot})"
                                     : program ? $"_program       replaced: {oldVersion ?? "an unknown version"} -> {BuildInfo.Full}"
                                     : addProgram ? $"_program       added (--add-program): {BuildInfo.Full}"
                                     : "-  _program     none in this folder (--add-program adds it)"));
            Console.WriteLine("  " + (readmeOld ? "README.txt     rewritten" : "README.txt     unchanged"));
            Console.WriteLine("  " + (keyOld ? "discloak.key   rewritten: the catalog's key file as it is now (a new password since staging?)" : "discloak.key   unchanged"));
            if (room is long limit && folderAfter > limit)
            {
                Console.WriteLine($"  NOT REFRESHED: the folder would take {SizeText.Bytes(folderAfter)} bytes, more than the " +
                                  $"{SizeText.Bytes(limit)} the disc has for it (the catalog backup outgrew the reserve?). " +
                                  "Nothing was changed; remove _catalog by hand to refresh the rest.");
                return Outcome.TooBig;
            }
            Console.WriteLine($"  space          {SizeText.Bytes(folderAfter)} bytes" + (room is long r ? $" of {SizeText.Bytes(r)} the disc has for it" : ""));
            if (dryRun) return Outcome.Refreshed;

            if (catalog || addCatalog)
            {
                if (catalog) Directory.Delete(catalogDir, true);
                Directory.Move(Path.Combine(temp, DiscFiles.CatalogBackupName), catalogDir);
            }
            if (writeProgram)
            {
                if (program) Directory.Delete(programDir, true);
                Directory.Move(Path.Combine(temp, DiscProgram.FolderName), programDir);
            }
            if (readmeOld) File.WriteAllText(readme, DiscFiles.Readme);
            if (keyOld) File.Copy(catalogKey, keyFile, overwrite: true);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }

        // the space the disc takes, counted as stage counts it: packs, manifest, program and reserve - or the whole
        // folder, if its metadata came out larger than the reserve
        long folder = DiscSpace.Folder(dir);
        long occupied = before + Math.Max(sessionSpace + Space(programDir) + (disc.Reserve ?? 0), folder);
        if (disc.OccupiedBytes != occupied)
        {
            disc.OccupiedBytes = occupied;
            changed = true;
        }
        return Outcome.Refreshed;
    }
}
