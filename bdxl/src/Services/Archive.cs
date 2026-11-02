namespace Bdxl;

/// <summary>Queries and bookkeeping shared by the commands (dedup, planning, labels, ignore rules).</summary>
public sealed class Archive
{
    readonly Catalog cat;

    public Archive(Catalog cat) { this.cat = cat; }

    // ---- discs ----------------------------------------------------------------------------

    public HashSet<string> DeadDiscs() =>
        cat.Discs.Where(d => d.State is "lost" or "destroyed").Select(d => d.Label).ToHashSet();

    public DiscRecord FindDisc(string label) =>
        cat.Discs.FirstOrDefault(d => d.Label.Equals(label, StringComparison.OrdinalIgnoreCase))
        ?? throw new UserException($"Unknown disc '{label}'.");

    /// <summary>
    /// Next disc label = prefix + counter. The counter lives in settings.json and is never reset, so changing
    /// the prefix later continues the numbering instead of starting over.
    /// </summary>
    public string NextLabel()
    {
        var s = cat.Settings;
        if (s.LabelCounter == 0)   // catalogs from before the counter existed: continue after the highest number in use
        {
            foreach (var d in cat.Discs)
            {
                string? tail = null;
                if (d.Label.StartsWith(s.LabelPrefix, StringComparison.OrdinalIgnoreCase)) tail = d.Label[s.LabelPrefix.Length..];
                else if (d.Label.StartsWith("BDXL-", StringComparison.OrdinalIgnoreCase)) tail = d.Label[5..];
                if (tail != null && int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > s.LabelCounter)
                    s.LabelCounter = n;
            }
        }

        string label;
        do
        {
            s.LabelCounter++;
            label = s.LabelPrefix + s.LabelCounter.ToString("D4", CultureInfo.InvariantCulture);
        }
        while (cat.Discs.Any(d => d.Label.Equals(label, StringComparison.OrdinalIgnoreCase)));
        return label;
    }

    /// <summary>
    /// Every copy stored on a disc, one per position in its data stream (in stream order), with the file it was stored
    /// for (rather than an alias).
    /// </summary>
    public List<(FileRecord File, BackupRef Copy)> CopiesOn(string label) =>
        cat.Files.SelectMany(f => f.Backups.Where(b => b.Disc == label).Select(b => (File: f, Copy: b)))
           .GroupBy(x => x.Copy.At)
           .Select(g => g.OrderBy(x => x.Copy.Alias).First())
           .OrderBy(x => x.Copy.At)
           .ToList();

    public string NextBlankId()
    {
        int n = cat.Blanks.Select(b => b.Id.StartsWith('B') && int.TryParse(b.Id.AsSpan(1), out int v) ? v : 0)
            .DefaultIfEmpty(0).Max() + 1;
        return $"B{n:D4}";
    }

    // ---- dedup ----------------------------------------------------------------------------

    /// <summary>content hash -> every copy (whole or part) on a live disc, from any file</summary>
    public ILookup<string, BackupRef> LiveCopies()
    {
        var dead = DeadDiscs();
        return cat.Files.SelectMany(f => f.Backups).Where(b => !dead.Contains(b.Disc))
                  .ToLookup(b => b.Sha256, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The copies that together hold content `sha` completely: one whole copy, or every part of one split layout
    /// (parts may sit on different discs). Prefers burned copies, then real ones over aliases. null = incomplete.
    /// </summary>
    public static List<BackupRef>? CompleteCopy(IEnumerable<BackupRef> refs, string sha)
    {
        var same = refs.Where(b => Hashing.SameHash(b.Sha256, sha))
                       .OrderByDescending(b => b.BackedUpUtc != null).ThenBy(b => b.Alias).ToList();
        var whole = same.FirstOrDefault(b => !b.IsPart);
        if (whole != null) return [whole];
        return PartSets(same).FirstOrDefault(set => set.Count == set[0].Parts);
    }

    /// <summary>Parts grouped by split layout (one entry per part number, in order), most complete layout first.</summary>
    public static IEnumerable<List<BackupRef>> PartSets(IEnumerable<BackupRef> refs) =>
        refs.Where(b => b.IsPart)
            .GroupBy(b => (b.Parts, SplitLayout.PartSizeOf(b)))
            .Select(g => g.GroupBy(b => b.Part).Select(p => p.First()).OrderBy(b => b.Part).ToList())
            .OrderByDescending(set => set.Count);

    public bool HasLiveCopyOfCurrent(FileRecord f, HashSet<string> dead) =>
        CompleteCopy(f.Backups.Where(b => !dead.Contains(b.Disc)), f.Sha256) != null;

    /// <summary>Some version of the file (current or older) can still be restored completely from live discs.</summary>
    public static bool HasAnyLiveCopy(FileRecord f, HashSet<string> dead)
    {
        var live = f.Backups.Where(b => !dead.Contains(b.Disc)).ToList();
        return live.Select(b => b.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Any(v => CompleteCopy(live, v) != null);
    }

    /// <summary>Hashes of files still present on a source (a moved/renamed file keeps its content there).</summary>
    public HashSet<string> SourceHashes() =>
        cat.Files.Where(f => f.MissingSinceUtc == null && f.Sha256 != "").Select(f => f.Sha256)
                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gives every file whose content is already completely on live discs backup references (Alias = true) to that
    /// copy - one per part for a split copy.
    /// </summary>
    public int LinkDuplicates()
    {
        var dead = DeadDiscs();
        var live = LiveCopies();
        int n = 0;
        foreach (var f in cat.Files.Where(f => f.MissingSinceUtc == null && !f.Ignored && f.Sha256.Length > 0 && !HasLiveCopyOfCurrent(f, dead)))
        {
            var copy = CompleteCopy(live[f.Sha256], f.Sha256);
            if (copy == null) continue;
            f.Backups.AddRange(copy.Select(r => r.AsAlias()));
            n++;
        }
        return n;
    }

    // ---- planning -------------------------------------------------------------------------

    /// <summary>
    /// Pending = still on the source, not ignored, hashed, and no live disc holds its content completely. Identical
    /// content is stored once. The pending pieces are allocated to the registered blank discs; what does not fit is
    /// reported. A file larger than every blank disc is cut into parts only with split = true; a file that already
    /// has parts on discs always continues with the same layout (only its missing parts are pending).
    /// into = allocate to these bins instead of the blank discs (stage --append); whether a file is too big, and
    /// the part size of a new split, still follow the blank discs.
    /// </summary>
    public PlanResult MakePlan(bool split = false, IReadOnlyList<BlankDisc>? into = null)
    {
        var live = LiveCopies();
        var wanted = WantedCopies();
        var pendingFiles = cat.Files
            .Where(f => f.MissingSinceUtc == null && !f.Ignored && f.Sha256.Length > 0)
            .GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => f.RootName + "/" + f.RelPath, StringComparer.Ordinal).First())
            .Where(f => CompleteCopies(live[f.Sha256], f.Sha256) < wanted(f.Sha256))
            .ToList();

        // the room of a disc is what its packs can hold (DiscFormat.Room); a piece takes its stored size (whole files
        // maybe compressed, sizes counted by scan; parts never) and its line in the disc's manifest
        Func<BlankDisc, long> roomOf = b => DiscFormat.Room(b.Usable);
        long largest = cat.Blanks.Select(roomOf).DefaultIfEmpty(0).Max();
        Func<Piece, long> sizeOf = p => StoredSizeOf(cat.Settings, p) + DiscSpace.ManifestShare(p);
        long PartSizeFor(FileRecord f) => largest - DiscSpace.ManifestShare(new Piece(f, 1, 2, 0, 0));
        var pending = new List<Piece>();
        var tooBig = new List<FileRecord>();
        int furtherCopies = 0;

        foreach (var f in pendingFiles)
        {
            var refs = live[f.Sha256].ToList();
            int before = pending.Count;
            bool hasOne = CompleteCopies(refs, f.Sha256) > 0;
            int missing = wanted(f.Sha256) - WholeCopies(refs, f.Sha256);   // copies still to make (> what a split layout has)
            var layout = Layouts(refs, f.Sha256).FirstOrDefault();
            if (layout != null)   // continue the layout: each part until it is on enough discs
            {
                foreach (var piece in SplitLayout.Pieces(f, layout.PartSize))
                    for (int n = layout.DiscsWith(piece.Part); n < missing; n++) pending.Add(piece);
            }
            else if (cat.Blanks.Count > 0 && sizeOf(Piece.Whole(f)) > largest)
            {
                if (split && PartSizeFor(f) is > 0 and long partSize)
                    for (int n = 0; n < missing; n++) pending.AddRange(SplitLayout.Pieces(f, partSize));
                else { tooBig.Add(f); pending.Add(Piece.Whole(f)); }
            }
            else
                for (int n = 0; n < missing; n++) pending.Add(Piece.Whole(f));
            if (hasOne) furtherCopies += pending.Count - before;
        }

        // copies of one content never share a disc (nor go onto a disc that holds it already); with one copy wanted
        // there is nothing to keep apart
        string? GroupOf(Piece p) => wanted(p.File.Sha256) > 1 || into != null ? p.File.Sha256 : null;
        bool Holds(BlankDisc disc, string sha) => live[sha].Any(b => b.Disc.Equals(disc.Id, StringComparison.OrdinalIgnoreCase));

        var tooBigSet = tooBig.ToHashSet();
        var (bins, leftover) = Packer.Allocate(pending.Where(p => !tooBigSet.Contains(p.File)), into ?? cat.Blanks, sizeOf,
                                               GroupOf, into != null ? Holds : null, roomOf);
        var unplaced = leftover.Concat(pending.Where(p => tooBigSet.Contains(p.File))).ToList();
        return new PlanResult(bins, unplaced, tooBig, pending, furtherCopies);
    }

    /// <summary>Bytes a piece takes in a disc's data stream: a part as it is, a whole file compressed if scan counted that.</summary>
    public static long StoredSizeOf(Settings s, Piece p) => p.IsPart ? p.Length : Compression.PlannedStoredSize(s, p.File);

    // ---- copies ---------------------------------------------------------------------------

    /// <summary>Copies wanted of each content on a source: the most any location holding it asks for ('copies').</summary>
    public Func<string, int> WantedCopies()
    {
        if (!cat.Settings.AnyExtraCopies) return _ => 1;   // the usual case: nothing to look up
        var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in cat.Files.Where(f => !f.Ignored && f.Sha256.Length > 0))
        {
            int n = cat.Settings.CopiesFor(f.RootName);
            if (!wanted.TryGetValue(f.Sha256, out int had) || n > had) wanted[f.Sha256] = n;
        }
        return sha => wanted.TryGetValue(sha, out int n) ? n : 1;
    }

    /// <summary>A split layout of a content: its part count and size, and on how many different discs each part is.</summary>
    public sealed record Layout(int Parts, long PartSize, int[] Discs)
    {
        public int DiscsWith(int part) => Discs[part - 1];
        public int Copies => Discs.Min();
    }

    /// <summary>The split layouts of content sha among refs, the one with the most complete copies (then parts) first.</summary>
    public static IEnumerable<Layout> Layouts(IEnumerable<BackupRef> refs, string sha) =>
        refs.Where(b => b.IsPart && Hashing.SameHash(b.Sha256, sha))
            .GroupBy(b => (b.Parts, SplitLayout.PartSizeOf(b)))
            .Select(g => new Layout(g.Key.Parts, g.Key.Item2, Enumerable.Range(1, g.Key.Parts)
                .Select(p => g.Where(b => b.Part == p).Select(b => b.Disc).Distinct(StringComparer.OrdinalIgnoreCase).Count()).ToArray()))
            .OrderByDescending(l => l.Copies).ThenByDescending(l => l.Discs.Count(n => n > 0));

    static int WholeCopies(IList<BackupRef> refs, string sha)
    {
        if (refs.Count == 1) return !refs[0].IsPart && Hashing.SameHash(refs[0].Sha256, sha) ? 1 : 0;   // the usual case, cheaply
        return refs.Where(b => !b.IsPart && Hashing.SameHash(b.Sha256, sha)).Select(b => b.Disc).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    /// <summary>Complete copies of content sha among refs, each on its own disc(s): whole copies + copies of its best split layout.</summary>
    public static int CompleteCopies(IEnumerable<BackupRef> refs, string sha)
    {
        var list = refs as IList<BackupRef> ?? refs.ToList();
        if (list.Count == 0) return 0;
        bool parts = false;
        foreach (var b in list) parts |= b.IsPart;
        return WholeCopies(list, sha) + (parts ? Layouts(list, sha).FirstOrDefault()?.Copies ?? 0 : 0);
    }

    /// <summary>Burned copies on live discs (staged ones do not count yet) of the file's current content.</summary>
    public static int BurnedCopies(FileRecord f, HashSet<string> dead) =>
        CompleteCopies(f.Backups.Where(b => b.BackedUpUtc != null && !dead.Contains(b.Disc)).ToList(), f.Sha256);

    /// <summary>Files on a source with fewer burned copies than their location wants (current content; ignored files not).</summary>
    public List<FileRecord> TooFewCopies()
    {
        var dead = DeadDiscs();
        var burned = cat.Files.SelectMany(f => f.Backups).Where(b => b.BackedUpUtc != null && !dead.Contains(b.Disc))
                        .ToLookup(b => b.Sha256, StringComparer.OrdinalIgnoreCase);
        return cat.Files.Where(f => f.MissingSinceUtc == null && !f.Ignored && f.Sha256.Length > 0 &&
                                    CompleteCopies(burned[f.Sha256], f.Sha256) < cat.Settings.CopiesFor(f.RootName))
                        .ToList();
    }

    public static void PrintTooBig(List<FileRecord> tooBig)
    {
        if (tooBig.Count == 0) return;
        Console.WriteLine("These files are larger than the largest registered blank disc (add --split to store them in parts across discs):");
        foreach (var f in tooBig) Console.WriteLine($"  {f.RootName}/{f.RelPath}  ({SizeText.Bytes(f.Size)} bytes)");
        Console.WriteLine();
    }

    // ---- search ---------------------------------------------------------------------------

    public static bool Matches(FileRecord f, string text) =>
        text == "*" ||
        $"{f.RootName}/{f.RelPath}".Contains(text, StringComparison.OrdinalIgnoreCase) ||   // the form find/restore print
        (f.Sha256.Length > 0 && f.Sha256.StartsWith(text, StringComparison.OrdinalIgnoreCase)) ||
        f.Backups.Any(b => b.Sha256.StartsWith(text, StringComparison.OrdinalIgnoreCase));   // an older version on a disc

    // ---- ignore rules ---------------------------------------------------------------------

    public GitIgnore BuildIgnore() => new GitIgnore(cat.Settings.IgnoreRules);

    /// <summary>
    /// Re-evaluates every catalogued file against the current ignore rules. A file that was pending becomes ignored
    /// and is no longer staged; a file already on a disc stays on it (restage still includes it) but no new backup
    /// of it is made. Returns (became ignored, no longer ignored).
    /// </summary>
    public (int NowIgnored, int NoLongerIgnored) ApplyIgnoreRules()
    {
        var ignore = BuildIgnore();
        int now = 0, noLonger = 0;
        foreach (var f in cat.Files)
        {
            bool ignored = ignore.IsIgnored(f.RelPath);
            if (ignored && !f.Ignored) { f.Ignored = true; now++; }
            else if (!ignored && f.Ignored) { f.Ignored = false; noLonger++; }
        }
        return (now, noLonger);
    }
}
