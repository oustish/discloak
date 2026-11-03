using System.Diagnostics;

namespace Bdxl;

/// <summary>
/// Progress of a long job by bytes (verify, stage, restore, restage): the status line, redrawn at most once a second,
/// and the rate ("12.4%   85.3 MB/s  ~3h 12m left") for the lines that stay. Done = bytes of the finished items;
/// `current` = how far the item in hand is.
/// </summary>
public sealed class ProgressMeter(long total)
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(1);
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly StatusLine status = new();
    TimeSpan shown;

    public long Total { get; set; } = total;
    public long Done { get; set; }
    public TimeSpan Elapsed => clock.Elapsed;

    public string Rate(long current = 0) => StatusLine.Rate(Done + current, Total, clock.Elapsed);

    /// <summary>"  [head  rate] what" in the status line, what shortened in the middle to fit.</summary>
    public void Show(string head, string what, long current = 0)
    {
        if (clock.Elapsed - shown < Every) return;
        shown = clock.Elapsed;
        string start = $"  [{head}  {Rate(current)}] ";
        status.Show(start + SizeText.ShortenMiddle(what, Math.Max(10, StatusLine.WindowWidth() - 1 - start.Length)));
    }

    /// <summary>A line that stays: the status line makes way, and comes back a second later at the earliest.</summary>
    public void WriteLine(string text)
    {
        status.Clear();
        Console.WriteLine(text);
        shown = clock.Elapsed;
    }

    public void Clear() => status.Clear();
}

/// <summary>
/// One line at the bottom of the console, rewritten in place (the lines printed above it stay). Nothing is written
/// when the output goes to a file or pipe.
/// </summary>
public sealed class StatusLine
{
    readonly bool live = !Console.IsOutputRedirected;
    int width;

    public void Show(string text)
    {
        if (!live) return;
        int max = Math.Max(20, WindowWidth() - 1);   // a wrapped line could not be overwritten
        if (text.Length > max) text = text[..(max - 3)] + "...";
        Console.Write("\r" + text.PadRight(width));
        width = Math.Max(width, text.Length);
    }

    public void Clear()
    {
        if (width == 0) return;
        Console.Write("\r" + new string(' ', width) + "\r");
        width = 0;
    }

    public static int WindowWidth()
    {
        try { return Console.WindowWidth > 0 ? Console.WindowWidth : 120; }
        catch (IOException) { return 120; }
    }

    /// <summary>
    /// "12.4%   85.3 MB/s  ~3h 12m left" - by bytes, for `read` of `total` bytes in `elapsed`; fixed widths, so it does
    /// not move from line to line ("100.0%", speeds up to 9999.9 MB/s). The time left is shown after 5 seconds.
    /// </summary>
    public static string Rate(long read, long total, TimeSpan elapsed)
    {
        double perSecond = elapsed.TotalSeconds > 0 ? read / elapsed.TotalSeconds : 0;
        string text = $"{Percent(read, total),6}  {(perSecond / 1e6).ToString("F1", CultureInfo.InvariantCulture),6} MB/s";
        if (perSecond > 0 && read < total && elapsed > TimeSpan.FromSeconds(5))
            text += $"  ~{Duration(TimeSpan.FromSeconds((total - read) / perSecond))} left";
        return text;
    }

    /// <summary>"1,234/50,000" with the first number padded to the width of the second.</summary>
    public static string Count(long done, long count)
    {
        string countText = count.ToString("N0", CultureInfo.InvariantCulture);
        return $"{done.ToString("N0", CultureInfo.InvariantCulture).PadLeft(countText.Length)}/{countText}";
    }

    public static string Percent(long part, long whole) =>
        (whole > 0 ? 100.0 * part / whole : 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";

    /// <summary>"3h 12m", "4m 05s", "12s".</summary>
    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:D2}m" :
        d.TotalMinutes >= 1 ? $"{d.Minutes}m {d.Seconds:D2}s" : $"{d.Seconds}s";
}
