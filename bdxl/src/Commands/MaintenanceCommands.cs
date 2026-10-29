namespace Bdxl;

/// <summary>purge, alias, settings</summary>
public static class MaintenanceCommands
{
    // ---- purge ----------------------------------------------------------------------------

    /// <summary>
    /// purge [--dry-run] [--yes]
    /// Removes catalog entries for files that are no longer on their source AND were never put on any disc.
    /// (Entries that have copies on discs - even lost ones - are history and are kept.)
    /// </summary>
    public static int Purge(Ctx c)
    {
        var victims = c.Cat.Files.Where(f => f.MissingSinceUtc != null && f.Backups.Count == 0).ToList();
        if (victims.Count == 0) { Console.WriteLine("Nothing to purge: every file that is gone from its source has a copy on a disc."); return 0; }

        Console.WriteLine($"{victims.Count:N0} catalog entr{(victims.Count == 1 ? "y" : "ies")} refer to files that no longer exist and were never burned:");
        foreach (var f in victims.OrderBy(x => x.RootName, StringComparer.Ordinal).ThenBy(x => x.RelPath, StringComparer.Ordinal).Take(25))
            Console.WriteLine($"  {f.RootName}/{f.RelPath}  (gone since {f.MissingSinceUtc:yyyy-MM-dd})");
        if (victims.Count > 25) Console.WriteLine($"  ... and {victims.Count - 25:N0} more");

        if (c.Opt.Has("dry-run")) { Console.WriteLine("\nDry run - nothing removed."); return 0; }
        if (!Ui.Confirm(c.Opt, $"\nRemove these {victims.Count:N0} entries from the catalog? (the previous catalog version is kept in history)"))
        {
            Console.WriteLine("Cancelled.");
            return 1;
        }

        var set = victims.ToHashSet();
        c.Cat.Files.RemoveAll(f => set.Contains(f));
        c.Cat.Save();
        Console.WriteLine($"Removed {victims.Count:N0} entries.");
        return 0;
    }

    // ---- alias ----------------------------------------------------------------------------

    /// <summary>
    /// alias [list] | alias rename &lt;old&gt; &lt;new&gt;
    /// The alias is the name a scanned folder gets (scan --name). Renaming changes how the folder appears in the catalog
    /// and in future discs; paths already burned onto discs keep the old name (the catalog remembers where they are).
    /// </summary>
    public static int Alias(Ctx c)
    {
        string sub = c.Args.Count > 0 ? c.Args[0].ToLowerInvariant() : "list";
        if (sub == "list" && c.Args.Count <= 1)
        {
            var groups = c.Cat.Files.GroupBy(f => (f.RootName, f.Root)).OrderBy(g => g.Key.RootName, StringComparer.OrdinalIgnoreCase).ToList();
            if (groups.Count == 0) Console.WriteLine("No locations scanned yet.");
            foreach (var g in groups)
                Console.WriteLine($"{g.Key.RootName,-24} {g.Count(),9:N0} files   {g.Key.Root}");
            return 0;
        }

        if (sub == "rename" && c.Args.Count == 3)
        {
            string oldName = c.Args[1], newName = c.Args[2];
            if (newName.Length == 0 || newName.IndexOfAny(new[] { '/', '\\' }) >= 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new UserException("The new alias must be a plain name (no slashes or other characters that cannot be in a folder name).");

            var affected = c.Cat.Files.Where(f => f.RootName.Equals(oldName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (affected.Count == 0) throw new UserException($"No location with the alias '{oldName}'. See:  alias list");

            var roots = affected.Select(f => f.Root).Distinct().ToList();
            bool clash = c.Cat.Files.Any(f => f.RootName.Equals(newName, StringComparison.OrdinalIgnoreCase) && !roots.Contains(f.Root));
            if (clash) throw new UserException($"The alias '{newName}' is already used by another location.");

            foreach (var f in affected) f.RootName = newName;
            c.Cat.Save();
            Console.WriteLine($"Renamed '{oldName}' to '{newName}' on {affected.Count:N0} file(s). " +
                              "Discs that were already burned keep the old folder name on the disc; the catalog still finds the files there.");
            return 0;
        }

        throw new UserException("Usage: alias [list] | alias rename <old> <new>");
    }

    // ---- settings -------------------------------------------------------------------------

    /// <summary>settings [show] | settings set &lt;key&gt; &lt;value&gt;</summary>
    public static int Settings(Ctx c)
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
