namespace Discloak;

public sealed class CopiesCommand : ICommand
{
    public string Name => "copies";
    public string Group => CommandGroup.Indexing;
    public string Summary => "How many copies (on different discs) each location's files should have";
    public string Usage => "copies [list] | copies set <alias> N | copies set --default N | copies clear <alias>";
    public bool ReadOnlyFor(Options opt) => SubCommand.Is(opt, "list", "list");
    public string Details => $"""
        Every file is stored once by default. A location (alias) can ask for more: 'plan' and 'stage' then keep
        planning further copies of its files until each is on that many different discs - never two copies of one
        file on the same disc, so losing one disc never loses two copies. A file whose content also lives in another
        location gets the higher of the two numbers. Up to {Settings.MaxCopies}.
        Staged discs already count for planning (so the next 'stage' does not plan the same copy again), but a copy
        only counts as made when its disc is burned: 'status', 'copies list' and 'find' count burned discs only.
          copies list           the settings and, per location, how many files have fewer burned copies than wanted
          copies set <alias> N  this location wants N copies
          copies set --default N   every location without its own number
          copies clear <alias>  the location uses the default again
        Lowering the number removes nothing from discs; 'lost'/'destroyed' discs stop counting, and their files are
        planned again.
        """;

    public int Run(Ctx c)
    {
        string sub = c.Args.Count > 0 ? c.Args[0].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list": return List(c);
            case "set": return Set(c);
            case "clear":
            {
                string alias = c.Arg(1, "copies clear <alias>");
                var key = c.Settings.CopiesByAlias.Keys.FirstOrDefault(k => k.Equals(alias, StringComparison.OrdinalIgnoreCase))
                          ?? throw new UserException($"'{alias}' has no number of its own (it uses the default: {c.Settings.CopiesFor(alias)}).");
                c.Settings.CopiesByAlias.Remove(key);
                c.Cat.Save();
                Console.WriteLine($"{key} uses the default again: {c.Settings.CopiesFor(key)} cop{(c.Settings.CopiesFor(key) == 1 ? "y" : "ies")}.");
                return 0;
            }
            default:
                throw new UserException("Usage: " + Usage.Replace(" | ", "\n       "));
        }
    }

    static int Set(Ctx c)
    {
        bool isDefault = c.Opt.Has("default");
        string? text = isDefault ? c.Opt.Get("default") : c.Args.Count == 3 ? c.Args[2] : null;
        if (text == null || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > Settings.MaxCopies)
            throw new UserException($"Usage: copies set <alias> N   or   copies set --default N   (N = 1-{Settings.MaxCopies})");

        if (isDefault)
        {
            c.Settings.Copies = n;
            Console.WriteLine($"Default: {n} cop{(n == 1 ? "y" : "ies")} of every file.");
        }
        else
        {
            string alias = c.Args[1];
            string known = c.Cat.Files.Select(f => f.RootName).FirstOrDefault(r => r.Equals(alias, StringComparison.OrdinalIgnoreCase))
                           ?? throw new UserException($"No location with the alias '{alias}'. See:  alias list");
            var old = c.Settings.CopiesByAlias.Keys.FirstOrDefault(k => k.Equals(known, StringComparison.OrdinalIgnoreCase));
            if (old != null) c.Settings.CopiesByAlias.Remove(old);
            c.Settings.CopiesByAlias[known] = n;
            Console.WriteLine($"{known}: {n} cop{(n == 1 ? "y" : "ies")} of every file.");
        }
        c.Cat.Save();
        int fewer = c.Arc.TooFewCopies().Count;
        if (fewer > 0) Console.WriteLine($"{fewer:N0} file(s) have fewer burned copies than wanted now; 'plan' shows the discs for them.");
        return 0;
    }

    static int List(Ctx c)
    {
        Console.WriteLine($"Default: {c.Settings.Copies} cop{(c.Settings.Copies == 1 ? "y" : "ies")} of every file");
        var locations = c.Cat.Files.Where(f => !f.Root.StartsWith("imported:", StringComparison.Ordinal))
            .GroupBy(f => f.RootName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (locations.Count == 0) { Console.WriteLine("No locations scanned yet."); return 0; }
        Console.WriteLine();
        var fewer = c.Arc.TooFewCopies().ToHashSet();
        foreach (var g in locations)
        {
            bool own = c.Settings.CopiesByAlias.Keys.Any(k => k.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            int want = c.Settings.CopiesFor(g.Key);
            var live = g.Where(f => !f.Ignored && f.MissingSinceUtc == null && f.Sha256.Length > 0).ToList();
            int short_ = live.Count(fewer.Contains);
            Console.WriteLine($"  {g.Key,-24} {want} cop{(want == 1 ? "y " : "ies")}{(own ? "" : "  (default)")}   " +
                              (short_ == 0 ? $"all {live.Count:N0} file(s) have them" : $"{short_:N0} of {live.Count:N0} file(s) have fewer burned copies"));
        }
        return fewer.Count > 0 ? 2 : 0;
    }
}
