namespace Bdxl;

public static class Packer
{
    /// <summary>
    /// Best Fit Decreasing onto a FIXED set of blank discs of possibly different sizes: largest piece first, each into
    /// the disc that is left with the least free space. Pieces that fit nowhere are returned, never dropped.
    /// sizeOf = bytes the piece occupies on disc (the plain size, or the encrypted size).
    /// </summary>
    public static (List<Bin> Bins, List<Piece> Unplaced) Allocate(
        IEnumerable<Piece> pieces, IReadOnlyList<BlankDisc> blanks, Func<Piece, long> sizeOf)
    {
        var bins = blanks.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => new Bin(b)).ToList();
        var byFree = new SortedSet<(long Free, int Index)>();
        for (int i = 0; i < bins.Count; i++) byFree.Add((bins[i].Free, i));
        var unplaced = new List<Piece>();

        var ordered = pieces.Select(p => (Piece: p, Size: sizeOf(p)))
            .OrderByDescending(x => x.Size)
            .ThenBy(x => x.Piece.Name, StringComparer.Ordinal);

        foreach (var item in ordered)
        {
            (long Free, int Index)? best = null;
            foreach (var candidate in byFree.GetViewBetween((item.Size, int.MinValue), (long.MaxValue, int.MaxValue)))
            {
                best = candidate;   // first element of the view = smallest free space that still fits
                break;
            }

            if (best is not { } chosen) { unplaced.Add(item.Piece); continue; }
            byFree.Remove(chosen);
            bins[chosen.Index].Add(item.Piece, item.Size);
            byFree.Add((bins[chosen.Index].Free, chosen.Index));
        }
        return (bins, unplaced);
    }
}
