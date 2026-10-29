namespace Bdxl;

public sealed class SettingsCommand : ICommand
{
    public string Name => "settings";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Global settings (label prefix, currency, ...)";
    public string Usage => "settings [show] | settings set <key> <value>";
    public string Details => """
        Keys: label-prefix, currency, history-keep, default-threads.
        label-prefix: new discs are named <prefix><counter>. The counter is global and never resets, so changing the
        prefix continues the numbering. Existing labels are not renamed.
        """;

    /// <summary>settings [show] | settings set &lt;key&gt; &lt;value&gt;</summary>
    public int Run(Ctx c)
    {
        string sub = c.Args.Count > 0 ? c.Args[0].ToLowerInvariant() : "show";
        if (sub == "show" && c.Args.Count <= 1)
        {
            foreach (string key in Bdxl.Settings.Keys)
                Console.WriteLine($"{key,-16} {c.Settings.Get(key)}");
            Console.WriteLine($"{"label counter",-16} {c.Settings.LabelCounter}   (last number used; never reset)");
            Console.WriteLine($"{"ignore rules",-16} {c.Settings.IgnoreRules.Count}   (see: ignore list)");
            Console.WriteLine($"\nNext disc label would be: {c.Settings.LabelPrefix}{(c.Settings.LabelCounter + 1):D4}");
            return 0;
        }

        if (sub == "set" && c.Args.Count == 3)
        {
            string key = c.Args[1], value = c.Args[2];
            string before = c.Settings.Get(key);
            c.Settings.Set(key, value);
            c.Cat.Save();
            Console.WriteLine($"{key}: '{before}' -> '{c.Settings.Get(key)}'");
            if (key.Equals("label-prefix", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"Existing discs keep their labels. The counter continues from {c.Settings.LabelCounter}: " +
                                  $"the next disc will be {value}{(c.Settings.LabelCounter + 1):D4}.");
            return 0;
        }

        throw new UserException("Usage: settings [show] | settings set <key> <value>   keys: " + string.Join(", ", Bdxl.Settings.Keys));
    }
}
