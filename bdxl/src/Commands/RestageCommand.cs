namespace Bdxl;

public sealed class RestageCommand : ICommand
{
    public string Name => "restage";
    public string Group => CommandGroup.Recovery;
    public string Summary => "Rebuild a disc's folder (same label, same files) to burn it again";
    public string Usage => "restage <label> <destDir> [--from dirA;dirB] [--allow-partial] [--password-env VAR]";
    public bool Interruptible => true;
    public string Details => """
        Takes the data from the current source files (if their hash still matches) or from the --from folders, checks
        every file against the catalog and writes the folder again. Files that are ignored now are included: the
        disc is rebuilt the way it was. Encrypted discs: intact encrypted copies are reused, other files are
        encrypted again; the password (BDXL_PASSWORD, --password-env or typed) is always needed for an
        encrypted disc, because its manifest is written encrypted, and it is checked against the disc's password.
        A disc with appended sessions is rebuilt as one folder. Afterwards the disc is 'staged' until you run
        'burned'.
        Without --allow-partial nothing is staged if any file cannot be rebuilt.
        Ctrl+C stops after the file being written (that one is removed); the catalog stays unchanged and what was
        rebuilt is kept as <destDir>/<label>.partial, to be reused with --from on the next run.
        """;

    /// <summary>
    /// Rebuilds the folder for an existing disc (same label, same paths, same content) so it can be burned again.
    /// Data comes from the current source files if their hash still matches, otherwise from --from folders.
    /// Files that are ignored now are still included: the disc is rebuilt the way it was.
    /// </summary>
    public int Run(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: restage <label> <destDir> [--from dirA;dirB] [--allow-partial]");
        var disc = c.Arc.FindDisc(c.Args[0]);
        if (disc.PendingAppend is { } session)
            throw new UserException($"Session {session.Number} of {disc.Label} is staged but not burned: burn it ('burned {disc.Label}') " +
                                    $"or take it back ('discard {disc.Label}') first.");
        string dir = Path.Combine(Path.GetFullPath(c.Args[1]), disc.Label);
        if (Directory.Exists(dir)) throw new UserException($"Folder already exists: {dir}");
        var fromDirs = (c.Opt.Get("from") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool allowPartial = c.Opt.Has("allow-partial");

        var entries = c.Arc.CopiesOn(disc.Label);
        if (entries.Count == 0) throw new UserException($"The catalog has no files for {disc.Label}.");
        var onSource = c.Cat.Files.Where(f => f.MissingSinceUtc == null && f.Sha256.Length > 0)
            .ToLookup(f => f.Sha256, StringComparer.OrdinalIgnoreCase);

        var manifest = new List<ManifestLine>();
        var missing = new List<BackupRef>();
        KeyCache? keys = null;
        byte[]? salt = null;
        // the password must be the disc's own (copies under --from are reused as they are); without a check value
        // in the catalog, it is checked against one of them
        var probes = entries.Where(e => e.Copy.Encrypted).SelectMany(e => fromDirs.Select(fd => Path.Combine(fd, e.Copy.DiscPath)));
        if (disc.Encrypted)   // the manifest of an encrypted disc is always written encrypted
        {
            keys = DiscPassword.Get(c, disc, probes, allowUnchecked: true);
            salt = FileCrypto.NewSalt();
        }

        string? writing = null;   // file being written when Ctrl+C cut it off
        try
        {
            foreach (var (file, entry) in entries)
            {
                if (Interrupt.Requested) break;
                string dst = Path.Combine(dir, entry.DiscPath);
                writing = dst;
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
                        keys ??= DiscPassword.Get(c, disc, probes, allowUnchecked: true);
                        salt ??= FileCrypto.NewSalt();
                        foreach (var src in sourceFiles)
                        {
                            try
                            {
                                string plain = FileCrypto.Encrypt(src, dst, keys, salt, offset, length, Interrupt.Token);
                                if (Hashing.SameHash(plain, entry.ContentSha256))
                                {
                                    storedHash = Hashing.HashFile(dst, Interrupt.Token);
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
                    long storedSize = new FileInfo(dst).Length;
                    foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label && b.DiscPath == entry.DiscPath))
                    {
                        b.StoredSize = storedSize;   // includes entry itself
                        if (entry.Encrypted) b.StoredSha256 = storedHash;
                    }
                    manifest.Add(ManifestLine.For(entry, storedSize, file));
                }
                else missing.Add(entry);
            }
        }
        catch (OperationCanceledException)
        {
            if (writing != null && File.Exists(writing)) File.Delete(writing);
        }

        if (Interrupt.Requested)   // the catalog stays as it was; what was rebuilt can be picked up again with --from
        {
            string kept = dir + ".partial";
            for (int n = 2; Directory.Exists(kept); n++) kept = $"{dir}.partial{n}";
            if (Directory.Exists(dir)) Directory.Move(dir, kept);
            Console.WriteLine($"\nStopped by Ctrl+C after rebuilding {manifest.Count:N0} of {entries.Count:N0} files. The catalog is unchanged." +
                              (manifest.Count > 0 ? $" They are kept in {kept}; to reuse them, run restage again with  --from \"{kept}\"" : ""));
            return Interrupt.ExitCode;
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
        disc.Sessions = 1;               // the whole disc (all its sessions) is in one folder now
        foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label)) b.BackedUpUtc = null;

        DiscFiles.Write(dir, disc, manifest, keys, salt);
        long metadata = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length) - disc.UsedBytes;
        disc.OccupiedBytes = disc.UsedBytes + Math.Max(disc.Reserve ?? 0, metadata);
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
            string got = Hashing.CopyAndHash(src, dst, offset, length, Interrupt.Token);
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
}
