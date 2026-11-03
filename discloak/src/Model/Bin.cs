namespace Discloak;

/// <summary>What one blank disc would receive. Usable = the room for stored data (see DiscFormat.Room).</summary>
public class Bin
{
    public Bin(BlankDisc blank, long usable) { Blank = blank; Usable = usable; }
    public BlankDisc Blank { get; }
    public long Usable { get; }
    public List<Piece> Pieces { get; } = new();
    public long Used { get; private set; }
    public long Free => Usable - Used;
    public void Add(Piece p, long storedSize) { Pieces.Add(p); Used += storedSize; }
}
