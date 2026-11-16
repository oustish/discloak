namespace Discloak;

/// <summary>Global settings, stored as settings.json inside the catalog folder (and covered by the catalog hash).</summary>
public class Settings
{
    public string LabelPrefix { get; set; } = "DISC-";    // new discs are named <prefix><counter>
    public int LabelCounter { get; set; }                 // last number handed out; shared by all prefixes; goes back only on discard (Archive.ReclaimLabels)
    public string Currency { get; set; } = "";            // shown next to costs, e.g. "PLN" or "$"
    public int HistoryKeep { get; set; } = 10;           // how many older catalog versions to keep
    public int DefaultThreads { get; set; } = 1;          // default for 'scan --threads'
    public List<string> IgnoreRules { get; set; } = new(); // .gitignore syntax, applied relative to every source folder
    public CompressionSetting Compression { get; set; } = new();   // default for every alias (see Compression)
    public Dictionary<string, CompressionSetting> CompressionByAlias { get; set; } = new();   // alias -> its own setting
    public string Packing { get; set; } = Discloak.Packing.Largest;   // how 'plan'/'stage' lay files onto discs (see Packing)
    public int Copies { get; set; } = 1;                  // copies on different discs wanted of every file (see 'copies') ...
    public Dictionary<string, int> CopiesByAlias { get; set; } = new();   // ... unless its location (alias) has its own number
    public const int MaxCopies = 9;
    [JsonIgnore] public bool AnyExtraCopies => Copies > 1 || CopiesByAlias.Values.Any(n => n > 1);

    public int CopiesFor(string alias) =>
        CopiesByAlias.FirstOrDefault(kv => kv.Key.Equals(alias, StringComparison.OrdinalIgnoreCase)).Value is int n and > 0 ? n : Math.Max(1, Copies);
    public int UnlockIdleMinutes { get; set; } = 30;      // 'unlock': locked after this long without use
    public int UnlockMaxMinutes { get; set; } = 480;      // 'unlock': locked after this long at the latest
    public bool LockOnSessionLock { get; set; } = true;   // 'unlock': locked when Windows is locked (Win+L)
    public bool LockOnSleep { get; set; } = true;         // 'unlock': locked when the computer goes to sleep

    public static readonly string[] Keys =
        { "label-prefix", "currency", "history-keep", "default-threads", "packing", "unlock-idle", "unlock-max", "lock-on-win-l", "lock-on-sleep" };

    public string Get(string key) => key.ToLowerInvariant() switch
    {
        "label-prefix" => LabelPrefix,
        "currency" => Currency,
        "history-keep" => HistoryKeep.ToString(CultureInfo.InvariantCulture),
        "default-threads" => DefaultThreads.ToString(CultureInfo.InvariantCulture),
        "packing" => Packing,
        "unlock-idle" => SizeText.Duration(TimeSpan.FromMinutes(UnlockIdleMinutes)),
        "unlock-max" => SizeText.Duration(TimeSpan.FromMinutes(UnlockMaxMinutes)),
        "lock-on-win-l" => LockOnSessionLock ? "yes" : "no",
        "lock-on-sleep" => LockOnSleep ? "yes" : "no",
        _ => throw new UserException($"Unknown setting '{key}'. Known: {string.Join(", ", Keys)}.")
    };

    public void Set(string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "label-prefix":
                if (value.Length == 0 || value.Length > 20 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new UserException("The label prefix must be 1-20 characters and usable in a folder name.");
                LabelPrefix = value;
                break;
            case "currency":
                Currency = value;
                break;
            case "history-keep":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keep) || keep < 1 || keep > 1000)
                    throw new UserException("history-keep must be a number between 1 and 1000.");
                HistoryKeep = keep;
                break;
            case "default-threads":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int threads) || threads < 1 || threads > 64)
                    throw new UserException("default-threads must be a number between 1 and 64.");
                DefaultThreads = threads;
                break;
            case "packing":
                Packing = Discloak.Packing.Parse(value);
                break;
            case "unlock-idle":
                UnlockIdleMinutes = Minutes(value, "unlock-idle");
                if (UnlockIdleMinutes > UnlockMaxMinutes) throw new UserException("unlock-idle cannot be longer than unlock-max.");
                break;
            case "unlock-max":
                UnlockMaxMinutes = Minutes(value, "unlock-max");
                if (UnlockIdleMinutes > UnlockMaxMinutes) throw new UserException("unlock-max cannot be shorter than unlock-idle.");
                break;
            case "lock-on-win-l":
                LockOnSessionLock = YesNo(value, "lock-on-win-l");
                break;
            case "lock-on-sleep":
                LockOnSleep = YesNo(value, "lock-on-sleep");
                break;
            default:
                throw new UserException($"Unknown setting '{key}'. Known: {string.Join(", ", Keys)}.");
        }
    }

    static int Minutes(string value, string key)
    {
        var t = SizeText.ParseDuration(value);
        if (t < TimeSpan.FromMinutes(1) || t > TimeSpan.FromDays(7)) throw new UserException($"{key} must be between 1m and 7d.");
        return (int)t.TotalMinutes;
    }

    static bool YesNo(string value, string key) => value.ToLowerInvariant() switch
    {
        "yes" or "true" or "on" => true,
        "no" or "false" or "off" => false,
        _ => throw new UserException($"{key} is yes or no.")
    };
}
