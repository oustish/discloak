namespace Bdxl;

public sealed class PurgeCommand : ICommand
{
    public string Name => "purge";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Remove entries of files that are gone and were never burned";
    public string Usage => "purge [--dry-run] [--yes]";
    public string Details => "Entries that have a copy on any disc are kept. The previous catalog version stays in history.";

    /// <summary>
    /// purge [--dry-run] [--yes]
    /// Removes catalog entries for files that are no longer on their source AND were never put on any disc.
    /// (Entries that have copies on discs - even lost ones - are history and are kept.)
    /// </summary>
    public int Run(Ctx c)
    {
        var victims = c.Cat.Files.Where(f => f.MissingSinceUtc != null && f.Backups.Count == 0).ToList();
        if (victims.Count == 0)
        {
            Console.WriteLine(c.Cat.Files.Any(f => f.MissingSinceUtc != null)
                ? "Nothing to purge: every file that is gone from its source is on a disc (or staged for one)."
                : "Nothing to purge: no file is marked as gone from its source. Only a scan that runs to the end marks " +
                  "files as gone (one stopped with Ctrl+C marks nothing).");
            return 0;
        }

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
}
