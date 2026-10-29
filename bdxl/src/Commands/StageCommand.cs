namespace Bdxl;

public sealed class StageCommand : ICommand
{
    public string Name => "stage";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Copy the next disc's worth of files into a folder, ready to burn";
    public string Usage => "stage <destDir> [--max-discs N] [--min-fill 0.9] [--encrypt] [--password-env VAR] [--split] [--with-catalog]";
    public string Details => """
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
        """;

    public int Run(Ctx c)
    {
        if (c.Args.Count != 1) throw new UserException("Usage: stage <destDir> [--max-discs N] [--min-fill 0.9] [--encrypt] [--split] [--with-catalog]");
        string destDir = Path.GetFullPath(c.Args[0]);
        int maxDiscs = c.Opt.GetInt("max-discs", 1);
        double minFill = double.Parse(c.Opt.GetOr("min-fill", "0"), CultureInfo.InvariantCulture);
        bool encrypt = c.Opt.Has("encrypt");

        var plan = c.Arc.MakePlan(encrypt, c.Opt.Has("split"));
        if (c.Cat.Blanks.Count == 0)
        {
            Console.WriteLine("No blank discs are registered. Register some first, e.g.:  blanks add 5 --size 96882130944 --reserve 200MB");
            return 1;
        }
        Archive.PrintTooBig(plan.TooBig);

        var chosen = plan.Bins.Where(b => b.Pieces.Count > 0 && b.Used >= minFill * b.Usable)
            .OrderByDescending(b => (double)b.Used / b.Usable).Take(maxDiscs).ToList();
        if (chosen.Count == 0)
        {
            if (plan.Pending.Count == 0) { Console.WriteLine("Nothing to stage: every file is backed up."); return 0; }
            Console.WriteLine(plan.Bins.Any(b => b.Pieces.Count > 0)
                ? "Nothing staged: no blank disc would be full enough (see --min-fill)."
                : "Nothing staged: the pending files do not fit on any registered blank disc (see 'plan').");
            return 2;
        }
        if (plan.Unplaced.Count > 0)
            Console.WriteLine($"Note: {plan.Unplaced.Count:N0} pending file(s) do not fit on the registered blank discs (see 'plan').\n");

        KeyCache? keys = null;
        if (encrypt)
        {
            keys = new KeyCache(Ui.GetPassword(c.Opt, forNewEncryption: true));
            Console.WriteLine("Files will be encrypted (AES-256-GCM). File names stay readable in manifest.jsonl / disc.json" +
                              (c.Opt.Has("with-catalog") ? " and in the catalog backup" : "") + " on the disc.\n");
        }

        int overfull = 0;
        foreach (var bin in chosen)
        {
            string label = c.Arc.NextLabel();
            string dir = Path.Combine(destDir, label);
            if (Directory.Exists(dir)) throw new UserException($"Folder already exists: {dir}");

            var disc = new DiscRecord
            {
                Label = label, State = "staged", CreatedUtc = DateTime.UtcNow,
                Capacity = bin.Blank.Capacity, Reserve = bin.Blank.Reserve, Cost = bin.Blank.Cost, Encrypted = encrypt
            };
            c.Cat.Discs.Add(disc);
            var manifest = new List<ManifestLine>();
            byte[]? salt = encrypt ? FileCrypto.NewSalt() : null;

            foreach (var p in bin.Pieces)
            {
                var f = p.File;
                string storedPath = p.Name + (encrypt ? FileCrypto.Suffix : "");
                string src = Path.Combine(f.Root, f.RelPath);
                string dst = Path.Combine(dir, storedPath);
                Console.WriteLine($"[{label}] {storedPath}");

                // a part cannot be checked against the whole file's hash, so the source must be exactly as scanned
                // (same size and time - the rule 'scan' uses), before and after the copy
                if (p.IsPart && !SameAsScanned(src, f))
                {
                    Console.WriteLine("  changed since last scan - not staged. Re-run 'scan'.");
                    continue;
                }

                string plainHash;
                try
                {
                    plainHash = encrypt ? FileCrypto.Encrypt(src, dst, keys!, salt!, p.Offset, p.Length)
                                        : Hashing.CopyAndHash(src, dst, p.Offset, p.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"  FAILED: {ex.Message}");
                    if (File.Exists(dst)) File.Delete(dst);
                    continue;
                }

                if (p.IsPart ? !SameAsScanned(src, f) : !Hashing.SameHash(plainHash, f.Sha256))
                {
                    File.Delete(dst);
                    Console.WriteLine("  changed since last scan - not staged. Re-run 'scan'.");
                    continue;
                }

                File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
                long storedSize = new FileInfo(dst).Length;
                string? storedHash = encrypt ? Hashing.HashFile(dst) : null;

                var copy = new BackupRef
                {
                    Disc = label, DiscPath = storedPath, Sha256 = f.Sha256, Encrypted = encrypt, StoredSha256 = storedHash
                };
                if (p.IsPart)
                {
                    copy.Part = p.Part; copy.Parts = p.Parts; copy.Offset = p.Offset; copy.Length = p.Length;
                    copy.PartSha256 = plainHash;
                }
                f.Backups.Add(copy);
                disc.FileCount++;
                disc.UsedBytes += storedSize;
                manifest.Add(ManifestLine.For(copy, storedSize, f.Size));
            }

            if (disc.FileCount == 0)
            {
                c.Cat.Discs.Remove(disc);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Console.WriteLine($"{label}: nothing could be staged.");
                continue;
            }

            c.Cat.Blanks.Remove(bin.Blank);                 // this blank disc is now spoken for
            DiscFiles.Write(dir, disc, manifest);           // disc describes itself (manifest.jsonl + disc.json)
            int dups = c.Arc.LinkDuplicates();              // other files with identical content now point at this disc too
            c.Cat.Save();
            if (c.Opt.Has("with-catalog"))                  // leave room for it in the blank discs' reserve
            {
                Console.WriteLine("Writing catalog backup into the disc folder:");
                CatalogCommand.WriteBackup(c, Path.Combine(dir, "_catalog"));
            }
            Console.WriteLine($"{label}: {disc.FileCount:N0} files, {SizeText.Bytes(disc.UsedBytes)} bytes staged in {dir}" +
                              (dups > 0 ? $" ({dups} duplicate file(s) linked, not stored again)" : ""));

            // manifest, disc.json and _catalog live in the reserve - check they really fit
            long folderBytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
            if (disc.Capacity is long capacity && folderBytes > capacity)
            {
                Console.WriteLine($"WARNING: {label} folder is {SizeText.Bytes(folderBytes)} bytes, more than the disc holds " +
                                  $"({SizeText.Bytes(capacity)}); it will not burn. Raise the reserve ('blanks reserve'), " +
                                  $"then 'discard {label}' and stage again.");
                overfull++;
            }
        }

        Console.WriteLine("\nBurn each staged folder, then run:  burned <label>   (and optionally: verify <label> <mountPath>)");
        return overfull > 0 ? 2 : 0;
    }

    static bool SameAsScanned(string path, FileRecord f)
    {
        var fi = new FileInfo(path);
        return fi.Exists && fi.Length == f.Size && fi.LastWriteTimeUtc == f.ModifiedUtc;
    }
}
