namespace Discloak;

public static class Packer
{
    /// <summary>
    /// Best Fit Decreasing onto a FIXED set of blank discs of possibly different sizes: largest piece first, each into
    /// the disc that is left with the least free space. Pieces that fit nowhere are returned, never dropped.
    /// sizeOf = room the piece takes (its stored size and its manifest line); roomOf = room a disc has (default: its usable bytes).
    /// groupOf (optional): pieces of the same group never share a disc (copies of one content); holds (optional): the
    /// disc already holds that group (a burned disc getting another session), so no piece of the group goes there.
    /// </summary>
    public static (List<Bin> Bins, List<Piece> Unplaced) Allocate(
        IEnumerable<Piece> pieces, IReadOnlyList<BlankDisc> blanks, Func<Piece, long> sizeOf,
        Func<Piece, string?>? groupOf = null, Func<BlankDisc, string, bool>? holds = null, Func<BlankDisc, long>? roomOf = null)
    {
        var bins = blanks.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => new Bin(b, roomOf?.Invoke(b) ?? b.Usable)).ToList();
        var byFree = new SortedSet<(long Free, int Index)>();
        for (int i = 0; i < bins.Count; i++) byFree.Add((bins[i].Free, i));
        var groups = bins.Select(_ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToList();
        var unplaced = new List<Piece>();

        var ordered = pieces.Select(p => (Piece: p, Size: sizeOf(p)))
            .OrderByDescending(x => x.Size)
            .ThenBy(x => x.Piece.Name, StringComparer.Ordinal);

        foreach (var item in ordered)
        {
            string? group = groupOf?.Invoke(item.Piece);
            (long Free, int Index)? best = null;
            // in order of free space: the first disc that fits (and does not hold the piece's group yet)
            foreach (var candidate in byFree.GetViewBetween((item.Size, int.MinValue), (long.MaxValue, int.MaxValue)))
            {
                if (group != null && (groups[candidate.Index].Contains(group) || holds?.Invoke(bins[candidate.Index].Blank, group) == true))
                    continue;
                best = candidate;
                break;
            }

            if (best is not { } chosen) { unplaced.Add(item.Piece); continue; }
            byFree.Remove(chosen);
            bins[chosen.Index].Add(item.Piece, item.Size);
            if (group != null) groups[chosen.Index].Add(group);
            byFree.Add((bins[chosen.Index].Free, chosen.Index));
        }
        return (bins, unplaced);
    }

    /// <summary>
    /// Sequential packing (setting 'packing sequential'): the pieces in the order given, disc after disc (the blank
    /// discs in Allocate's order): each piece goes onto the disc being filled; one that does not fit there starts the
    /// next disc it fits on, and nothing goes back to fill the space left behind. A piece that fits on no disc left is
    /// returned (and the rest go on). Each run (the first copies, the second copies, ...) starts on a disc no earlier
    /// run used, so copies of one file never share a disc; groupOf/holds are kept as in Allocate as well.
    /// </summary>
    public static (List<Bin> Bins, List<Piece> Unplaced) Sequential(
        IEnumerable<IEnumerable<Piece>> runs, IReadOnlyList<BlankDisc> blanks, Func<Piece, long> sizeOf,
        Func<Piece, string?>? groupOf = null, Func<BlankDisc, string, bool>? holds = null, Func<BlankDisc, long>? roomOf = null)
    {
        var bins = blanks.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => new Bin(b, roomOf?.Invoke(b) ?? b.Usable)).ToList();
        var groups = bins.Select(_ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToList();
        var unplaced = new List<Piece>();
        int fresh = 0;   // the first disc no run has used yet
        foreach (var run in runs)
        {
            int current = fresh;
            foreach (var piece in run)
            {
                long size = sizeOf(piece);
                int target = current;
                while (target < bins.Count && bins[target].Free < size) target++;
                string? group = groupOf?.Invoke(piece);
                if (target == bins.Count ||
                    group != null && (groups[target].Contains(group) || holds?.Invoke(bins[target].Blank, group) == true))
                {
                    unplaced.Add(piece);
                    continue;
                }
                current = target;
                bins[current].Add(piece, size);
                if (group != null) groups[current].Add(group);
                fresh = Math.Max(fresh, current + 1);
            }
        }
        return (bins, unplaced);
    }
}
