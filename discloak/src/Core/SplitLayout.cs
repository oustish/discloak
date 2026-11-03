namespace Discloak;

/// <summary>
/// How a file too large for one disc is cut into parts (stage --split). Every part except the last has the same
/// size; parts are stored as "&lt;name&gt;.001", "&lt;name&gt;.002" ... so that, even without discloak, the original is
/// simply the parts joined in order (Windows: copy /b a.001+a.002 a; Linux: cat a.0* &gt; a).
/// The part size is fixed when the first part is staged; later parts (or a restaged part) reuse it.
/// </summary>
public static class SplitLayout
{
    public static string Suffix(int part, int parts) =>
        "." + part.ToString(new string('0', Math.Max(3, parts.ToString(CultureInfo.InvariantCulture).Length)), CultureInfo.InvariantCulture);

    public static List<Piece> Pieces(FileRecord f, long partSize)
    {
        if (partSize <= 0) throw new ArgumentException("part size must be positive");
        int parts = (int)((f.Size + partSize - 1) / partSize);
        var list = new List<Piece>(parts);
        for (int i = 0; i < parts; i++)
        {
            long offset = i * partSize;
            list.Add(new Piece(f, i + 1, parts, offset, Math.Min(partSize, f.Size - offset)));
        }
        return list;
    }

    /// <summary>The layout's part size, read back from any one part (the last part is usually shorter).</summary>
    public static long PartSizeOf(BackupRef part) =>
        part.Part < part.Parts ? part.Length : part.Offset / (part.Part - 1);
}
