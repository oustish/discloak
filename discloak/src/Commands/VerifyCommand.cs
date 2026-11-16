namespace Discloak;

public sealed class VerifyCommand : ICommand
{
    public string Name => "verify";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Re-read a disc and compare it with the catalog";
    public string Usage => "verify [<label>] <mountedDiscPath> [--deep] [--packs 312,680-690] [--files <text>] [--no-cache] [--retries N] [--mark-burned] [--as-disc]";
    public bool Interruptible => true;
    public string Details => """
        Reads a disc back and compares it with the catalog. Every pack's SHA-256 is compared with the catalog - nothing is
        decrypted, so no password is needed. --deep also decrypts every pack (each 1 MiB chunk is authenticated) and checks
        every file against its hash, in the same single read.
        The label can be left out: the disc names itself in its disc.json. Given, it is checked against it.

        The disc's other files are checked first, a line each:
          disc.json       which disc this is, and its key
          packs.sha256    the same pack hashes as the catalog
          manifests       their SHA-256 as the catalog has it; --deep also decrypts them and compares their files with the
                          catalog (and records the hashes of a disc staged before the catalog kept them)
          discloak.key    this catalog's
          README.txt, _program (the exe, its source zip) and _catalog (every file against its repair data)

        Reads that fail: a pack, chunk or header that cannot be read, or comes back wrong, is read again from the disc itself
        (past the Windows file cache) before it counts as damaged. One that comes right on a retry is printed as RE-READ: the
        data is intact, but it is a warning sign about the disc, the drive or its connection.

        A disc or a folder: what verify finds is recorded in the catalog only when it reads a disc in an optical drive. A
        folder elsewhere - the staging folder, a copy of a disc - is checked the same way, but nothing is recorded. The
        output says which it is.

        What it records (a disc only):
          - damaged copies: without --deep every file with data in a damaged pack, with --deep only the files that do not
            come back intact. They no longer count as backups (files still on their source are pending again); restore tries
            them last.
          - a mark goes again, with a warning, once the packs that read back wrong read back as the catalog has them (--packs
            of just those is enough), or --deep checks the whole file intact. Damage --deep finds in packs that read back
            exactly as staged was already in what was staged: only --deep clears it.
          - copies recorded by 'scandisc' stop being 'unverified' once their packs check out.

        What to do about damage (verify says it too): a disc not marked as burned yet and not finalized can get its damaged
        packs burned again from the staging folder, as a new session; otherwise restore the files from another copy, or
        rebuild the disc with 'restage' (which reuses the intact packs). Damage already in what was staged needs the files
        staged again.

        Options:
          --deep         also decrypt every pack and check every file against its hash
          --packs L      only these packs: 312, 312,683, 300-310. With --deep, files wholly in them are checked against their
                         hash; a file that reaches beyond them only has its part in them decrypted
          --files T      only the packs of the files matching T on this disc (as 'find' matches: part of the path, a hash
                         prefix, or *); with --deep each of them is checked whole. With --packs: the packs of both
          --no-cache     read every byte from the disc itself, none from what Windows still keeps in memory from an earlier
                         read since the disc was inserted (the disc's data, but not proof that it reads back now). It also
                         keeps the read from pushing other programs' data out of memory
          --retries N    how often a failed read is tried again (default 2; 0: never)
          --mark-burned  if every pack, file and the disc's other files check out, mark the disc (or its staged session) as
                         burned, as 'burned' does, with today's date. A disc only; not with --packs or --files
          --as-disc      the folder is the disc, although its drive does not report itself as an optical drive

        Progress: a status line shows the pack, the progress by bytes, speed, time left and the file being read (not written
        when the output is redirected). --deep prints every file as it is checked (OK or DAMAGED); a damaged pack is
        reported as soon as it is read.
        Ctrl+C stops after the pack being read and reports what was checked so far. Exit code 3: something is damaged.
        """;

    public int Run(Ctx c)
    {
        // verify [<label>] <path>: without a label, the disc names itself in its disc.json
        if (c.Args.Count is < 1 or > 2) throw new UserException("Usage: " + Usage);
        string mount = Path.GetFullPath(c.Args[^1]);
        if (!Directory.Exists(mount)) throw new UserException($"Not a folder: {mount}");
        string label = c.Args.Count == 2 ? c.Args[0]
            : DiscFiles.ReadInfo(mount)?.Label ?? throw new UserException(
                $"{mount} has no {DiscFiles.InfoName} that can be read, so it does not say which disc it is: give the label, " +
                $"discloak verify <label> {mount}");
        var disc = c.Arc.FindDisc(label);
        bool deep = c.Opt.Has("deep"), noCache = c.Opt.Has("no-cache"), markBurned = c.Opt.Has("mark-burned");
        int retries = c.Opt.GetInt("retries", PackReader.DefaultRetries);
        if (retries < 0) throw new UserException("--retries needs 0 or more.");
        if (markBurned) BurnedCommand.CheckCanMark(disc);
        var drive = DiscFiles.FindDrive(mount);
        bool onDisc = c.Opt.Has("as-disc") || drive?.DriveType == DriveType.CDRom;   // only a disc's state is recorded
        if (markBurned && !onDisc)
            throw new UserException($"--mark-burned needs the disc, in an optical drive; {mount} is a folder on {DriveText(drive)}. " +
                                    "(If it is the disc, add --as-disc.)");
        if (DiscFiles.ReadInfo(mount) is { } info)
        {
            if (!info.Label.Equals(disc.Label, StringComparison.OrdinalIgnoreCase))
                throw new UserException($"The disc in {mount} is {info.Label}, not {disc.Label}.");
            if (deep && info.Unreadable() is { } why) throw new UserException($"{disc.Label} cannot be decrypted: {why}.");
        }
        var copies = c.Arc.CopiesOn(disc.Label);
        int discPacks = Math.Max(disc.Packs.Count, copies.Count == 0 ? 0 : DiscFormat.PackOf(copies[^1].Copy.End - 1));
        if (discPacks == 0) { Console.WriteLine($"{disc.Label} has no packs in the catalog."); return 0; }
        if (!deep && disc.Packs.Count == 0)
            throw new UserException($"The catalog has no pack hashes for {disc.Label}; use --deep (decrypts and checks every file).");
        SortedSet<int>? only = c.Opt.Get("packs") is { } list ? ParsePacks(list, deep ? discPacks : disc.Packs.Count) : null;
        string? filesText = c.Opt.Get("files");
        int matched = 0;
        if (filesText != null)   // --files: the packs of the matching files' copies on this disc
        {
            var places = c.Cat.Files.Where(f => Archive.Matches(f, filesText))
                .SelectMany(f => f.Backups.Where(b => b.Disc == disc.Label)).Select(b => b.At).ToHashSet();
            var hits = copies.Where(x => places.Contains(x.Copy.At)).ToList();
            if (hits.Count == 0) throw new UserException($"No file matching '{filesText}' has a copy on {disc.Label} ('find {filesText}' shows where they are).");
            var filePacks = hits.SelectMany(x => PacksOf(x.Copy)).Where(p => p <= (deep ? discPacks : disc.Packs.Count)).ToList();
            if (filePacks.Count == 0) throw new UserException($"The files matching '{filesText}' have no data on {disc.Label} (empty files).");
            only ??= new SortedSet<int>();
            only.UnionWith(filePacks);
            matched = hits.Count;
        }
        if (only != null && markBurned) throw new UserException("--mark-burned needs the whole disc checked: leave out --packs and --files.");
        int packs = only?.Count ?? discPacks;   // the packs this run reads

        // the files to check: all, or (--packs) those with data in the packs given
        var toCheck = only == null ? copies
            : copies.Where(x => x.Copy.StoredSize > 0 &&
                                only.GetViewBetween(DiscFormat.PackOf(x.Copy.At), DiscFormat.PackOf(x.Copy.End - 1)).Count > 0).ToList();

        Console.WriteLine($"Verifying {disc.Label} in {mount}: {discPacks:N0} pack(s), {SizeText.Bytes((long)discPacks * DiscFormat.PackFileSize)} bytes, {copies.Count:N0} file(s)");
        Console.WriteLine(onDisc
            ? "  a disc" + (drive?.DriveType == DriveType.CDRom ? " in an optical drive" : " (--as-disc)") + ": what verify finds is recorded in the catalog"
            : $"  a folder on {DriveText(drive)}, not a disc in an optical drive: nothing is recorded in the catalog (--as-disc if it is the disc)");
        Console.WriteLine(deep
            ? "  --deep: every pack is hashed and decrypted, and every file is checked against its hash"
            : "  every pack's SHA-256 is compared with the catalog; nothing is decrypted (--deep also checks every file)");
        if (filesText != null) Console.WriteLine($"  --files {filesText}: {matched:N0} file(s) on this disc");
        if (only != null) Console.WriteLine($"  only pack(s) {Ranges(only)}, {toCheck.Count:N0} file(s) with data in them");
        if (noCache) Console.WriteLine("  --no-cache: every byte is read from the disc itself, none from what Windows keeps in memory");
        var (fileProblems, recorded) = CheckDiscFiles(c, disc, mount, deep, noCache, copies, record: onDisc);

        // progress: the packs are read in order, all of the same size
        var meter = new ProgressMeter((long)packs * DiscFormat.PackFileSize);
        int readPacks = 0, rereads = 0;
        string reading = "";   // the file whose data is being read

        var badPacks = new SortedDictionary<int, string>();   // pack -> what is wrong with it
        int unknown = 0;
        var okPacks = new HashSet<int>();   // read back as the catalog has them (or, --deep without a hash, decrypted whole)
        var asStaged = new HashSet<int>();  // read back exactly as the catalog has them (hash), whatever else is wrong
        void Judge(int pack, string? sha, string? problem)
        {
            readPacks++;
            meter.Done = (long)readPacks * DiscFormat.PackFileSize;
            string? expected = pack <= disc.Packs.Count ? disc.Packs[pack - 1] : null;
            string? bad = null;
            if (expected != null && sha != null && Hashing.SameHash(sha, expected)) asStaged.Add(pack);
            if (problem != null && sha == null) bad = problem;
            else if (expected == null) unknown++;
            else if (sha != null && !Hashing.SameHash(sha, expected)) bad = $"pack {pack} differs from the catalog";
            else if (problem != null) bad = problem;   // the right bytes, but they do not decrypt (another key?)
            if (bad != null)
            {
                badPacks[pack] = bad;
                meter.WriteLine($"  DAMAGED  {bad}");
            }
            else if (problem == null && (expected != null || deep)) okPacks.Add(pack);
            meter.Show($"pack {StatusLine.Count(readPacks, packs)}", reading);
        }
        void Reread(string note)
        {
            rereads++;
            meter.WriteLine($"  RE-READ  {note}");
        }

        int badFiles = 0, partly = 0;
        var damagedFiles = new List<string>();
        var damageAt = new Dictionary<long, Damage>();   // copies (by place) found damaged: why, and which packs
        var intactAt = new HashSet<long>();              // copies checked intact as a whole by --deep
        var deepDamage = new List<(long At, string Why, int[] Packs)>();   // --deep: judged once every pack is read
        if (!deep)
        {
            int first = 0;   // the first copy that does not end before the pack being read
            foreach (int p in only ?? Enumerable.Range(1, disc.Packs.Count))
            {
                if (Interrupt.Requested) break;
                reading = InPack(copies, p, ref first);
                string path = Path.Combine(mount, DiscFormat.PackPath(p));
                if (!File.Exists(path)) { Judge(p, null, $"pack {p} is missing"); continue; }
                try
                {
                    var (sha, problem) = PackReader.HashPack(path, p, disc.Packs[p - 1], noCache, retries, Reread);
                    Judge(p, sha, problem);
                }
                catch (OperationCanceledException) { break; }
            }
        }
        else
        {
            // every pack read once, in order: hashed and decrypted; the files are checked as their packs come by
            using var reader = new PackReader(mount, c.Keys, wholePacks: true, noCache) { PackRead = Judge, Retries = retries, Reread = Reread };
            void ReadUpTo(int pack)   // --packs: the packs given, up to `pack`, skipping the others
            {
                if (only != null && pack > reader.Loaded)
                    foreach (int p in only.GetViewBetween(reader.Loaded + 1, pack)) reader.ReadPack(p);
            }
            int done = 0;
            Console.WriteLine();
            try
            {
                foreach (var (file, copy) in toCheck)
                {
                    if (Interrupt.Requested) break;
                    string name = Describe(file, copy);
                    reading = name;
                    int firstPack = DiscFormat.PackOf(copy.At), lastPack = DiscFormat.PackOf(Math.Max(copy.At, copy.End - 1));
                    bool whole = only == null || copy.StoredSize == 0 || only.GetViewBetween(firstPack, lastPack).Count == lastPack - firstPack + 1;
                    string? damage = null;
                    try
                    {
                        if (whole)
                        {
                            ReadUpTo(firstPack);
                            using var stored = reader.Open(copy.At, copy.StoredSize);
                            string got = StoredForm.Read(stored, copy.Compression, copy.Codec, Stream.Null, Interrupt.Token);
                            if (!Hashing.SameHash(got, copy.ContentSha256)) damage = "decrypts, but the content differs from the catalog";
                        }
                        else   // only its part in the packs given: that it decrypts
                            foreach (int p in only!.GetViewBetween(firstPack, lastPack))
                            {
                                ReadUpTo(p);
                                long from = Math.Max(copy.At, (p - 1L) * DiscFormat.PackData), to = Math.Min(copy.End, p * DiscFormat.PackData);
                                using var part = reader.Open(from, to - from);
                                part.CopyTo(Stream.Null);
                            }
                    }
                    catch (InvalidDataException ex) { damage = ex.Message; }
                    done++;
                    if (!whole) partly++;
                    string what = $"{name} ({SizeText.Bytes(copy.Length)} bytes)" +
                                  (whole ? "" : $", its data in pack(s) {Ranges(only!.GetViewBetween(firstPack, lastPack))} (the file spans packs {firstPack}-{lastPack}: not checked against its hash)");
                    if (damage != null)
                    {
                        badFiles++;
                        damagedFiles.Add($"{what}  ({damage})");
                        deepDamage.Add((copy.At, damage, whole ? Enumerable.Range(firstPack, lastPack - firstPack + 1).ToArray()
                                                               : only!.GetViewBetween(firstPack, lastPack).ToArray()));
                    }
                    else if (whole) intactAt.Add(copy.At);
                    meter.WriteLine(damage == null ? $"  OK       {what}" : $"  DAMAGED  {what}: {damage}");
                }
                reading = "packs that hold no file (padding)";
                if (!Interrupt.Requested)   // they are checked too
                {
                    if (only == null) reader.ReadThrough(packs);
                    else ReadUpTo(only.Max);
                }
            }
            catch (OperationCanceledException) { }
        }
        meter.Clear();

        if (!deep)   // the files that have data in a damaged pack
            foreach (var (file, copy) in copies.Where(x => x.Copy.StoredSize > 0))
            {
                var inBad = PacksOf(copy).Where(badPacks.ContainsKey).ToList();
                if (inBad.Count == 0) continue;
                damagedFiles.Add(Describe(file, copy));
                damageAt[copy.At] = new Damage(badPacks[inBad[0]], inBad, AsStaged: false);
            }
        // --deep: a file whose packs all read back exactly as staged, and that still failed, was damaged when it was
        // staged - reading its packs again cannot clear that; otherwise the packs that did not read back as staged
        foreach (var (at, why, inPacks) in deepDamage)
        {
            if (inPacks.All(asStaged.Contains))
            {
                var undecryptable = inPacks.Where(badPacks.ContainsKey).ToList();   // none: the content differs
                damageAt[at] = new Damage(why, undecryptable.Count > 0 ? undecryptable : inPacks.ToList(), AsStaged: true);
            }
            else damageAt[at] = new Damage(why, inPacks.Where(p => !asStaged.Contains(p)).ToList(), AsStaged: false);
        }
        if (badPacks.Count > 0)
            Console.WriteLine($"\nDamaged pack(s): {string.Join(", ", badPacks.Keys.Take(50))}" + (badPacks.Count > 50 ? $" and {badPacks.Count - 50:N0} more" : ""));
        if (damagedFiles.Count > 0)
        {
            Console.WriteLine(deep ? "Files that do not come back intact:" : "Files with data in the damaged packs:");
            foreach (string f in damagedFiles.Take(50)) Console.WriteLine("  " + f);
            if (damagedFiles.Count > 50) Console.WriteLine($"  ... and {damagedFiles.Count - 50:N0} more");
        }

        bool complete = !Interrupt.Requested && readPacks >= packs;
        int ok = readPacks - badPacks.Count - unknown;
        Console.WriteLine($"\n{disc.Label}: {ok:N0} of {packs:N0} pack(s){(only != null ? " given" : "")} OK, {badPacks.Count:N0} damaged" +
                          (unknown > 0 ? $", {unknown:N0} without a hash in the catalog" : "") +
                          (deep ? $"; {toCheck.Count - badFiles:N0} of {toCheck.Count:N0} file(s) checked intact" +
                                  (partly > 0 ? $" ({partly:N0} of them only their part in these packs)" : "") : "") +
                          (rereads > 0 ? $"; {rereads:N0} read(s) right only when tried again" : "") +
                          (fileProblems == 0 ? "; the disc's other files OK" : $"; {fileProblems:N0} of the disc's other files with a problem (see above)") +
                          $" (read in {StatusLine.Duration(meter.Elapsed)}).");
        if (rereads > 0)
            Console.WriteLine("RE-READ: those reads failed or came back wrong at first, then read right. The data is intact, but the " +
                              "disc may be wearing, or the drive or its connection is unreliable: verify it again later, or in another drive.");
        if (!onDisc)
        {
            if (badPacks.Count + badFiles > 0 && (disc.State == "staged" || disc.PendingAppend != null))
                Console.WriteLine($"\nThis folder is damaged: do not burn it as it is. 'discard {disc.Label}' and stage again.");
        }
        else if (badPacks.Count > 0 && !Interrupt.Requested)
            Advise(disc, mount, badPacks.Keys.Where(p => !asStaged.Contains(p)), damageAt.Values.Any(d => d.AsStaged));
        else if (damageAt.Values.Any(d => d.AsStaged) && !Interrupt.Requested) Advise(disc, mount, [], asStagedFound: true);

        // copies taken from the disc's manifest (scandisc) are verified once their packs check out
        bool changed = recorded | (onDisc && RecordDamage(c, disc, damageAt, intactAt, okPacks));
        if (!onDisc) Console.WriteLine("Nothing was recorded in the catalog: this is a folder, not the disc.");
        bool clean = only == null && complete && badPacks.Count == 0 && (deep ? badFiles == 0 : unknown == 0);
        if (clean && onDisc)
            foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label && b.Unverified))
            {
                b.Unverified = false;
                changed = true;
            }
        if (markBurned)
        {
            if (!clean || fileProblems > 0)
                Console.WriteLine($"Not marked as burned (--mark-burned): {(Interrupt.Requested ? "the check was stopped" : "not everything checked out")}.");
            else
            {
                var (marked, message) = BurnedCommand.Mark(c, disc, DateTime.UtcNow);
                changed |= marked;
                Console.WriteLine(message);
            }
        }
        if (changed) c.Cat.Save();   // a plain re-check changes nothing and should not use up a history slot
        if (Interrupt.Requested)
        {
            Console.WriteLine($"Stopped by Ctrl+C: {packs - readPacks:N0} of {packs:N0} pack(s) were not checked.");
            return Interrupt.ExitCode;
        }
        return badPacks.Count + badFiles + fileProblems == 0 ? 0 : 3;
    }

    /// <summary>What a copy found damaged is recorded with: why, the packs to read again, damage already in what was staged.</summary>
    record Damage(string Why, List<int> Packs, bool AsStaged);

    /// <summary>"a fixed drive (E:\)", "a removable drive (F:\)", "a network share" ...</summary>
    static string DriveText(DriveInfo? drive) => drive == null ? "a drive Windows does not name (a network share?)" : drive.DriveType switch
    {
        DriveType.Fixed => $"a fixed drive ({drive.Name})",
        DriveType.Removable => $"a removable drive ({drive.Name})",
        DriveType.Network => $"a network drive ({drive.Name})",
        DriveType.Ram => $"a RAM drive ({drive.Name})",
        _ => $"a drive of another kind ({drive.Name})"
    };

    public static IEnumerable<int> PacksOf(BackupRef copy) =>
        copy.StoredSize <= 0 ? [] : Enumerable.Range(DiscFormat.PackOf(copy.At), DiscFormat.PackOf(copy.End - 1) - DiscFormat.PackOf(copy.At) + 1);

    /// <summary>
    /// What can be done about damaged packs: burn them again (a disc not marked as burned yet), or rebuild. Damage that
    /// was already in what was staged (the packs read back exactly as staged) comes back with the same packs: then
    /// only staging the files again, from their sources, helps.
    /// </summary>
    static void Advise(DiscRecord disc, string mount, IEnumerable<int> bad, bool asStagedFound)
    {
        var packs = bad.ToList();
        bool notBurned = disc.State == "staged" || disc.PendingAppend != null;
        if (asStagedFound)
            Console.WriteLine(notBurned
                ? $"""

                  Some damage was already in what was staged: those packs read back exactly as they were written, and their
                  files still do not come back intact. Burning them again from the staging folder would bring it back.
                  'discard {disc.Label}' and stage again (the files are read from their sources anew).
                  """
                : $"""

                  Some damage was already in what was staged: those packs read back exactly as they were written, and their
                  files still do not come back intact. Reading them again will not change that, and 'restage --from' (which
                  reuses packs whose hash matches) would bring it back. The files still on their source are pending again
                  already: the next 'stage' backs them up. To rebuild this disc: 'restage {disc.Label} <dir>' without --from
                  builds every pack again from the source files.
                  """);
        if (packs.Count == 0) return;
        if (notBurned)
        {
            Console.WriteLine($"""

                {disc.Label} is not marked as burned yet. If the disc is not finalized and has room left, burn only the
                damaged pack(s) again from its staging folder, as a new session that continues the file system:
                """);
            foreach (int p in packs.Take(20)) Console.WriteLine("  " + DiscFormat.PackPath(p).Replace('/', Path.DirectorySeparatorChar));
            if (packs.Count > 20) Console.WriteLine($"  ... and {packs.Count - 20:N0} more");
            Console.WriteLine($"""
                (check the folder's copies first: discloak verify {disc.Label} <stagingFolder>\{disc.Label} --packs {Ranges(packs)})
                then check the disc again: discloak verify {disc.Label} {mount} --packs {Ranges(packs)} --deep
                Or burn the whole folder onto a new disc.
                """);
        }
        else
            Console.WriteLine($"""

                Restore these files from another copy if they have one; 'restage {disc.Label} <dir> --from {mount}'
                rebuilds the disc onto a new blank, reusing its intact packs.
                """);
    }

    /// <summary>
    /// Records on the catalog's copies what this verify found: a damaged copy is marked (it stops counting as a
    /// backup, so the file is pending again while on its source); a copy marked damaged that read back intact as a
    /// whole loses the mark, with a warning - a copy that fails and then passes points to unreliable reads or a
    /// wearing disc. Aliases (other files with the same stored copy) share its place, and its mark. Returns whether
    /// anything changed.
    /// </summary>
    static bool RecordDamage(Ctx c, DiscRecord disc, Dictionary<long, Damage> damageAt, HashSet<long> intactAt, HashSet<int> okPacks)
    {
        var now = DateTime.UtcNow;
        var marked = new HashSet<long>();
        var cleared = new List<string>();
        var keptAsStaged = new HashSet<long>();
        bool changed = false;
        foreach (var f in c.Cat.Files)
            foreach (var b in f.Backups.Where(b => b.Disc == disc.Label))
            {
                if (damageAt.TryGetValue(b.At, out var found))
                {
                    if (!b.IsDamaged) { b.DamagedUtc = now; marked.Add(b.At); }
                    // damage already in what was staged stays known as such; packs found right this time drop out
                    bool asStaged = b.DamagedAsStaged || found.AsStaged;
                    var packs = asStaged && !b.DamagedAsStaged ? found.Packs
                        : (b.DamagedPacks ?? []).Where(p => !okPacks.Contains(p)).Union(found.Packs).Order().ToList();
                    changed |= b.Damage != found.Why || b.DamagedAsStaged != asStaged || !(b.DamagedPacks ?? []).SequenceEqual(packs) || marked.Contains(b.At);
                    (b.Damage, b.DamagedAsStaged, b.DamagedPacks) = (found.Why, asStaged, packs);
                }
                else if (b.IsDamaged && Intact(b))
                {
                    cleared.Add($"{Describe(f, b)}  (recorded as damaged on {b.DamagedUtc:yyyy-MM-dd}: {b.Damage})");
                    (b.DamagedUtc, b.Damage, b.DamagedPacks, b.DamagedAsStaged) = (null, null, null, false);
                    changed = true;
                }
                else if (b.IsDamaged && b.DamagedAsStaged && PacksOf(b).All(okPacks.Contains)) keptAsStaged.Add(b.At);
            }

        // a mark goes when --deep checked the whole copy intact, or (damage from reading, not from staging) when the
        // packs that read back wrong read back as the catalog has them; a mark from before 0.12.0 names no packs: all
        // of the copy's packs then
        bool Intact(BackupRef b) =>
            intactAt.Contains(b.At) ||
            !b.DamagedAsStaged && (b.DamagedPacks is { Count: > 0 } packs ? packs : PacksOf(b)).All(okPacks.Contains) && PacksOf(b).Any();
        if (damageAt.Count > 0)
            Console.WriteLine(marked.Count > 0
                ? $"\nRecorded in the catalog: {marked.Count:N0} file copy(ies) on {disc.Label} are damaged" +
                  (marked.Count < damageAt.Count ? $" ({damageAt.Count - marked.Count:N0} more already were)" : "") +
                  ". They no longer count as backups: files still on their source are pending again ('plan', 'stage'), " +
                  "and restore tries these copies last. A verify that reads one back intact removes its mark."
                : $"\n{damageAt.Count:N0} file copy(ies) on {disc.Label} stay recorded as damaged (they do not count as backups).");
        if (cleared.Count > 0)
        {
            Console.WriteLine($"\nWARNING  {cleared.Count:N0} file copy(ies) on {disc.Label} recorded as damaged read back intact now. " +
                              "The mark is removed: they count as backups again.");
            foreach (string line in cleared.Take(50)) Console.WriteLine("  " + line);
            if (cleared.Count > 50) Console.WriteLine($"  ... and {cleared.Count - 50:N0} more");
            Console.WriteLine("A copy that failed before and passes now points to unreliable reads - the drive, its cable or its connection - " +
                              "or to a disc that is wearing (unless its packs were burned again since). Verify it again in a while, or in another drive.");
        }
        if (keptAsStaged.Count > 0)
            Console.WriteLine($"\n{keptAsStaged.Count:N0} file copy(ies) on {disc.Label} stay recorded as damaged although their packs read back " +
                              "as staged: the damage was already in what was staged, so only a --deep check of the whole file can clear it.");
        return changed;
    }

    /// <summary>--packs: "312", "312,683", "300-310" (1 to `packs`).</summary>
    public static SortedSet<int> ParsePacks(string text, int packs)
    {
        var set = new SortedSet<int>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(ends[0], out int from) || !int.TryParse(ends[^1], out int to) || from < 1 || to < from)
                throw new UserException($"--packs: '{part}' is not a pack number or a range (312, 300-310).");
            if (to > packs) throw new UserException($"--packs: the disc has {packs:N0} pack(s), there is no pack {to:N0}.");
            for (int p = from; p <= to; p++) set.Add(p);
        }
        if (set.Count == 0) throw new UserException("--packs: no pack given (312, 312,683 or 300-310).");
        return set;
    }

    /// <summary>"300-310,312" (sorted, runs joined).</summary>
    public static string Ranges(IEnumerable<int> packs)
    {
        var runs = new List<string>();
        int start = -1, end = -1;
        foreach (int p in packs.Order())
        {
            if (p == end + 1) { end = p; continue; }
            if (start > 0) runs.Add(start == end ? $"{start}" : $"{start}-{end}");
            start = end = p;
        }
        if (start > 0) runs.Add(start == end ? $"{start}" : $"{start}-{end}");
        return string.Join(",", runs);
    }

    /// <summary>
    /// The disc's files besides its packs, a line each: disc.json, packs.sha256, the manifests, discloak.key, README.txt
    /// and the catalog backup in _catalog. Returns how many have a problem, and whether the manifests' hashes were
    /// recorded (--deep, for a disc staged before the catalog kept them). They are small: this takes seconds.
    /// </summary>
    static (int Problems, bool Recorded) CheckDiscFiles(Ctx c, DiscRecord disc, string mount, bool deep, bool noCache,
                                                        List<(FileRecord File, BackupRef Copy)> copies, bool record)
    {
        byte[]? Read(string path) => File.Exists(path) ? DiscRead.AllBytes(path, noCache) : null;   // null: missing
        int problems = 0;
        void Report(string name, string? problem, string ok = "")
        {
            if (problem != null) problems++;
            Console.WriteLine(problem == null ? $"  OK       {name}{ok}" : $"  PROBLEM  {name}: {problem}");
        }
        bool OurCatalog(string? id) => id == null || id.Equals(c.Keys.CatalogId, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine("The disc's files:");

        // disc.json: which disc this is, and its key
        try
        {
            var info = Read(Path.Combine(mount, DiscFiles.InfoName)) is { } json ? JsonStore.ParseObject<DiscInfo>(json, DiscFiles.InfoName) : null;
            int burned = disc.PendingAppend is { } pending ? pending.Number - 1 : disc.Sessions;   // a staged session is not on the disc yet
            Report(DiscFiles.InfoName,
                info == null ? "is missing"
                : info.Unreadable()
                  ?? (!OurCatalog(info.CatalogId) ? "belongs to another catalog"
                  : disc.KeyId != null && info.KeyId != null && !info.KeyId.Equals(disc.KeyId, StringComparison.OrdinalIgnoreCase) ? $"names key {info.KeyId}, the catalog {disc.KeyId}"
                  : info.Sessions != disc.Sessions && info.Sessions != burned ? $"says {info.Sessions} session(s), the catalog {disc.Sessions}"
                  : null));
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Report(DiscFiles.InfoName, $"cannot be read ({ex.Message})"); }

        // packs.sha256: the pack hashes the catalog has
        try
        {
            if (Read(Path.Combine(mount, DiscFiles.PackListName)) is not { } text) Report(DiscFiles.PackListName, "is missing");
            else
            {
                var list = DiscFiles.ParsePackList(Encoding.UTF8.GetString(text));
                int differ = disc.Packs.Where((sha, i) => !(list.TryGetValue(i + 1, out string? got) && Hashing.SameHash(got, sha))).Count();
                int extra = list.Keys.Count(p => p > disc.Packs.Count);
                Report(DiscFiles.PackListName,
                    disc.Packs.Count == 0 || differ + extra == 0 ? null
                    : (differ > 0 ? $"{differ:N0} of the {disc.Packs.Count:N0} pack hashes are missing or differ from the catalog" : "") +
                      (differ > 0 && extra > 0 ? ", " : "") + (extra > 0 ? $"{extra:N0} pack(s) more than the catalog has" : ""),
                    disc.Packs.Count == 0 ? $" ({list.Count:N0} packs; the catalog has no pack hashes to compare)" : $" ({list.Count:N0} packs, as in the catalog)");
            }
        }
        catch (IOException ex) { Report(DiscFiles.PackListName, $"cannot be read ({ex.Message})"); }

        // the manifests: their hashes as the catalog has them; --deep: their files as the catalog has them
        var files = DiscFiles.Manifests(mount);
        var sessions = new List<List<ManifestLine>>();
        var hashes = new Dictionary<int, string>();
        bool allIntact = true;
        for (int s = 1; s <= Math.Max(disc.Sessions, files.Count == 0 ? 0 : files[^1].Session); s++)
        {
            string name = DiscFiles.ManifestFor(s);
            string? path = files.FirstOrDefault(f => f.Session == s).Path;
            string? expected = s <= disc.Manifests.Count && disc.Manifests[s - 1].Length > 0 ? disc.Manifests[s - 1] : null;
            if (s > disc.Sessions)
            {
                Report(name, "a session the catalog does not know ('scandisc --update' records it)");
                continue;
            }
            if (path == null)
            {
                allIntact = false;
                Report(name, disc.PendingAppend?.Number == s ? "is missing (session " + s + " is staged, not burned yet)" : "is missing");
                continue;
            }
            try
            {
                byte[] bytes = DiscRead.AllBytes(path, noCache);
                hashes[s] = Convert.ToHexString(SHA256.HashData(bytes));
                if (expected != null && !Hashing.SameHash(hashes[s], expected))
                {
                    allIntact = false;
                    Report(name, "differs from the catalog (its SHA-256)");
                }
                else if (!deep)
                    Report(name, null, expected != null ? " (SHA-256 as in the catalog)" : " (readable; the catalog has no hash of it: --deep checks it)");
                else
                {
                    var lines = DiscFiles.OpenManifest(bytes, c.Keys, name);
                    sessions.Add(lines);
                    Report(name, null, $" (decrypts: {lines.Count:N0} file(s))");
                }
            }
            catch (IOException ex) { allIntact = false; Report(name, $"cannot be read ({ex.Message})"); }
            catch (InvalidDataException ex) { allIntact = false; Report(name, $"does not decrypt ({ex.Message})"); }
        }
        bool recorded = false;
        if (deep && allIntact && sessions.Count > 0)
        {
            var onDisc = DiscFiles.Merge(sessions).ToDictionary(l => l.At);
            var inCatalog = copies.ToDictionary(x => x.Copy.At, x => x.Copy);
            int onlyDisc = onDisc.Keys.Count(at => !inCatalog.ContainsKey(at)), onlyCatalog = inCatalog.Keys.Count(at => !onDisc.ContainsKey(at));
            int differ = onDisc.Count(kv => inCatalog.TryGetValue(kv.Key, out var copy) && !Same(kv.Value, copy));
            var what = new List<string>();
            if (onlyDisc > 0) what.Add($"{onlyDisc:N0} file(s) the catalog does not have on this disc");
            if (onlyCatalog > 0) what.Add($"{onlyCatalog:N0} file(s) of the catalog missing");
            if (differ > 0) what.Add($"{differ:N0} file(s) with other sizes or hashes");
            Report("the manifests", what.Count == 0 ? null : "list " + string.Join(", ", what),
                   $" list the {onDisc.Count:N0} file(s) the catalog has on this disc");
            // a disc staged before the catalog kept the manifests' hashes: from now on a plain verify checks them
            if (record && what.Count == 0 && Enumerable.Range(1, disc.Sessions).All(hashes.ContainsKey) &&
                !Enumerable.Range(1, disc.Sessions).All(s => s <= disc.Manifests.Count && disc.Manifests[s - 1].Length > 0))
            {
                disc.Manifests = Enumerable.Range(1, disc.Sessions).Select(s => hashes[s]).ToList();
                recorded = true;
                Console.WriteLine("           (their SHA-256 is now in the catalog: a plain verify checks them too)");
            }
        }

        // discloak.key: this catalog's master key, wrapped by the password
        try
        {
            string keyPath = KeyFile.PathIn(mount);
            var keyFile = Read(keyPath) is { } bytes ? KeyFile.Parse(bytes, keyPath) : null;
            Report(Path.GetFileName(keyPath),   // bdxl.key on a disc staged before 0.8.0
                keyFile == null ? "is missing"
                : !OurCatalog(keyFile.CatalogId) ? "belongs to another catalog"
                : keyFile.Password == null ? "has no password envelope" : null,
                " (this catalog's, with a password envelope" + (keyFile?.Recovery != null ? " and a recovery key envelope)" : ")"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { Report(Path.GetFileName(KeyFile.PathIn(mount)), ex.Message); }

        string readme = Path.Combine(mount, DiscFiles.ReadmeName);
        try { Report(DiscFiles.ReadmeName, Read(readme) is not { } bytes ? "is missing" : bytes.Length == 0 ? "is empty" : null); }
        catch (IOException ex) { Report(DiscFiles.ReadmeName, $"cannot be read ({ex.Message})"); }

        // _program: the program that wrote the disc - its exe, source and licenses readable, the source zip whole
        string program = Path.Combine(mount, DiscProgram.FolderName);
        if (!Directory.Exists(program))
            Console.WriteLine($"  -        {DiscProgram.FolderName}: none on this disc (staged before 0.9.0, or with --without-program)");
        else
        {
            string? problem = null;
            string? exeVersion = null;
            var names = Directory.GetFiles(program).Select(p => Path.GetFileName(p)!).Order(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string name in names)
            {
                try
                {
                    byte[] bytes = DiscRead.AllBytes(Path.Combine(program, name), noCache);
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
                        foreach (var entry in zip.Entries) { using var s = entry.Open(); s.CopyTo(Stream.Null); }   // every entry unpacks
                    }
                    if (name.Equals(DiscProgram.ExeName, StringComparison.OrdinalIgnoreCase))
                        exeVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(program, name)).ProductVersion;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { problem ??= $"{name} cannot be read ({ex.Message})"; }
            }
            if (!names.Contains(DiscProgram.ExeName, StringComparer.OrdinalIgnoreCase)) problem ??= $"{DiscProgram.ExeName} is missing";
            Report(DiscProgram.FolderName, problem, $" ({DiscProgram.ExeName} {exeVersion}, {names.Count - 1:N0} more file(s): source, licenses)");
        }

        // _catalog: every file against its repair data (no password needed)
        string backup = Path.Combine(mount, DiscFiles.CatalogBackupName);
        if (!Directory.Exists(backup))
            Console.WriteLine($"  -        {DiscFiles.CatalogBackupName}: none on this disc ('stage --with-catalog' puts one there)");
        else
        {
            try
            {
                int intact = 0, repairable = 0, lost = 0;
                foreach (string name in CatalogCommand.RepairNames(backup))
                {
                    try
                    {
                        var status = CatalogCommand.Load(backup, name, noCache).Status;
                        if (status == RsStatus.Ok) intact++;
                        else if (status == RsStatus.Repaired) repairable++;
                        else lost++;
                    }
                    catch (UserException) { lost++; }
                }
                Report(DiscFiles.CatalogBackupName,
                    lost > 0 ? $"{lost:N0} file(s) damaged beyond what the repair data can rebuild" + (repairable > 0 ? $", {repairable:N0} damaged but repairable" : "")
                    : repairable > 0 ? $"{repairable:N0} file(s) damaged, but the repair data can rebuild them ('catalog backup restore' does as it reads)"
                    : null,
                    $" ({intact:N0} file(s) intact, checked against their repair data)");
            }
            catch (UserException ex) { Report(DiscFiles.CatalogBackupName, ex.Message); }
        }
        return (problems, recorded);
    }

    /// <summary>A manifest line that describes the catalog's copy.</summary>
    static bool Same(ManifestLine line, BackupRef copy) =>
        line.Size == copy.StoredSize && line.PlainSize == copy.Length && Hashing.SameHash(line.Sha256, copy.ContentSha256) &&
        (line.Compression ?? 0) == copy.Compression && Compression.CodecOf(line.Compression ?? 0, line.Codec) == Compression.CodecOf(copy.Compression, copy.Codec) &&
        (line.Part ?? 0) == copy.Part && (line.Offset ?? 0) == copy.Offset;

    /// <summary>
    /// The files with data in pack `pack`, for the status line: "S/a.bin", "S/a.bin +12 more" or the padding.
    /// `first` moves forward with the packs (the copies are in disc order).
    /// </summary>
    static string InPack(List<(FileRecord File, BackupRef Copy)> copies, int pack, ref int first)
    {
        long start = (pack - 1L) * DiscFormat.PackData, end = start + DiscFormat.PackData;
        while (first < copies.Count && copies[first].Copy.End <= start) first++;
        var inPack = copies.Skip(first).TakeWhile(x => x.Copy.At < end).Where(x => x.Copy.StoredSize > 0).ToList();
        if (inPack.Count == 0) return "no file data (padding)";
        return Describe(inPack[0].File, inPack[0].Copy) + (inPack.Count > 1 ? $" +{inPack.Count - 1:N0} more" : "");
    }

    static string Describe(FileRecord f, BackupRef copy) =>
        $"{f.RootName}/{f.RelPath}" + (copy.IsPart ? $" (part {copy.Part}/{copy.Parts})" : "") +
        (Hashing.SameHash(copy.Sha256, f.Sha256) ? "" : " (an older version)");
}
