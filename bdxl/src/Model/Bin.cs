namespace Bdxl;

/// <summary>What one blank disc would receive.</summary>
public class Bin
{
    public Bin(BlankDisc blank) { Blank = blank; }
    public BlankDisc Blank { get; }
    public long Usable => Blank.Usable;
    public List<Piece> Pieces { get; } = new();
    public long Used { get; private set; }
    public long Free => Usable - Used;
    public void Add(Piece p, long storedSize) { Pieces.Add(p); Used += storedSize; }
}
