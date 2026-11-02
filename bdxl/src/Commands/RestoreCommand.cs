namespace Bdxl;

public sealed class RestoreCommand : ICommand
{
    public string Name => "restore";
    public string Group => CommandGroup.Recovery;
    public string Summary => "Copy files back from mounted discs into a folder";
    public string Usage => "restore <destDir> <text|hashPrefix|*> --mount F:\\;G:\\ [--disc label] [--source alias] [--flat] [--overwrite]";
    public bool ReadOnlyFor(Options opt) => true;
    public bool Interruptible => true;
    public string Details => """
        Finds the files in the catalog, tells you which discs it needs, and copies them from the discs you mounted.
        Discs are recognised by disc.json (or --disc for a single mount). Only the parts of the packs a file needs are
        read and decrypted; every file is checked against its hash. Layout: <destDir>/<alias>/<path>, or --flat.
        A damaged spot on a disc costs only the files with data there: if another mounted disc holds a copy of such a
        file, it is taken from there; the others are listed as FAILED, and the rest is restored.
        Ctrl+C stops after the file being copied (that one is removed; parts of split files already verified are
        kept) and lists what is left; running the same command again continues (restored files show as EXISTS).
        """;

    /// <summary>Copies files back from mounted discs. Layout under destDir: source alias/relative path (or --flat).</summary>
    public int Run(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: " + Usage);
        string dest = Path.GetFullPath(c.Args[0]);
        string query = c.Args[1];
        string destPrefix = dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool flat = c.Opt.Has("flat"), overwrite = c.Opt.Has("overwrite");

        // which disc is mounted where (identified by disc.json written at stage time)
        var mounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mp in (c.Opt.Get("mount") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var discInfo = DiscFiles.ReadInfo(mp);
            string? lbl = discInfo?.Label ?? c.Opt.Get("disc");
            if (lbl == null) { Console.WriteLine($"Ignoring {mp}: no disc.json there (for a single mount you can pass --disc <label>)."); continue; }
            if (discInfo?.KeyId != null && !c.Keys.Contains(discInfo.KeyId))
            {
                Console.WriteLine($"Ignoring {mp}: {lbl} is encrypted with another key. " + DiscFiles.OtherCatalogHint(mp));
                continue;
            }
            if (discInfo?.Unreadable() is { } why) { Console.WriteLine($"Ignoring {mp}: {why}."); continue; }
            mounts[lbl] = mp;
        }

        var dead = c.Arc.DeadDiscs();
        string? sourceFilter = c.Opt.Get("source");
        var wanted = c.Cat.Files.Where(f => Archive.Matches(f, query))
            .Where(f => sourceFilter == null || f.RootName.Equals(sourceFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.RootName, StringComparer.Ordinal).ThenBy(f => f.RelPath, StringComparer.Ordinal)
            .ToList();
        if (wanted.Count == 0) { Console.WriteLine("No file in the catalog matches."); return 1; }

        // pick the copy to restore (live discs only): the current version first, then the newest other version.
        // A split copy needs every one of its parts.
        // A file with several copies ('copies') is on several discs: each piece comes from a mounted one if it can.
        var jobs = new List<(FileRecord File, List<BackupRef> Copy)>();
        var noCopy = new List<FileRecord>();
        List<BackupRef>? Choose(List<BackupRef> live, string version) =>
            Archive.CompleteCopy(live, version)?.Select(piece => Candidates(live, piece).First()).ToList();
        IEnumerable<BackupRef> Candidates(List<BackupRef> live, BackupRef piece) =>
            live.Where(b => Hashing.SameHash(b.Sha256, piece.Sha256) && b.Part == piece.Part && b.Parts == piece.Parts &&
                            (!b.IsPart || SplitLayout.PartSizeOf(b) == SplitLayout.PartSizeOf(piece)))
                .OrderByDescending(b => mounts.ContainsKey(b.Disc)).ThenByDescending(b => b.BackedUpUtc != null).ThenBy(b => b.Alias);
        foreach (var f in wanted)
        {
            var live = f.Backups.Where(x => !dead.Contains(x.Disc)).ToList();
            var best = live.AsEnumerable().Reverse().Select(b => b.Sha256).Prepend(f.Sha256)
                .Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(v => Choose(live, v)).FirstOrDefault(set => set != null);
            if (best != null) jobs.Add((f, best));
            else if (!(f.Ignored && f.Backups.Count == 0)) noCopy.Add(f);   // ignored and never backed up: nothing was lost
        }

        int done = 0, failed = 0, skipped = 0, waiting = 0;
        var needDiscs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var readers = new Dictionary<string, PackReader>(StringComparer.OrdinalIgnoreCase);

        // the stored copy, read from its disc's packs into dst; returns the plaintext hash (InvalidDataException: damaged)
        string Fetch(BackupRef copy, string dst)
        {
            if (!readers.TryGetValue(copy.Disc, out var reader)) readers[copy.Disc] = reader = new PackReader(mounts[copy.Disc], c.Keys);
            using var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
            using var stored = reader.Open(copy.At, copy.StoredSize);
            return StoredForm.Read(stored, copy.Compression, output, Interrupt.Token);
        }

        // the first mounted copy among `options` that comes back intact, into dst; null = none (each failure is printed)
        BackupRef? FetchAny(IEnumerable<BackupRef> options, string dst, string expected, string what)
        {
            foreach (var copy in options.Where(o => mounts.ContainsKey(o.Disc)).DistinctBy(o => (o.Disc, o.At)))
            {
                if (File.Exists(dst)) File.Delete(dst);
                try
                {
                    if (Hashing.SameHash(Fetch(copy, dst), expected)) return copy;
                    Console.WriteLine($"CORRUPT  {what} on {copy.Disc} (hash mismatch)");
                }
                catch (InvalidDataException ex) { Console.WriteLine($"DAMAGED  {what} on {copy.Disc}: {ex.Message}"); }
                catch (IOException ex) { Console.WriteLine($"FAILED   {what} on {copy.Disc}: {ex.Message}"); }
                if (File.Exists(dst)) File.Delete(dst);
            }
            return null;
        }

        int handled = 0;
        try
        {
            foreach (var (f, copy) in jobs.OrderBy(j => j.Copy[0].Disc, StringComparer.Ordinal).ThenBy(j => j.Copy[0].At))
            {
                if (Interrupt.Requested) break;
                handled++;
                var live = f.Backups.Where(x => !dead.Contains(x.Disc)).ToList();
                string rel = flat ? Path.GetFileName(f.RelPath) : f.RootName + "/" + f.RelPath;
                string target = Path.GetFullPath(Path.Combine(dest, rel));
                if (!target.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"SKIPPED (path escapes destination)  {rel}");
                    failed++;
                    continue;
                }
                if (File.Exists(target) && !overwrite) { Console.WriteLine($"EXISTS   {rel}  (use --overwrite)"); skipped++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                string? inProgress = null;   // half-written file to remove if something throws
                try
                {
                    if (!copy[0].IsPart)
                    {
                        var whole = copy[0];
                        if (!mounts.ContainsKey(whole.Disc)) { needDiscs[whole.Disc] = needDiscs.GetValueOrDefault(whole.Disc) + 1; continue; }

                        inProgress = target + ".part";
                        if (FetchAny(Candidates(live, whole).Prepend(whole), inProgress, whole.Sha256, rel) == null)
                        {
                            Console.WriteLine($"FAILED   {rel}: no intact copy on the mounted discs");
                            failed++;
                            continue;
                        }
                        if (Hashing.SameHash(whole.Sha256, f.Sha256)) File.SetLastWriteTimeUtc(inProgress, f.ModifiedUtc);
                        File.Move(inProgress, target, overwrite: true);
                        inProgress = null;
                        done++;
                        Console.WriteLine($"restored {rel}");
                        continue;
                    }

                    // split file: the parts usually sit on different discs that are mounted one after another, so each
                    // verified part waits next to the target as <target>.bdxl-part.NNN until the last one is there
                    var held = copy.Select(p => target + ".bdxl-part" + SplitLayout.Suffix(p.Part, p.Parts)).ToList();
                    bool bad = false;
                    for (int i = 0; i < copy.Count; i++)
                    {
                        var p = copy[i];
                        if (File.Exists(held[i])) continue;   // fetched in an earlier run (kept only after its hash matched)
                        if (!mounts.ContainsKey(p.Disc)) { needDiscs[p.Disc] = needDiscs.GetValueOrDefault(p.Disc) + 1; continue; }

                        inProgress = held[i] + ".tmp";
                        if (FetchAny(Candidates(live, p).Prepend(p), inProgress, p.ContentSha256, $"{rel} part {p.Part}/{p.Parts}") == null) { bad = true; continue; }
                        File.Move(inProgress, held[i]);
                        inProgress = null;
                    }

                    int have = held.Count(File.Exists);
                    if (have < copy.Count)
                    {
                        Console.WriteLine($"PARTIAL  {rel}: {have} of {copy.Count} parts restored so far (kept next to it; run again with the other discs)");
                        if (bad) failed++; else waiting++;
                        continue;
                    }

                    // all parts there: join them and check the whole file
                    inProgress = target + ".part";
                    if (File.Exists(inProgress)) File.Delete(inProgress);
                    string joinedHash;
                    using (var output = new FileStream(inProgress, FileMode.CreateNew, FileAccess.Write))
                    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        var buf = new byte[1 << 20];
                        foreach (string h in held)
                        {
                            using var input = File.OpenRead(h);
                            int n;
                            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                            {
                                Interrupt.Token.ThrowIfCancellationRequested();
                                hash.AppendData(buf, 0, n);
                                output.Write(buf, 0, n);
                            }
                        }
                        joinedHash = Convert.ToHexString(hash.GetHashAndReset());
                    }
                    if (!Hashing.SameHash(joinedHash, copy[0].Sha256))
                    {
                        File.Delete(inProgress);
                        Console.WriteLine($"CORRUPT  {rel}: every part is intact, but joined they do not match the file's hash " +
                                          "(the source changed while it was being staged?). The parts are kept next to it.");
                        failed++;
                        continue;
                    }
                    if (Hashing.SameHash(copy[0].Sha256, f.Sha256)) File.SetLastWriteTimeUtc(inProgress, f.ModifiedUtc);
                    File.Move(inProgress, target, overwrite: true);
                    inProgress = null;
                    foreach (string h in held) File.Delete(h);
                    done++;
                    Console.WriteLine($"restored {rel}  (joined {copy.Count} parts)");
                }
                catch (OperationCanceledException)
                {
                    if (inProgress != null && File.Exists(inProgress)) File.Delete(inProgress);
                    Console.WriteLine($"STOPPED  {rel}  (Ctrl+C, not restored)");
                    handled--;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Console.WriteLine($"FAILED   {rel}: {ex.Message}");
                    if (inProgress != null && File.Exists(inProgress)) File.Delete(inProgress);
                    failed++;
                }
            }
        }
        finally
        {
            foreach (var r in readers.Values) r.Dispose();
        }

        Console.WriteLine($"\n{done:N0} restored, {skipped:N0} skipped (already exist), {failed:N0} failed" +
                          (waiting > 0 ? $", {waiting:N0} waiting for more parts." : "."));
        if (Interrupt.Requested)
        {
            Console.WriteLine($"Stopped by Ctrl+C: {jobs.Count - handled:N0} file(s) were not looked at. Run the same command again to continue " +
                              "(files already restored show as EXISTS).");
            return Interrupt.ExitCode;
        }
        if (needDiscs.Count > 0)
        {
            Console.WriteLine("Insert / mount these discs and run the same command again with --mount:");
            foreach (var kv in needDiscs)
                Console.WriteLine($"  {kv.Key}  ({kv.Value:N0} file(s))");
            if (jobs.Any(j => j.File.Backups.Where(b => !dead.Contains(b.Disc)).Select(b => b.Disc).Distinct().Count() > j.Copy.Count))
                Console.WriteLine("Some of these files have copies on other discs too ('find' lists them); any disc with a copy will do.");
        }
        if (failed > 0 && jobs.Any(j => j.File.Backups.Any(b => !dead.Contains(b.Disc) && !mounts.ContainsKey(b.Disc))))
            Console.WriteLine("Files that failed may have copies on discs that are not mounted ('find' lists them).");
        if (noCopy.Count > 0)
        {
            Console.WriteLine($"\n{noCopy.Count:N0} file(s) have no surviving copy on any disc:");
            foreach (var f in noCopy.Take(50)) Console.WriteLine($"  {f.RootName}/{f.RelPath}");
        }
        return failed > 0 ? 3 : needDiscs.Count > 0 || noCopy.Count > 0 || waiting > 0 ? 2 : 0;
    }
}
