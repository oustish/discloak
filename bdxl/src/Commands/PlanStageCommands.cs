namespace Bdxl;

public static class PlanCommand
{
    /// <summary>
    /// Allocates pending files to the registered blank discs (see 'blanks') and reports whether everything fits.
    /// Changes nothing; 'stage' uses exactly the same allocation.
    /// </summary>
    public static int Run(Ctx c)
    {
        var plan = c.Arc.MakePlan(c.Opt.Has("encrypt"), c.Opt.Has("split"));
        Console.WriteLine($"Pending: {plan.PendingFiles:N0} file(s), {SizeText.Bytes(plan.PendingBytes)} bytes");
        int splitFiles = plan.Pending.Where(p => p.IsPart).Select(p => p.File).Distinct().Count();
        if (splitFiles > 0)
            Console.WriteLine($"({splitFiles:N0} file(s) are split across discs: {plan.Pending.Count(p => p.IsPart):N0} part(s) still to store.)");

        int ignored = c.Cat.Files.Count(f => f.Ignored);
        if (ignored > 0) Console.WriteLine($"({ignored:N0} catalogued file(s) are ignored by rules and not part of the plan.)");

        if (c.Cat.Blanks.Count == 0)
        {
            if (plan.Pending.Count > 0)
                Console.WriteLine("No blank discs are registered, so nothing can be allocated. Register some, e.g.:\n" +
                                  "  blanks add 5 --size 96882130944 --reserve 200MB");
            return plan.Pending.Count > 0 ? 2 : 0;
        }

        Console.WriteLine($"Blank discs: {c.Cat.Blanks.Count} ({SizeText.Bytes(c.Cat.Blanks.Sum(b => b.Usable))} usable bytes)\n");
        foreach (var bin in plan.Bins.Where(b => b.Pieces.Count > 0))
        {
            int parts = bin.Pieces.Count(p => p.IsPart);
            Console.WriteLine($"  {bin.Blank.Id}  {bin.Blank.Capacity,18:N0} B disc: {bin.Pieces.Count,8:N0} files, {bin.Used,18:N0} bytes " +
                              $"({100.0 * bin.Used / bin.Usable:F1}% of usable)" + (parts > 0 ? $"  incl. {parts} part(s) of split files" : ""));
        }
        int unused = plan.Bins.Count(b => b.Pieces.Count == 0);
        if (unused > 0) Console.WriteLine($"  {unused} blank disc(s) would stay unused.");

        if (plan.Unplaced.Count == 0)
        {
            Console.WriteLine("\nAll pending data fits on the designated blank discs.");
            return 0;
        }

        long left = plan.Unplaced.Sum(p => p.Length);
        Console.WriteLine($"\nNOT ALL DATA FITS: {plan.Unplaced.Count:N0} file(s), {SizeText.Bytes(left)} bytes cannot be placed on the {c.Cat.Blanks.Count} designated blank disc(s).");
        Archive.PrintTooBig(plan.TooBig);
        var rest = plan.Unplaced.Where(p => !plan.TooBig.Contains(p.File)).ToList();
        if (rest.Count > 0)
        {
            // more discs only help with what fits on one disc; files larger than any disc need --split (or bigger discs)
            long restBytes = rest.Sum(p => p.Length), biggest = c.Cat.Blanks.Max(b => b.Usable);
            Console.WriteLine($"Roughly {(restBytes + biggest - 1) / biggest} more disc(s) of the largest size would be needed for the rest.");
            Console.WriteLine("Left over (do not fit in the remaining space):");
            foreach (var p in rest.OrderByDescending(x => x.Length).Take(20))
                Console.WriteLine($"  {p.Name}  ({SizeText.Bytes(p.Length)} bytes)");
            if (rest.Count > 20) Console.WriteLine($"  ... and {rest.Count - 20:N0} more");
        }
        return 2;
    }
}

public static class StageCommand
{
    public static int Run(Ctx c)
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

/// <summary>Files every staged disc carries so it can describe itself.</summary>
public static class DiscFiles
{
    public static void Write(string dir, DiscRecord disc, List<ManifestLine> manifest)
    {
        JsonStore.WriteLines(Path.Combine(dir, "manifest.jsonl"), manifest);
        JsonStore.WriteObject(Path.Combine(dir, "disc.json"), new DiscInfo(disc.Label, disc.CreatedUtc, disc.FileCount, disc.UsedBytes));
    }
}
