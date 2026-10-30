namespace Bdxl;

public static class SizeText
{
    /// <summary>"96882130944", "25GB" (decimal), "90GiB" (binary), "500MB", "1.5TB" ...</summary>
    public static long Parse(string text)
    {
        string s = text.Trim().Replace("_", "").Replace(",", "");
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0 || !double.TryParse(s[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            throw new UserException($"Cannot parse size '{text}'.");
        double mult = s[i..].Trim().ToUpperInvariant() switch
        {
            "" or "B" => 1,
            "KB" => 1e3, "MB" => 1e6, "GB" => 1e9, "TB" => 1e12,
            "K" or "KIB" => 1024, "M" or "MIB" => 1048576, "G" or "GIB" => 1073741824, "T" or "TIB" => 1099511627776,
            _ => throw new UserException($"Unknown size unit in '{text}'.")
        };
        return (long)Math.Round(n * mult);
    }

    public static decimal ParseMoney(string text)
    {
        string s = text.Trim().Replace(",", ".");
        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) || value < 0)
            throw new UserException($"Cannot parse amount '{text}' (use e.g. 24.99).");
        return value;
    }

    public static string Bytes(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>text cut to max characters by replacing its middle with "…"; keeps more of the end (a path's file name).</summary>
    public static string ShortenMiddle(string text, int max)
    {
        if (text.Length <= max) return text;
        if (max < 5) return text[^Math.Max(1, max)..];
        int head = (max - 1) / 3, tail = max - 1 - head;
        return text[..head] + "…" + text[^tail..];
    }

    /// <summary>"90s", "30m", "8h", "2d", "1h30m".</summary>
    public static TimeSpan ParseDuration(string text)
    {
        var parts = Regex.Matches(text.Trim().ToLowerInvariant(), @"(\d+)\s*([smhd])");
        if (parts.Count == 0 || string.Concat(parts.Select(m => m.Value)).Replace(" ", "") != text.Trim().ToLowerInvariant().Replace(" ", ""))
            throw new UserException($"Cannot parse duration '{text}' (use e.g. 30m, 8h, 1h30m, 2d).");
        var total = TimeSpan.Zero;
        foreach (Match m in parts)
        {
            long n = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(n), "m" => TimeSpan.FromMinutes(n), "h" => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n)
            };
        }
        return total;
    }

    /// <summary>The short form ParseDuration reads: 30m, 8h, 1h30m, 2d.</summary>
    public static string Duration(TimeSpan t)
    {
        var sb = new StringBuilder();
        if (t.Days > 0) sb.Append(t.Days).Append('d');
        if (t.Hours > 0) sb.Append(t.Hours).Append('h');
        if (t.Minutes > 0) sb.Append(t.Minutes).Append('m');
        if (t.Seconds > 0 || sb.Length == 0) sb.Append(t.Seconds).Append('s');
        return sb.ToString();
    }

    public static string Money(decimal value, string currency) =>
        value.ToString("N2", CultureInfo.InvariantCulture) + (currency.Length > 0 ? " " + currency : "");
}
