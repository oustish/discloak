namespace Bdxl;

public sealed class SettingsCommand : ICommand
{
    public string Name => "settings";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Global settings (label prefix, currency, ...)";
    public string Usage => "settings [show] | settings set <key> <value> | settings set mirror <folder>|none";
    public bool ReadOnlyFor(Options opt) => opt.Args.Count == 0 || opt.Args[0].Equals("show", StringComparison.OrdinalIgnoreCase);
    public bool FixesMirror(Options opt) => opt.Args.Count > 1 && opt.Args[1].Equals("mirror", StringComparison.OrdinalIgnoreCase);
    public string Details => """
        Keys: label-prefix, currency, history-keep, default-threads, unlock-idle, unlock-max, lock-on-win-l, lock-on-sleep, mirror.
        unlock-idle / unlock-max (e.g. 30m, 8h): when an unlocked catalog locks again; lock-on-win-l / lock-on-sleep
        (yes/no): lock it when Windows is locked or goes to sleep (see 'unlock').
        label-prefix: new discs are named <prefix><counter>. The counter is global and never resets, so changing the
        prefix continues the numbering. Existing labels are not renamed.
        mirror <folder>|none: a second copy of the catalog, kept in step by every save (ideally on another drive). While it
        is set, nothing that changes the catalog runs unless the mirror is there and holds exactly the same (read-only
        commands only warn); see 'catalog mirror'. An empty folder gets a copy at once.
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

        throw new UserException("Usage: settings [show] | settings set <key> <value>   keys: " + string.Join(", ", Bdxl.Settings.Keys) + ", mirror");
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
