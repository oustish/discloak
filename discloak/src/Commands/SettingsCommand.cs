namespace Discloak;

public sealed class SettingsCommand : ICommand
{
    public string Name => "settings";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Global settings (label prefix, currency, ...)";
    public string Usage => "settings [show] | settings set <key> <value> | settings set mirror <folder>|none";
    public bool NeedsFilesFor(Options opt) => false;   // settings only
    public bool ReadOnlyFor(Options opt) => opt.Args.Count == 0 || opt.Args[0].Equals("show", StringComparison.OrdinalIgnoreCase);
    public bool FixesMirror(Options opt) => opt.Args.Count > 1 && opt.Args[1].Equals("mirror", StringComparison.OrdinalIgnoreCase);
    public string Details => """
        'settings' (or 'settings show') lists every setting; 'settings set <key> <value>' changes one.

        Keys:
          label-prefix     new discs are named <prefix><counter>. The counter is shared by all prefixes, so changing the
                           prefix continues the numbering; existing labels are not renamed. It goes back only when 'discard'
                           takes the newest discs out: the next disc takes the number after the highest one still there
          currency         shown next to costs
          history-keep     how many older catalog versions are kept (default 10)
          default-threads  the default for 'scan --threads' (default 1)
          packing          how 'plan' and 'stage' lay files onto discs: largest (the default: largest file first, the fullest
                           discs) or sequential (in the order of the source folders; see 'plan --help')
          unlock-idle      e.g. 30m: an unlocked catalog locks again after this long without use (see 'unlock')
          unlock-max       e.g. 8h: ... and after this long at the latest
          lock-on-win-l    yes/no: lock it when Windows is locked
          lock-on-sleep    yes/no: lock it when the computer goes to sleep
          mirror           <folder>|none: a second copy of the catalog, kept in step by every save (ideally on another
                           drive). While it is set, nothing that changes the catalog runs unless the mirror is there and holds
                           exactly the same (read-only commands only warn); see 'catalog mirror'. An empty folder gets a copy
                           at once
        """;

    /// <summary>settings [show] | settings set &lt;key&gt; &lt;value&gt;</summary>
    public int Run(Ctx c)
    {
        string sub = c.Args.Count > 0 ? c.Args[0].ToLowerInvariant() : "show";
        if (sub == "show" && c.Args.Count <= 1)
        {
            foreach (string key in Discloak.Settings.Keys)
                Console.WriteLine($"{key,-16} {c.Settings.Get(key)}");
            Console.WriteLine($"{"label counter",-16} {c.Settings.LabelCounter}   (the last number given out; discarding the newest discs gives theirs back)");
            Console.WriteLine($"{"ignore rules",-16} {c.Settings.IgnoreRules.Count}   (see: ignore list)");
            var mirror = Mirror.Check(c.Cat.Dir, c.Keys);
            Console.WriteLine($"{"mirror",-16} " + (mirror.State == Mirror.State.None ? "(none)" : $"{mirror.Path}   ({(mirror.Ok ? "in step" : mirror.Message)})"));
            Console.WriteLine($"\nNext disc label would be: {c.Settings.LabelPrefix}{(c.Settings.LabelCounter + 1):D4}");
            return 0;
        }

        if (sub == "set" && c.Args.Count == 3)
        {
            string key = c.Args[1], value = c.Args[2];
            if (key.Equals("mirror", StringComparison.OrdinalIgnoreCase)) return SetMirror(c, value);
            string before = c.Settings.Get(key);
            c.Settings.Set(key, value);
            c.Cat.Save();
            Console.WriteLine($"{key}: '{before}' -> '{c.Settings.Get(key)}'");
            if (key.Equals("label-prefix", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"Existing discs keep their labels. The counter continues from {c.Settings.LabelCounter}: " +
                                  $"the next disc will be {value}{(c.Settings.LabelCounter + 1):D4}.");
            return 0;
        }

        throw new UserException("Usage: settings [show] | settings set <key> <value>   keys: " + string.Join(", ", Discloak.Settings.Keys) + ", mirror");
    }

    /// <summary>settings set mirror &lt;folder&gt;|none: an empty folder gets a copy at once; one holding this catalog is only checked.</summary>
    static int SetMirror(Ctx c, string value)
    {
        string dir = c.Cat.Dir;
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
        {
            string? was = Mirror.PathFor(dir);
            Mirror.Configure(dir, null);
            Console.WriteLine(was == null ? "No mirror was set." : $"The catalog is no longer mirrored to {was} (the files there stay as they are).");
            return 0;
        }
        if (c.Cat.Integrity.State == IntegrityState.Broken)
            throw new UserException("The catalog fails its check; fix that first ('catalog check').");

        Mirror.Configure(dir, value);
        string path = Mirror.PathFor(dir)!;
        var r = Mirror.Check(dir, c.Keys);
        if (r.Ok) { Console.WriteLine($"Mirror: {path} (already in step)."); return 0; }
        if (KeyFile.ReadFrom(path) == null)
        {
            Mirror.CopyCatalog(dir, path);
            Console.WriteLine($"Mirror: {path}. The catalog was copied there; every save keeps it in step from now on.");
            return 0;
        }
        Console.WriteLine($"Mirror: {path}. It already holds this catalog, but: {r.Message}.");
        Console.WriteLine("Bring it in step with 'catalog mirror sync' (catalog -> mirror) or 'catalog mirror restore' (mirror -> catalog).");
        return 2;
    }
}
