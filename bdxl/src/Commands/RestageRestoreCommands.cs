namespace Bdxl;

/// <summary>restage (rebuild a disc folder) and restore (copy files back from mounted discs)</summary>
public static class RestageRestoreCommands
{
    // ---- restage --------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the folder for an existing disc (same label, same paths, same content) so it can be burned again.
    /// Data comes from the current source files if their hash still matches, otherwise from --from folders.
    /// Files that are ignored now are still included: the disc is rebuilt the way it was.
    /// </summary>
    public static int Restage(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: restage <label> <destDir> [--from dirA;dirB] [--allow-partial]");
        var disc = c.Arc.FindDisc(c.Args[0]);
        string dir = Path.Combine(Path.GetFullPath(c.Args[1]), disc.Label);
        if (Directory.Exists(dir)) throw new UserException($"Folder already exists: {dir}");
        var fromDirs = (c.Opt.Get("from") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool allowPartial = c.Opt.Has("allow-partial");

        var entries = c.Cat.Files.SelectMany(f => f.Backups.Where(b => b.Disc == disc.Label).Select(b => (File: f, Copy: b)))
            .GroupBy(x => x.Copy.DiscPath, StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (entries.Count == 0) throw new UserException($"The catalog has no files for {disc.Label}.");
        var onSource = c.Cat.Files.Where(f => f.MissingSinceUtc == null && f.Sha256.Length > 0)
            .ToLookup(f => f.Sha256, StringComparer.OrdinalIgnoreCase);

        var manifest = new List<ManifestLine>();
        var missing = new List<BackupRef>();
        KeyCache? keys = null;
        byte[]? salt = null;

        foreach (var (file, entry) in entries)
        {
            string dst = Path.Combine(dir, entry.DiscPath);
            var sourceFiles = onSource[entry.Sha256].Select(f => Path.Combine(f.Root, f.RelPath)).Where(File.Exists).ToList();
            var fromFiles = fromDirs.Select(fd => Path.Combine(fd, entry.DiscPath)).Where(File.Exists).ToList();
            long offset = entry.Offset, length = entry.IsPart ? entry.Length : -1;   // a part is cut from the whole source file
            bool restored = false;
            string? storedHash = null;

            if (entry.Encrypted)
            {
                // 1. an existing encrypted copy is taken over unchanged if it is intact
                foreach (var src in fromFiles)
                {
                    if (TryCopy(src, dst, entry.StoredSha256)) { restored = true; storedHash = entry.StoredSha256; break; }
                }
                // 2. otherwise encrypt the source again (new salt/nonces, so the stored hash changes)
                if (!restored && sourceFiles.Count > 0)
                {
                    keys ??= new KeyCache(Ui.GetPassword(c.Opt, forNewEncryption: true));
                    salt ??= FileCrypto.NewSalt();
                    foreach (var src in sourceFiles)
                    {
                        try
                        {
                            string plain = FileCrypto.Encrypt(src, dst, keys, salt, offset, length);
                            if (Hashing.SameHash(plain, entry.ContentSha256))
                            {
                                File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                                storedHash = Hashing.HashFile(dst);
                                restored = true;
                                break;
                            }
                            File.Delete(dst);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Console.WriteLine($"  {src}: {ex.Message}");
                            if (File.Exists(dst)) File.Delete(dst);
                        }
                    }
                }
            }
            else
            {
                foreach (var src in sourceFiles)
                {
                    if (TryCopy(src, dst, entry.ContentSha256, offset, length)) { restored = true; break; }
                }
                if (!restored)
                    foreach (var src in fromFiles)   // --from holds disc copies: already just the part
                    {
                        if (TryCopy(src, dst, entry.ContentSha256)) { restored = true; break; }
                    }
            }

            if (restored)
            {
                Console.WriteLine($"[{disc.Label}] {entry.DiscPath}");
                if (entry.Encrypted && !Hashing.SameHash(storedHash!, entry.StoredSha256 ?? ""))
                    foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label && b.DiscPath == entry.DiscPath))
                        b.StoredSha256 = storedHash;   // includes entry itself
                manifest.Add(ManifestLine.For(entry, new FileInfo(dst).Length, file.Size));
            }
            else missing.Add(entry);
        }

        if (missing.Count > 0)
        {
            Console.WriteLine($"\n{missing.Count:N0} file(s) could not be rebuilt (no source file with identical content, none found under --from):");
            foreach (var m in missing.Take(50)) Console.WriteLine("  " + m.DiscPath);
            if (!allowPartial)
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Console.WriteLine("\nNothing staged and the catalog is unchanged. Add --from <folder;folder> to point at other copies, " +
                                  "or --allow-partial to restage without those files.");
                return 3;
            }
            var gonePaths = missing.Select(m => m.DiscPath).ToHashSet(StringComparer.Ordinal);
            foreach (var f in c.Cat.Files) f.Backups.RemoveAll(b => b.Disc == disc.Label && gonePaths.Contains(b.DiscPath));
        }

        if (manifest.Count == 0)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            throw new UserException("No file could be rebuilt; the catalog is unchanged.");
        }

        disc.State = "staged";           // it has to be burned again
        disc.BurnedUtc = null;
        disc.LostUtc = null;
        disc.RestagedUtc = DateTime.UtcNow;
        disc.FileCount = manifest.Count;
        disc.UsedBytes = manifest.Sum(x => x.Size);
        foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label)) b.BackedUpUtc = null;

        DiscFiles.Write(dir, disc, manifest);
        c.Arc.LinkDuplicates();
        c.Cat.Save();
        Console.WriteLine($"\n{disc.Label}: {manifest.Count:N0} files, {SizeText.Bytes(disc.UsedBytes)} bytes rebuilt in {dir}");
        Console.WriteLine($"Burn it, then run:  burned {disc.Label}");
        return 0;
    }

    /// <summary>Copies src (or a byte range of it) to dst if the copy has the expected hash; otherwise removes dst and returns false.</summary>
    static bool TryCopy(string src, string dst, string? expectedHash, long offset = 0, long length = -1)
    {
        try
        {
            string got = Hashing.CopyAndHash(src, dst, offset, length);
            if (expectedHash != null && Hashing.SameHash(got, expectedHash))
            {
                File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                return true;
            }
            File.Delete(dst);   // wrong content (changed / damaged) - try the next candidate
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  {src}: {ex.Message}");
            if (File.Exists(dst)) File.Delete(dst);
        }
        return false;
    }

    // ---- restore --------------------------------------------------------------------------

    /// <summary>Copies files back from mounted discs. Layout under destDir: source alias/relative path (or --flat).</summary>
    public static int Restore(Ctx c)
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
            if (!copy.Encrypted) return Hashing.CopyAndHash(src, dst);
            keys ??= new KeyCache(Ui.GetPassword(c.Opt, forNewEncryption: false));
            return FileCrypto.Decrypt(src, dst, keys);
        }

        foreach (var (f, copy) in jobs.OrderBy(j => j.Copy[0].Disc, StringComparer.Ordinal).ThenBy(j => j.Copy[0].DiscPath, StringComparer.Ordinal))
        {
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
                        while ((n = input.Read(buf, 0, buf.Length)) > 0) { hash.AppendData(buf, 0, n); output.Write(buf, 0, n); }
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
