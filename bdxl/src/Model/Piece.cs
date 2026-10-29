namespace Bdxl;

/// <summary>One file to store on a disc: a whole file, or one part of a file split across discs.</summary>
public record Piece(FileRecord File, int Part, int Parts, long Offset, long Length)
{
    public static Piece Whole(FileRecord f) => new(f, 0, 0, 0, f.Size);
    public bool IsPart => Parts > 0;
    public string Name => $"{File.RootName}/{File.RelPath}" + (IsPart ? SplitLayout.Suffix(Part, Parts) : "");
}
