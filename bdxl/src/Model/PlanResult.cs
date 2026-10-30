namespace Bdxl;

/// <summary>Pending = everything still to be stored (parts of a split file are separate pieces; a file that needs more
/// copies has one piece per copy still to make). TooBig = whole files that fit on no blank disc and were not split (no
/// --split). FurtherCopies = pending pieces of content that is already on at least one disc.</summary>
public record PlanResult(List<Bin> Bins, List<Piece> Unplaced, List<FileRecord> TooBig, List<Piece> Pending, int FurtherCopies = 0)
{
    public int PendingFiles => Pending.Select(p => p.File).Distinct().Count();
    public long PendingBytes => Pending.Sum(p => p.Length);
    public string CopiesNote => FurtherCopies > 0 ? $" (incl. {FurtherCopies:N0} further cop{(FurtherCopies == 1 ? "y" : "ies")} of files already on a disc)" : "";
}
