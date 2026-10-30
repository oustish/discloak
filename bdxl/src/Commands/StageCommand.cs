namespace Bdxl;

public sealed class StageCommand : ICommand
{
    public string Name => "stage";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Copy the next disc's worth of files into a folder, ready to burn";
    public string Usage => "stage <destDir> [--max-discs N] [--min-fill 0.9] [--split] [--with-catalog] [--append <label> [--mount <path>]]";
    public bool Interruptible => true;
    public string Details => """
        Creates <destDir>/<label>/ for each disc. Every file is encrypted on its own with the catalog's master key
        (AES-256-GCM, one .enc file per file, no archive; compressed if set, see 'compression') under a name that says nothing
        (data/000/000001.enc ...) and checked against its hash. The disc also gets an encrypted manifest
        (manifest.jsonl.enc) and, readable without the password: disc.json (label, file count, size), bdxl.key (the
        master key, wrapped by today's password) and README.txt (the formats, to read the disc without bdxl).
          --max-discs N    stage up to N discs (default 1)
          --min-fill 0.9   only stage discs that would be at least 90% full
          --split          a file larger than every blank disc is cut into parts, one per disc, each encrypted on its
                           own. 'restore' collects the parts disc by disc and joins them; the file counts as backed up
                           only when every part is on a disc. Once a file has parts on discs, its remaining parts are
                           staged even without --split.
          --with-catalog   also put a catalog backup (with repair data) into <disc>/_catalog
          --append L       fill the free space of the burned disc L with a new session instead of using a blank disc.
                           Only the pending files that fit go into <destDir>/L.s2/ (s3, s4 ... later), together with a
                           manifest of the WHOLE disc, disc.json and, with --with-catalog, _catalog; burned as the next
                           session they replace the old ones. The disc must not be finalized, and the burning program
                           must continue the disc's file system (multisession / "import previous session"), or the
                           earlier files disappear from view. Free space = disc size - earlier sessions - the disc's
                           reserve, which every session needs again (metadata and the medium's session overhead).
                           A changed file whose older version is on the disc is added under a new hidden name, so both
                           versions stay restorable. Only a disc encrypted with this catalog's master key can take a
                           session (a plain disc from before cannot).
                           Afterwards: 'burned L' for the session, or 'discard L' to take it back.
          --mount P        with --append: the disc is in the drive at P - its label (and key) is checked, and the free
                           space the drive reports is used if it is smaller.
        Labels are <prefix><counter>, e.g. BDXL-0007 (see 'settings').
        Ctrl+C stops after the file being copied (that one is removed): the disc (or session) is finished with the
        files copied so far - a complete, smaller folder you can burn or 'discard' - and no further disc is started.
        """;

    public int Run(Ctx c)
    {
        if (c.Args.Count != 1) throw new UserException("Usage: " + Usage);
        string destDir = Path.GetFullPath(c.Args[0]);
        if (c.Opt.Get("append") is { } appendTo) return Append(c, destDir, appendTo);
        int maxDiscs = c.Opt.GetInt("max-discs", 1);
        double minFill = double.Parse(c.Opt.GetOr("min-fill", "0"), CultureInfo.InvariantCulture);
        var plan = c.Arc.MakePlan(c.Opt.Has("split"));
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

        int overfull = 0;
        for (int n = 0; n < chosen.Count; n++)
        {
            var bin = chosen[n];
            string label = c.Arc.NextLabel();
            string dir = Path.Combine(destDir, label);
            if (Directory.Exists(dir)) throw new UserException($"Folder already exists: {dir}");

            var disc = new DiscRecord
            {
                Label = label, State = "staged", CreatedUtc = DateTime.UtcNow,
                Capacity = bin.Blank.Capacity, Reserve = bin.Blank.Reserve, Cost = bin.Blank.Cost, Encrypted = true, KeyId = c.Key.KeyId
            };
            c.Cat.Discs.Add(disc);
            int hidden = 0;
            var manifest = StorePieces(disc, dir, bin.Pieces, _ => DiscFiles.HiddenName(++hidden), c.Key, c.Settings);
            bool cutShort = Interrupt.Requested;
            int notStarted = chosen.Count - n - 1;

            if (disc.FileCount == 0)
            {
                c.Cat.Discs.Remove(disc);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Console.WriteLine($"{label}: nothing could be staged.");
                if (cutShort) return Stopped($"Nothing was staged on {label}.", notStarted + 1);
                continue;
            }

            c.Cat.Blanks.Remove(bin.Blank);   // this blank disc is now spoken for
            var (dups, folderBytes) = Finish(c, disc, dir, manifest, sessionFiles: disc.UsedBytes, occupiedBefore: 0, c.Key);
            Console.WriteLine($"{label}: {disc.FileCount:N0} files, {SizeText.Bytes(disc.UsedBytes)} bytes staged in {dir}" +
                              (dups > 0 ? $" ({dups} duplicate file(s) linked, not stored again)" : ""));

            // manifest, disc.json and _catalog live in the reserve - check they really fit
            if (disc.Capacity is long capacity && folderBytes > capacity)
            {
                Console.WriteLine($"WARNING: {label} folder is {SizeText.Bytes(folderBytes)} bytes, more than the disc holds " +
                                  $"({SizeText.Bytes(capacity)}); it will not burn. Raise the reserve ('blanks reserve'), " +
                                  $"then 'discard {label}' and stage again.");
                overfull++;
            }

            if (Interrupt.Requested)
                return Stopped(cutShort
                    ? $"{label} holds {disc.FileCount:N0} of its {bin.Pieces.Count:N0} planned files ({SizeText.Bytes(disc.UsedBytes)} of " +
                      $"{SizeText.Bytes(bin.Used)} bytes); its folder is complete for what it holds. Burn it as it is, or " +
                      $"'discard {label}' and stage again to fill it."
                    : $"{label} is complete.", notStarted);
        }

        Console.WriteLine("\nBurn each staged folder, then run:  burned <label>   (and optionally: verify <label> <mountPath>)");
        return overfull > 0 ? 2 : 0;
    }

    /// <summary>stage --append: the pending files that fit go into one more session of a burned disc.</summary>
    static int Append(Ctx c, string destDir, string label)
    {
        foreach (string option in new[] { "max-discs", "min-fill" })
            if (c.Opt.Has(option)) throw new UserException($"--{option} does not go with --append.");
        var target = AppendTarget.Open(c, label);
        var disc = target.Disc;

        var plan = c.Arc.MakePlan(c.Opt.Has("split"), [target.AsBlank()]);
        var bin = plan.Bins[0];
        Console.WriteLine($"{disc.Label}: {SizeText.Bytes(target.Free)} bytes free for files in session {target.Session} " +
                          $"({SizeText.Bytes(target.Reserve)} bytes stay reserved for its manifest, disc.json and catalog backup).");
        if (bin.Pieces.Count == 0)
        {
            if (plan.Pending.Count == 0) { Console.WriteLine("Nothing to stage: every file is backed up."); return 0; }
            Console.WriteLine($"Nothing staged: none of the pending files fits into the free space of {disc.Label}.");
            return 2;
        }
        if (plan.Unplaced.Count > 0)
            Console.WriteLine($"Note: {plan.Unplaced.Count:N0} pending file(s) do not fit and stay pending.");

        string dir = Path.Combine(destDir, $"{disc.Label}.s{target.Session}");
        if (Directory.Exists(dir)) throw new UserException($"Folder already exists: {dir}");

        var key = c.Keys.For(disc.KeyId);   // the disc's own key (checked by AppendTarget), even after a key rotation
        disc.KeyId = key.KeyId;
        var onDisc = c.Arc.CopiesOn(disc.Label);
        int hidden = onDisc.Select(x => DiscFiles.HiddenNumber(x.Copy.DiscPath)).DefaultIfEmpty(0).Max();
        long occupiedBefore = AppendTarget.Occupied(disc);
        var session = new AppendSession
        {
            Number = target.Session, CreatedUtc = DateTime.UtcNow,
            FileCountBefore = disc.FileCount, UsedBytesBefore = disc.UsedBytes, OccupiedBytesBefore = disc.OccupiedBytes
        };
        disc.PendingAppend = session;
        disc.Sessions = session.Number;
        StorePieces(disc, dir, bin.Pieces, _ => DiscFiles.HiddenName(++hidden), key, c.Settings);
        bool cutShort = Interrupt.Requested;

        if (disc.FileCount == session.FileCountBefore)
        {
            disc.PendingAppend = null;
            disc.Sessions = session.Number - 1;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Console.WriteLine($"{disc.Label}: nothing could be staged.");
            return cutShort ? Stopped($"Nothing was staged for {disc.Label}.", 0) : 2;
        }

        // the new manifest replaces the old one on the disc, so it lists every file, old and new
        var manifest = c.Arc.CopiesOn(disc.Label).Select(x => ManifestLine.For(x.Copy, x.File))
                            .OrderBy(m => m.Path, StringComparer.Ordinal).ToList();
        long sessionFiles = disc.UsedBytes - session.UsedBytesBefore;
        var (dups, folderBytes) = Finish(c, disc, dir, manifest, sessionFiles, occupiedBefore, key);
        Console.WriteLine($"{disc.Label} session {session.Number}: {disc.FileCount - session.FileCountBefore:N0} files, " +
                          $"{SizeText.Bytes(sessionFiles)} bytes staged in {dir}" +
                          (dups > 0 ? $" ({dups} duplicate file(s) linked, not stored again)" : ""));
        Console.WriteLine($"Its manifest lists all {manifest.Count:N0} files of the disc.");

        if (folderBytes > target.Room)
        {
            Console.WriteLine($"WARNING: the session folder is {SizeText.Bytes(folderBytes)} bytes, more than {disc.Label} has left " +
                              $"({SizeText.Bytes(target.Room)}); it will not burn. 'discard {disc.Label}' and append again " +
                              "(without --with-catalog if the catalog backup does not fit into the reserve).");
            return 2;
        }
        if (cutShort)
            return Stopped($"Session {session.Number} holds {disc.FileCount - session.FileCountBefore:N0} of its {bin.Pieces.Count:N0} planned " +
                           "files; its folder is complete for what it holds. Burn it as it is (see below), or " +
                           $"'discard {disc.Label}' and append again.\n\nBurn the folder onto {disc.Label} as its next session, then run:  burned {disc.Label}", 0);
        Console.WriteLine($"\nBurn the folder onto {disc.Label} as its next session - the burning program must continue the disc's " +
                          $"file system (multisession / import previous session) - then run:  burned {disc.Label}");
        return 0;
    }

    /// <summary>
    /// Encrypts each piece into dir under the (hidden) name storedPathFor gives it, checks what was read against the
    /// catalog and records it on the disc. A piece that changed since the scan or cannot be read is skipped.
    /// Returns the manifest lines of the stored copies.
    /// </summary>
    static List<ManifestLine> StorePieces(DiscRecord disc, string dir, IEnumerable<Piece> pieces, Func<Piece, string> storedPathFor,
                                          MasterKey key, Settings settings)
    {
        var manifest = new List<ManifestLine>();
        foreach (var p in pieces)
        {
            if (Interrupt.Requested) break;
            var f = p.File;
            string storedPath = storedPathFor(p);
            string src = Path.Combine(f.Root, f.RelPath);
            string dst = Path.Combine(dir, storedPath);
            int level = p.IsPart ? 0 : Compression.LevelFor(settings, f);   // a part keeps its planned plain size
            Console.WriteLine($"[{disc.Label}] {p.Name}" + (storedPath != p.Name ? $"  ->  {storedPath}" : ""));

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
                plainHash = FileCrypto.Encrypt(src, dst, key, KeyPurpose.File, p.Offset, p.Length, Interrupt.Token, level);
            }
            catch (OperationCanceledException)
            {
                if (File.Exists(dst)) File.Delete(dst);
                Console.WriteLine("  stopped (Ctrl+C) - not staged");
                break;
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

            long storedSize = new FileInfo(dst).Length;
            string storedHash = Hashing.HashFile(dst);   // lets 'verify' check the disc without decrypting

            var copy = new BackupRef
            {
                Disc = disc.Label, DiscPath = storedPath, Sha256 = f.Sha256, Encrypted = true, StoredSha256 = storedHash, StoredSize = storedSize,
                Compression = level
            };
            if (p.IsPart)
            {
                copy.Part = p.Part; copy.Parts = p.Parts; copy.Offset = p.Offset; copy.Length = p.Length;
                copy.PartSha256 = plainHash;
            }
            f.Backups.Add(copy);
            disc.FileCount++;
            disc.UsedBytes += storedSize;
            manifest.Add(ManifestLine.For(copy, storedSize, f));
        }
        return manifest;
    }

    /// <summary>
    /// Writes manifest + disc.json, links duplicates, saves, and writes the catalog backup (--with-catalog). The disc
    /// counts as occupied by the session's files plus its reserve - or its metadata, if that came out larger.
    /// </summary>
    static (int Dups, long FolderBytes) Finish(Ctx c, DiscRecord disc, string dir, List<ManifestLine> manifest,
                                               long sessionFiles, long occupiedBefore, MasterKey key)
    {
        DiscFiles.Write(dir, disc, manifest, key, c.Cat.Dir);   // the disc describes itself (manifest + disc.json [+ bdxl.key, README])
        int dups = c.Arc.LinkDuplicates();                  // other files with identical content now point at this disc too
        long reserve = disc.Reserve ?? 0;
        disc.OccupiedBytes = occupiedBefore + sessionFiles + reserve;
        c.Cat.Save();
        if (c.Opt.Has("with-catalog"))                      // leave room for it in the blank discs' reserve
        {
            Console.WriteLine("Writing catalog backup into the disc folder:");
            CatalogCommand.WriteBackup(c, Path.Combine(dir, "_catalog"));
        }

        long folderBytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
        if (folderBytes - sessionFiles > reserve)           // the metadata outgrew the reserve
        {
            disc.OccupiedBytes = occupiedBefore + folderBytes;
            c.Cat.Save();
        }
        return (dups, folderBytes);
    }

    static int Stopped(string what, int notStarted)
    {
        Console.WriteLine($"\nStopped by Ctrl+C. {what}" + (notStarted > 0 ? $" {notStarted} more planned disc(s) were not started." : ""));
        return Interrupt.ExitCode;
    }

    static bool SameAsScanned(string path, FileRecord f)
    {
        var fi = new FileInfo(path);
        return fi.Exists && fi.Length == f.Size && fi.LastWriteTimeUtc == f.ModifiedUtc;
    }
}
