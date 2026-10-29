namespace Bdxl;

public sealed class RestoreCommand : ICommand
{
    public string Name => "restore";
    public string Group => CommandGroup.Recovery;
    public string Summary => "Copy files back from mounted discs into a folder";
    public string Usage => "restore <destDir> <text|hashPrefix|*> --mount F:\\;G:\\ [--disc label] [--source alias] [--flat] [--overwrite]";
    public bool Interruptible => true;
    public string Details => """
        Finds the files in the catalog, tells you which discs it needs, and copies them from the discs you mounted.
        Discs are recognised by disc.json (or --disc for a single mount). Every file is verified by hash; encrypted
        files are decrypted (password from BDXL_PASSWORD or typed). Layout: <destDir>/<alias>/<path>, or --flat.
        Ctrl+C stops after the file being copied (that one is removed; parts of split files already verified are
        kept) and lists what is left; running the same command again continues (restored files show as EXISTS).
        """;

    /// <summary>Copies files back from mounted discs. Layout under destDir: source alias/relative path (or --flat).</summary>
    public int Run(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: restore <destDir> <text|hashPrefix|*> --mount F:\\;G:\\ [--disc label] [--source name] [--flat] [--overwrite]");
        string dest = Path.GetFullPath(c.Args[0]);
        string query = c.Args[1];
        string destPrefix = dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool flat = c.Opt.Has("flat"), overwrite = c.Opt.Has("overwrite");

        // which disc is mounted where (identified by disc.json written at stage time)
        var mounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mp in (c.Opt.Get("mount") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string info = Path.Combine(mp, "disc.json");
            string? lbl = File.Exists(info) ? JsonStore.ReadObject<DiscInfo>(info)?.Label : c.Opt.Get("disc");
            if (lbl == null) { Console.WriteLine($"Ignoring {mp}: no disc.json there (for a single mount you can pass --disc <label>)."); continue; }
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
        var jobs = new List<(FileRecord File, List<BackupRef> Copy)>();
        var noCopy = new List<FileRecord>();
        foreach (var f in wanted)
        {
            var live = f.Backups.Where(x => !dead.Contains(x.Disc)).ToList();
            var best = live.AsEnumerable().Reverse().Select(b => b.Sha256).Prepend(f.Sha256)
                .Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(v => Archive.CompleteCopy(live, v)).FirstOrDefault(set => set != null);
            if (best != null) jobs.Add((f, best));
            else if (!(f.Ignored && f.Backups.Count == 0)) noCopy.Add(f);   // ignored and never backed up: nothing was lost
        }

        int done = 0, failed = 0, skipped = 0, waiting = 0, authFailures = 0;
        var needDiscs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        KeyCache? keys = null;

        // copies (or decrypts) one stored file to dst; returns the plaintext hash
        string Fetch(BackupRef copy, string src, string dst)
        {
            if (!copy.Encrypted) return Hashing.CopyAndHash(src, dst, ct: Interrupt.Token);
            keys ??= new KeyCache(Ui.GetPassword(c.Opt, forNewEncryption: false));
            return FileCrypto.Decrypt(src, dst, keys, Interrupt.Token);
        }

        int handled = 0;
        foreach (var (f, copy) in jobs.OrderBy(j => j.Copy[0].Disc, StringComparer.Ordinal).ThenBy(j => j.Copy[0].DiscPath, StringComparer.Ordinal))
        {
            if (Interrupt.Requested) break;
            handled++;
            string rel = flat ? Path.GetFileName(f.RelPath) : f.RootName + "/" + f.RelPath;
            string target = Path.GetFullPath(Path.Combine(dest, rel));
            if (!target.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"SKIPPED (path escapes destination)  {rel}");
                failed++;
                continue;
            }
            if (File.Exists(target) && !overwrite) { Console.WriteLine($"EXISTS   {rel}  (use --overwrite)"); skipped++; continue; }

            string? inProgress = null;   // half-written file to remove if something throws
            try
            {
                if (!copy[0].IsPart)
                {
                    var whole = copy[0];
                    if (!mounts.TryGetValue(whole.Disc, out var root)) { needDiscs[whole.Disc] = needDiscs.GetValueOrDefault(whole.Disc) + 1; continue; }
                    string src = Path.Combine(root, whole.DiscPath);
                    if (!File.Exists(src)) { Console.WriteLine($"MISSING  {whole.Disc}: {whole.DiscPath}"); failed++; continue; }

                    inProgress = target + ".part";
                    if (File.Exists(inProgress)) File.Delete(inProgress);
                    if (!Hashing.SameHash(Fetch(whole, src, inProgress), whole.Sha256))
                    {
                        File.Delete(inProgress);
                        Console.WriteLine($"CORRUPT  {whole.Disc}: {whole.DiscPath}  (hash mismatch, not restored)");
                        failed++;
                        continue;
                    }
                    File.SetLastWriteTimeUtc(inProgress, File.GetLastWriteTimeUtc(src));
                    File.Move(inProgress, target, overwrite: true);
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
                    if (!mounts.TryGetValue(p.Disc, out var root)) { needDiscs[p.Disc] = needDiscs.GetValueOrDefault(p.Disc) + 1; continue; }
                    string src = Path.Combine(root, p.DiscPath);
                    if (!File.Exists(src)) { Console.WriteLine($"MISSING  {p.Disc}: {p.DiscPath}"); bad = true; continue; }

                    inProgress = held[i] + ".tmp";
                    if (File.Exists(inProgress)) File.Delete(inProgress);
                    if (!Hashing.SameHash(Fetch(p, src, inProgress), p.ContentSha256))
                    {
                        File.Delete(inProgress);
                        Console.WriteLine($"CORRUPT  {p.Disc}: {p.DiscPath}  (hash mismatch)");
                        bad = true;
                        continue;
                    }
                    File.SetLastWriteTimeUtc(inProgress, File.GetLastWriteTimeUtc(src));
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
                File.SetLastWriteTimeUtc(inProgress, File.GetLastWriteTimeUtc(held[0]));
                File.Move(inProgress, target, overwrite: true);
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
            catch (InvalidDataException ex)
            {
                if (inProgress != null && File.Exists(inProgress)) File.Delete(inProgress);
                Console.WriteLine($"FAILED   {rel}: {ex.Message}");
                failed++;
                if (++authFailures >= 3) { Console.WriteLine("Stopping after 3 decryption failures - check the password."); break; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"FAILED   {rel}: {ex.Message}");
                if (inProgress != null && File.Exists(inProgress)) File.Delete(inProgress);
                failed++;
            }
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
        }
        if (noCopy.Count > 0)
        {
            Console.WriteLine($"\n{noCopy.Count:N0} file(s) have no surviving copy on any disc:");
            foreach (var f in noCopy.Take(50)) Console.WriteLine($"  {f.RootName}/{f.RelPath}");
        }
        return failed > 0 ? 3 : needDiscs.Count > 0 || noCopy.Count > 0 || waiting > 0 ? 2 : 0;
    }
}
