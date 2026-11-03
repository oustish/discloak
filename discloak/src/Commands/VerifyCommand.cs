namespace Discloak;

public sealed class VerifyCommand : ICommand
{
    public string Name => "verify";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Re-read a disc and compare it with the catalog";
    public string Usage => "verify <label> <mountedDiscPath> [--deep] [--no-cache]";
    public bool Interruptible => true;
    public string Details => """
        Reads every pack of the disc and compares its SHA-256 with the catalog - nothing is decrypted, so no password
        is needed for that part. --deep also decrypts every pack (each 1 MiB chunk is authenticated) and checks every
        file on the disc against its hash, in the same single read of the disc.
        First the disc's other files are checked: disc.json (the disc and its key), packs.sha256 (the same pack hashes
        as the catalog), every manifest (its SHA-256 as the catalog has it; --deep also decrypts it and compares its
        files with the catalog), discloak.key (this catalog's), README.txt and the catalog backup in _catalog (every file
        against its repair data). A disc staged before the catalog kept its manifests' hashes gets them recorded by
        its first --deep verify that finds them intact.
        A status line shows the pack being read, the progress by bytes, speed and time left, and the file whose data
        is being read (rewritten in place, not written when the output is redirected). --deep also prints every file
        as soon as it is checked. A damaged pack is reported as soon as it is read.
        A damaged pack is named with the files that have data in it; those files can be restored from another copy,
        or the disc rebuilt with 'restage' (which reuses the intact packs).
        Copies recorded by 'scandisc' are marked 'unverified' until this has checked their packs.
          --no-cache   read every byte from the disc itself. Without it, Windows may hand over what it still keeps in
                       memory from an earlier read of the disc since it was inserted (ejecting the disc empties
                       that): still the disc's data, but not proof that it reads back now - which matters when a
                       disc stays in the drive for days. With it, the 100 GB read also do not push out what
                       Windows keeps in memory for other programs.
        Ctrl+C stops after the pack being read and reports what was checked so far.
        """;

    public int Run(Ctx c)
    {
        var disc = c.Arc.FindDisc(c.Arg(0, Usage));
        string mount = Path.GetFullPath(c.Arg(1, Usage));
        bool deep = c.Opt.Has("deep"), noCache = c.Opt.Has("no-cache");
        if (!Directory.Exists(mount)) throw new UserException($"Not a folder: {mount}");
        if (DiscFiles.ReadInfo(mount) is { } info)
        {
            if (!info.Label.Equals(disc.Label, StringComparison.OrdinalIgnoreCase))
                throw new UserException($"The disc in {mount} is {info.Label}, not {disc.Label}.");
            if (deep && info.Unreadable() is { } why) throw new UserException($"{disc.Label} cannot be decrypted: {why}.");
        }
        var copies = c.Arc.CopiesOn(disc.Label);
        int packs = Math.Max(disc.Packs.Count, copies.Count == 0 ? 0 : DiscFormat.PackOf(copies[^1].Copy.End - 1));
        if (packs == 0) { Console.WriteLine($"{disc.Label} has no packs in the catalog."); return 0; }
        if (!deep && disc.Packs.Count == 0)
            throw new UserException($"The catalog has no pack hashes for {disc.Label}; use --deep (decrypts and checks every file).");

        long discBytes = (long)packs * DiscFormat.PackFileSize;
        Console.WriteLine($"Verifying {disc.Label} in {mount}: {packs:N0} pack(s), {SizeText.Bytes(discBytes)} bytes, {copies.Count:N0} file(s)");
        Console.WriteLine(deep
            ? "  --deep: every pack is hashed and decrypted, and every file is checked against its hash"
            : "  every pack's SHA-256 is compared with the catalog; nothing is decrypted (--deep also checks every file)");
        if (noCache) Console.WriteLine("  --no-cache: every byte is read from the disc itself, none from what Windows keeps in memory");
        var (fileProblems, recorded) = CheckDiscFiles(c, disc, mount, deep, noCache, copies);

        // progress: the packs are read in order, all of the same size
        var meter = new ProgressMeter(discBytes);
        int readPacks = 0;
        string reading = "";   // the file whose data is being read

        var badPacks = new SortedDictionary<int, string>();   // pack -> what is wrong with it
        int checkedPacks = 0, unknown = 0;
        void Judge(int pack, string? sha, string? problem)
        {
            readPacks++;
            meter.Done = (long)readPacks * DiscFormat.PackFileSize;
            checkedPacks = Math.Max(checkedPacks, pack);
            string? expected = pack <= disc.Packs.Count ? disc.Packs[pack - 1] : null;
            string? bad = null;
            if (problem != null && sha == null) bad = problem;
            else if (expected == null) unknown++;
            else if (sha != null && !Hashing.SameHash(sha, expected)) bad = $"pack {pack} differs from the catalog";
            else if (problem != null) bad = problem;   // the right bytes, but they do not decrypt (another key?)
            if (bad != null)
            {
                badPacks[pack] = bad;
                meter.WriteLine($"DAMAGED  {bad}");
            }
            meter.Show($"pack {StatusLine.Count(readPacks, packs)}", reading);
        }

        int badFiles = 0;
        var damagedFiles = new List<string>();
        if (!deep)
        {
            int first = 0;   // the first copy that does not end before the pack being read
            for (int p = 1; p <= disc.Packs.Count && !Interrupt.Requested; p++)
            {
                reading = InPack(copies, p, ref first);
                string path = Path.Combine(mount, DiscFormat.PackPath(p));
                if (!File.Exists(path)) { Judge(p, null, $"pack {p} is missing"); continue; }
                try
                {
                    string sha;
                    using (var input = DiscRead.Open(path, noCache)) sha = Hashing.Hash(input, Interrupt.Token);
                    Judge(p, sha, null);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException ex) { Judge(p, null, $"pack {p} cannot be read ({ex.Message})"); }
            }
        }
        else
        {
            // every pack read once, in order: hashed and decrypted; the files are checked as their packs come by
            using var reader = new PackReader(mount, c.Keys, wholePacks: true, noCache) { PackRead = Judge };
            int done = 0;
            Console.WriteLine();
            try
            {
                foreach (var (file, copy) in copies)
                {
                    if (Interrupt.Requested) break;
                    string name = Describe(file, copy);
                    reading = name;
                    string? damage = null;
                    try
                    {
                        using var stored = reader.Open(copy.At, copy.StoredSize);
                        string got = StoredForm.Read(stored, copy.Compression, copy.Codec, Stream.Null, Interrupt.Token);
                        if (!Hashing.SameHash(got, copy.ContentSha256)) damage = "decrypts, but the content differs from the catalog";
                    }
                    catch (InvalidDataException ex) { damage = ex.Message; }
                    done++;
                    if (damage != null) { badFiles++; damagedFiles.Add($"{name}  ({damage})"); }
                    meter.WriteLine($"  [{StatusLine.Count(done, copies.Count)}  {meter.Rate()}] {name} ({SizeText.Bytes(copy.Length)} bytes) " +
                                    (damage == null ? "OK" : $"DAMAGED: {damage}"));
                }
                reading = "packs that hold no file (padding)";
                if (!Interrupt.Requested) reader.ReadThrough(packs);   // they are checked too
            }
            catch (OperationCanceledException) { }
        }
        meter.Clear();

        if (!deep)   // name the files that have data in a damaged pack
            foreach (var (file, copy) in copies.Where(x => badPacks.Keys.Any(p => Overlaps(x.Copy, p))))
                damagedFiles.Add(Describe(file, copy));
        if (badPacks.Count > 0)
            Console.WriteLine($"\nDamaged pack(s): {string.Join(", ", badPacks.Keys.Take(50))}" + (badPacks.Count > 50 ? $" and {badPacks.Count - 50:N0} more" : ""));
        if (damagedFiles.Count > 0)
        {
            Console.WriteLine(deep ? "Files that do not come back intact:" : "Files with data in the damaged packs:");
            foreach (string f in damagedFiles.Take(50)) Console.WriteLine("  " + f);
            if (damagedFiles.Count > 50) Console.WriteLine($"  ... and {damagedFiles.Count - 50:N0} more");
        }

        bool complete = !Interrupt.Requested && checkedPacks >= packs;
        int ok = checkedPacks - badPacks.Count - unknown;
        Console.WriteLine($"\n{disc.Label}: {ok:N0} of {packs:N0} pack(s) OK, {badPacks.Count:N0} damaged" +
                          (unknown > 0 ? $", {unknown:N0} without a hash in the catalog" : "") +
                          (deep ? $"; {copies.Count - badFiles:N0} of {copies.Count:N0} file(s) checked intact" : "") +
                          (fileProblems == 0 ? "; the disc's other files OK" : $"; {fileProblems:N0} of the disc's other files with a problem (see above)") +
                          $" (read in {StatusLine.Duration(meter.Elapsed)}).");

        // copies taken from the disc's manifest (scandisc) are verified once their packs check out
        bool changed = recorded;
        if (complete && badPacks.Count == 0 && (deep ? badFiles == 0 : unknown == 0))
            foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label && b.Unverified))
            {
                b.Unverified = false;
                changed = true;
            }
        if (changed) c.Cat.Save();   // a plain re-check changes nothing and should not use up a history slot
        if (Interrupt.Requested)
        {
            Console.WriteLine($"Stopped by Ctrl+C: {packs - checkedPacks:N0} of {packs:N0} pack(s) were not checked.");
            return Interrupt.ExitCode;
        }
        return badPacks.Count + badFiles + fileProblems == 0 ? 0 : 3;
    }

    /// <summary>
    /// The disc's files besides its packs, a line each: disc.json, packs.sha256, the manifests, discloak.key, README.txt
    /// and the catalog backup in _catalog. Returns how many have a problem, and whether the manifests' hashes were
    /// recorded (--deep, for a disc staged before the catalog kept them). They are small: this takes seconds.
    /// </summary>
    static (int Problems, bool Recorded) CheckDiscFiles(Ctx c, DiscRecord disc, string mount, bool deep, bool noCache,
                                                        List<(FileRecord File, BackupRef Copy)> copies)
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
            if (what.Count == 0 && Enumerable.Range(1, disc.Sessions).All(hashes.ContainsKey) &&
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

    static bool Overlaps(BackupRef copy, int pack) =>
        copy.StoredSize > 0 && DiscFormat.PackOf(copy.At) <= pack && DiscFormat.PackOf(copy.End - 1) >= pack;

    static string Describe(FileRecord f, BackupRef copy) =>
        $"{f.RootName}/{f.RelPath}" + (copy.IsPart ? $" (part {copy.Part}/{copy.Parts})" : "") +
        (Hashing.SameHash(copy.Sha256, f.Sha256) ? "" : " (an older version)");
}
