namespace Bdxl;

/// <summary>A table for the console: every column as wide as its widest cell (header included), two spaces apart.</summary>
public static class TextTable
{
    /// <summary>The first `leftAligned` columns are aligned left (names), the others right (numbers, amounts).</summary>
    public static IEnumerable<string> Lines(string[] header, IReadOnlyList<string[]> rows, int leftAligned)
    {
        var widths = header.Select((h, i) => rows.Select(r => r[i].Length).Prepend(h.Length).Max()).ToArray();
        string Line(string[] cells) =>
            string.Join("  ", cells.Select((cell, i) => i < leftAligned ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]))).TrimEnd();
        return rows.Select(Line).Prepend(Line(header));
    }
}
