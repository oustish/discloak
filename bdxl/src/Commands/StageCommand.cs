namespace Bdxl;

public sealed class StageCommand : ICommand
{
    public string Name => "stage";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Copy the next disc's worth of files into a folder, ready to burn";
    public string Usage => "stage <destDir> [--max-discs N] [--min-fill 0.9] [--split] [--with-catalog] [--append <label> [--mount <path>]]";
    public bool Interruptible => true;
    public string Details => """
        Creates <destDir>/<label>/ for each disc. The files (compressed if set, see 'compression') are laid end to end
        in one stream of data, cut into packs of 64 MiB (data/000/000001.pack ...), each pack encrypted on its own with
        the catalog's master key (AES-256-GCM); the last pack is filled up with zeros. Every file is checked against
        its hash as it is read. Nothing on the disc shows the names, sizes or number of the files. The disc also gets
        an encrypted manifest (manifest.enc: where each file lies in the stream, padded to whole packs) and, readable
        without the password: packs.sha256 (the packs' hashes), disc.json (label, date, key id), bdxl.key (the master
        key, wrapped by today's password) and README.txt (the formats, to read the disc without bdxl).
          --max-discs N    stage up to N discs (default 1): the fullest first; of discs equally full (to 0.1%), the one
                           with the largest file first
          --min-fill 0.9   only stage discs that would be at least 90% full
          --split          a file larger than every blank disc is cut into parts, one per disc. 'restore' collects the
                           parts disc by disc and joins them; the file counts as backed up only when every part is on a
                           disc. Once a file has parts on discs, its remaining parts are staged even without --split.
          --with-catalog   also put a catalog backup (with repair data) into <disc>/_catalog
          --append L       fill the free space of the burned disc L with a new session instead of using a blank disc.
                           Only the pending files that fit go into <destDir>/L.s2/ (s3, s4 ... later): packs that
                           continue the disc's numbering, their own manifest (manifest.s2.enc: only these files; the
                           earlier ones stay), packs.sha256 and disc.json (both replace the old ones) and, with
                           --with-catalog, _catalog. The disc must not be finalized, and the burning program
                           must continue the disc's file system (multisession / "import previous session"), or the
                           earlier files disappear from view. Free space = disc size - earlier sessions - the disc's
                           reserve, which every session needs again (metadata and the medium's session overhead).
                           A changed file whose older version is on the disc is stored again, so both versions stay
                           restorable. Only a disc encrypted with a key of this catalog can take a session.
                           Afterwards: 'burned L' for the session, or 'discard L' to take it back.
          --mount P        with --append: the disc is in the drive at P - its label (and key) is checked, and the free
                           space the drive reports is used if it is smaller.
        Labels are <prefix><counter>, e.g. BDXL-0007 (see 'settings').
        Every file is printed as soon as it is stored, with the disc's progress by bytes, speed and time left; while a
        large file is copied, a status line shows how far it is (not written when the output is redirected).
        Ctrl+C stops after the file being copied (that one is left out): the disc (or session) is finished with the
        files copied so far - a complete, smaller folder you can burn or 'discard' - and no further disc is started.
        A file that changed since 'scan' or cannot be read is left out too; the space it took in its pack stays unused.
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

        // the fullest discs first (a half-empty one waits for more data); discs that are equally full to 0.1% - as
        // nearly all are - in the order of their largest file, so the largest pending file goes first
        var chosen = plan.Bins.Where(b => b.Pieces.Count > 0 && b.Used >= minFill * b.Usable)
            .OrderByDescending(b => (int)(1000.0 * b.Used / b.Usable)).ThenByDescending(b => b.Pieces.Max(p => p.Length))
            .Take(maxDiscs).ToList();
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
                Capacity = bin.Blank.Capacity, Reserve = bin.Blank.Reserve, Cost = bin.Blank.Cost, KeyId = c.Key.KeyId
            };
            c.Cat.Discs.Add(disc);
            var manifest = StorePieces(disc, dir, bin.Pieces, c.Key, c.Settings);
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
            var (dups, folderBytes) = Finish(c, disc, dir, manifest, Space(disc.Packs.Count, manifest), occupiedBefore: 0, c.Key, session: 1);
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
        long occupiedBefore = AppendTarget.Occupied(disc);
        var session = new AppendSession
        {
            Number = target.Session, CreatedUtc = DateTime.UtcNow, FileCountBefore = disc.FileCount, UsedBytesBefore = disc.UsedBytes,
            OccupiedBytesBefore = disc.OccupiedBytes, PacksBefore = disc.Packs.Count
        };
        disc.PendingAppend = session;
        disc.Sessions = session.Number;
        var manifest = StorePieces(disc, dir, bin.Pieces, key, c.Settings);   // its packs continue the disc's numbering
        bool cutShort = Interrupt.Requested;

        if (disc.FileCount == session.FileCountBefore)
        {
            disc.PendingAppend = null;
            disc.Sessions = session.Number - 1;
            disc.Packs.RemoveRange(session.PacksBefore, disc.Packs.Count - session.PacksBefore);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Console.WriteLine($"{disc.Label}: nothing could be staged.");
            return cutShort ? Stopped($"Nothing was staged for {disc.Label}.", 0) : 2;
        }

        // the session's own manifest (manifest.sN.enc) lists only its files; the earlier manifests stay on the disc
        long sessionFiles = disc.UsedBytes - session.UsedBytesBefore;
        var (dups, folderBytes) = Finish(c, disc, dir, manifest, Space(disc.Packs.Count - session.PacksBefore, manifest), occupiedBefore, key, session.Number);
        Console.WriteLine($"{disc.Label} session {session.Number}: {disc.FileCount - session.FileCountBefore:N0} files, " +
                          $"{SizeText.Bytes(sessionFiles)} bytes staged in {dir}" +
                          (dups > 0 ? $" ({dups} duplicate file(s) linked, not stored again)" : ""));
        Console.WriteLine($"Its own manifest ({DiscFiles.ManifestFor(session.Number)}) lists them; those of the earlier sessions stay on the disc.");

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
    /// Lays each piece into the disc's data stream (new packs in dir, after the disc's packs so far), checks what was
    /// read against the catalog and records it on the disc. A piece that changed since the scan or cannot be read is
    /// left out (the space it took in its pack stays unused). The last pack is filled up and the packs' hashes are
    /// added to the disc. Returns the manifest lines of the stored copies.
    /// </summary>
    static List<ManifestLine> StorePieces(DiscRecord disc, string dir, IEnumerable<Piece> pieces, MasterKey key, Settings settings)
    {
        var manifest = new List<ManifestLine>();
        var list = pieces.ToList();
        var meter = new ProgressMeter(list.Sum(p => p.Length));
        Console.WriteLine($"{disc.Label}: {list.Count:N0} file(s), {SizeText.Bytes(meter.Total)} bytes to store in {dir}");
        using var packs = new PackWriter(dir, disc.Packs.Count + 1, key);
        for (int n = 1; n <= list.Count && !Interrupt.Requested; n++)
        {
            var p = list[n - 1];
            var f = p.File;
            string src = Path.Combine(f.Root, f.RelPath);
            int level = p.IsPart ? 0 : Compression.StoredLevel(settings, f);   // a part keeps its planned plain size
            // printed when the file is done: "  [3/302  12.4%  85.3 MB/s  ~3h 12m left] S/a.bin ..."
            void Done(string text)
            {
                meter.Done += p.Length;
                meter.WriteLine($"  [{StatusLine.Count(n, list.Count)}  {meter.Rate()}] {p.Name}{text}");
            }
            const string Changed = ": changed since last scan - not staged. Re-run 'scan'.";

            // a part cannot be checked against the whole file's hash, so the source must be exactly as scanned
            // (same size and time - the rule 'scan' uses), before and after the copy
            if (p.IsPart && !SameAsScanned(src, f)) { Done(Changed); continue; }

            long at = packs.Position;
            string plainHash;
            long stored;
            try
            {
                using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, StoredForm.Block, FileOptions.SequentialScan);
                input.Seek(p.Offset, SeekOrigin.Begin);
                // never more than planned: the plan (and the disc) has room for exactly that
                (plainHash, stored) = StoredForm.Write(input, p.Length, level, packs.Write, Archive.StoredSizeOf(settings, p), Interrupt.Token,
                    read => meter.Show(disc.Label, $"{p.Name} ({StatusLine.Percent(read, p.Length)})", read));
            }
            catch (OperationCanceledException)
            {
                Done(": stopped (Ctrl+C) - not staged");
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Done($": FAILED: {ex.Message}");
                continue;
            }
            catch (StoredForm.TooLargeException)
            {
                Done(Changed);
                continue;
            }

            if (p.IsPart ? !SameAsScanned(src, f) : !Hashing.SameHash(plainHash, f.Sha256)) { Done(Changed); continue; }

            var copy = new BackupRef
            {
                Disc = disc.Label, At = at, StoredSize = stored, Length = p.Length, Sha256 = f.Sha256, Compression = level
            };
            if (p.IsPart)
            {
                copy.Part = p.Part; copy.Parts = p.Parts; copy.Offset = p.Offset;
                copy.PartSha256 = plainHash;
            }
            f.Backups.Add(copy);
            disc.FileCount++;
            disc.UsedBytes += stored;
            manifest.Add(ManifestLine.For(copy, f));
            Done($" ({SizeText.Bytes(p.Length)} bytes" + (level > 0 ? $", compressed to {100.0 * stored / Math.Max(1, p.Length):F0}%)" : ")"));
        }
        meter.Clear();
        packs.Finish();
        disc.Packs.AddRange(packs.Written.Select(w => w.Sha256));
        return manifest;
    }

    /// <summary>Space a session takes on the disc: its packs (sectors, file system entries and lines in packs.sha256) and its manifest.</summary>
    static long Space(int packs, List<ManifestLine> manifest) => packs * DiscFormat.PackOnDisc + DiscFiles.ManifestSpace(manifest);

    /// <summary>
    /// Writes manifest + disc.json, links duplicates, saves, and writes the catalog backup (--with-catalog). The disc
    /// counts as occupied by the space of the session's packs and manifest plus its reserve - or the space of the whole
    /// folder, if its metadata came out larger. Returns that folder's space on the disc.
    /// </summary>
    static (int Dups, long FolderBytes) Finish(Ctx c, DiscRecord disc, string dir, List<ManifestLine> manifest,
                                               long sessionSpace, long occupiedBefore, MasterKey key, int session)
    {
        disc.SetManifest(session, DiscFiles.Write(dir, disc, manifest, key, c.Cat.Dir, session));   // the disc describes itself
        int dups = c.Arc.LinkDuplicates();                  // other files with identical content now point at this disc too
        long reserve = disc.Reserve ?? 0;
        disc.OccupiedBytes = occupiedBefore + sessionSpace + reserve;
        c.Cat.Save();
        if (c.Opt.Has("with-catalog"))                      // leave room for it in the blank discs' reserve
        {
            Console.WriteLine("Writing catalog backup into the disc folder:");
            CatalogCommand.WriteBackup(c, Path.Combine(dir, DiscFiles.CatalogBackupName));
        }

        long folderBytes = DiscSpace.Folder(dir);
        if (folderBytes - sessionSpace > reserve)           // the metadata outgrew the reserve
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
