namespace Bdxl;

public static class Packer
{
    /// <summary>
    /// Best Fit Decreasing onto a FIXED set of blank discs of possibly different sizes: largest piece first, each into
    /// the disc that is left with the least free space. Pieces that fit nowhere are returned, never dropped.
    /// sizeOf = bytes the piece occupies on disc (the plain size, or the encrypted size).
    /// groupOf (optional): pieces of the same group never share a disc (copies of one content); holds (optional): the
    /// disc already holds that group (a burned disc getting another session), so no piece of the group goes there.
    /// </summary>
    public static (List<Bin> Bins, List<Piece> Unplaced) Allocate(
        IEnumerable<Piece> pieces, IReadOnlyList<BlankDisc> blanks, Func<Piece, long> sizeOf,
        Func<Piece, string?>? groupOf = null, Func<BlankDisc, string, bool>? holds = null)
    {
        var bins = blanks.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => new Bin(b)).ToList();
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
}
