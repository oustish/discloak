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

    /// <summary>Every copy stored on a disc, one per stored path, with the file it was stored for (rather than an alias).</summary>
    public List<(FileRecord File, BackupRef Copy)> CopiesOn(string label) =>
        cat.Files.SelectMany(f => f.Backups.Where(b => b.Disc == label).Select(b => (File: f, Copy: b)))
           .GroupBy(x => x.Copy.DiscPath, StringComparer.Ordinal)
           .Select(g => g.OrderBy(x => x.Copy.Alias).First())
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
        var pendingFiles = cat.Files
            .Where(f => f.MissingSinceUtc == null && !f.Ignored && f.Sha256.Length > 0 && CompleteCopy(live[f.Sha256], f.Sha256) == null)
            .GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => f.RootName + "/" + f.RelPath, StringComparer.Ordinal).First())
            .ToList();

        Func<Piece, long> sizeOf = p => FileCrypto.StoredSize(p.Length);   // every staged piece is encrypted
        long largest = cat.Blanks.Select(b => b.Usable).DefaultIfEmpty(0).Max();
        // the part size leaves room for encryption, so a layout works whether later parts are encrypted or not
        long newPartSize = FileCrypto.MaxPlainSize(largest);
        var pending = new List<Piece>();
        var tooBig = new List<FileRecord>();

        foreach (var f in pendingFiles)
        {
            var started = PartSets(live[f.Sha256]).FirstOrDefault();
            if (started != null)
            {
                var have = started.Select(b => b.Part).ToHashSet();
                pending.AddRange(SplitLayout.Pieces(f, SplitLayout.PartSizeOf(started[0])).Where(p => !have.Contains(p.Part)));
            }
            else if (cat.Blanks.Count > 0 && sizeOf(Piece.Whole(f)) > largest)
            {
                if (split && newPartSize > 0) pending.AddRange(SplitLayout.Pieces(f, newPartSize));
                else { tooBig.Add(f); pending.Add(Piece.Whole(f)); }
            }
            else pending.Add(Piece.Whole(f));
        }

        var tooBigSet = tooBig.ToHashSet();
        var (bins, leftover) = Packer.Allocate(pending.Where(p => !tooBigSet.Contains(p.File)), into ?? cat.Blanks, sizeOf);
        var unplaced = leftover.Concat(pending.Where(p => tooBigSet.Contains(p.File))).ToList();
        return new PlanResult(bins, unplaced, tooBig, pending);
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
        f.Backups.Any(b => b.Sha256.StartsWith(text, StringComparison.OrdinalIgnoreCase) ||
                           (!b.Alias && b.DiscPath.Contains(text, StringComparison.OrdinalIgnoreCase)));   // its name on an older/imported disc

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
