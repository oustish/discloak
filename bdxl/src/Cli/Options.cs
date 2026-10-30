namespace Bdxl;

/// <summary>Command line: bdxl &lt;command&gt; [args...] [--option value] [--flag]</summary>
public sealed class Options
{
    /// <summary>Options that never take a value (so the next word stays a normal argument).</summary>
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "flat", "overwrite", "repair", "force", "allow-partial", "with-catalog", "update",
        "dry-run", "write-disc-json", "encrypt", "deep", "yes", "full", "all", "split", "recovery", "recovery-key", "status"
    };

    readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }
    public List<string> Args { get; } = new();

    public static Options Parse(string[] argv)
    {
        var o = new Options();
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            if (a == "-h" || a == "/?" || a == "-?") { o.values["help"] = "true"; continue; }

            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                string key = a[2..];
                int eq = key.IndexOf('=');
                if (eq > 0) { o.values[key[..eq]] = key[(eq + 1)..]; continue; }
                if (Flags.Contains(key)) { o.values[key] = "true"; continue; }
                bool hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal);
                o.values[key] = hasValue ? argv[++i] : "true";
                continue;
            }

            if (o.Command == null) o.Command = a;
            else o.Args.Add(a);
        }
        return o;
    }

    public bool Has(string name) => values.ContainsKey(name);
    public string? Get(string name) => values.TryGetValue(name, out var v) ? v : null;
    public string GetOr(string name, string fallback) => Get(name) ?? fallback;

    public int GetInt(string name, int fallback)
    {
        string? v = Get(name);
        if (v == null) return fallback;
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            throw new UserException($"--{name} needs a number (got '{v}').");
        return n;
    }

    public long GetSize(string name, long fallback)
    {
        string? v = Get(name);
        return v == null ? fallback : SizeText.Parse(v);
    }

    public DateTime? GetDate(string name)
    {
        string? v = Get(name);
        if (v == null) return null;
        // a typed date is a calendar day: keep it as that day in UTC (dates are shown as UTC), not local midnight shifted to the day before
        if (!DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            throw new UserException($"Cannot parse date '{v}' (use yyyy-MM-dd).");
        return d;
    }

    public decimal? GetMoney(string name)
    {
        string? v = Get(name);
        return v == null ? null : SizeText.ParseMoney(v);
    }
}
