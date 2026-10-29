namespace Bdxl;

/// <summary>Pending = everything still to be stored (parts of a split file are separate pieces). TooBig = whole files
/// that fit on no blank disc and were not split (no --split).</summary>
public record PlanResult(List<Bin> Bins, List<Piece> Unplaced, List<FileRecord> TooBig, List<Piece> Pending)
{
    public int PendingFiles => Pending.Select(p => p.File).Distinct().Count();
    public long PendingBytes => Pending.Sum(p => p.Length);
}
