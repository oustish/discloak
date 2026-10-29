namespace Bdxl;

/// <summary>Global settings, stored as settings.json inside the catalog folder (and covered by the catalog hash).</summary>
public class Settings
{
    public string LabelPrefix { get; set; } = "BDXL-";    // new discs are named <prefix><counter>
    public int LabelCounter { get; set; }                 // last number handed out; NEVER reset when the prefix changes
    public string Currency { get; set; } = "";            // shown next to costs, e.g. "PLN" or "$"
    public int HistoryKeep { get; set; } = 10;           // how many older catalog versions to keep
    public int DefaultThreads { get; set; } = 1;          // default for 'scan --threads'
    public List<string> IgnoreRules { get; set; } = new(); // .gitignore syntax, applied relative to every source folder

    public static readonly string[] Keys = { "label-prefix", "currency", "history-keep", "default-threads" };

    public string Get(string key) => key.ToLowerInvariant() switch
    {
        "label-prefix" => LabelPrefix,
        "currency" => Currency,
        "history-keep" => HistoryKeep.ToString(CultureInfo.InvariantCulture),
        "default-threads" => DefaultThreads.ToString(CultureInfo.InvariantCulture),
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
            default:
                throw new UserException($"Unknown setting '{key}'. Known: {string.Join(", ", Keys)}.");
        }
    }
}
