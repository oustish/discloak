namespace Bdxl;

public sealed class AliasCommand : ICommand
{
    public string Name => "alias";
    public string Group => CommandGroup.Catalog;
    public string Summary => "List or rename the names of scanned locations";
    public string Usage => "alias [list] | alias rename <old> <new>";
    public bool ReadOnlyFor(Options opt) => SubCommand.Is(opt, "list", "list");
    public string Details => "The alias is what 'scan --name' gave a folder. Renaming affects the catalog and future discs; discs already burned keep the old name on the disc.";

    /// <summary>
    /// alias [list] | alias rename &lt;old&gt; &lt;new&gt;
    /// The alias is the name a scanned folder gets (scan --name). Renaming changes how the folder appears in the catalog
    /// and in future discs; paths already burned onto discs keep the old name (the catalog remembers where they are).
    /// </summary>
    public int Run(Ctx c)
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
            var ownSetting = c.Settings.CompressionByAlias.Keys.FirstOrDefault(k => k.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (ownSetting != null)   // the compression setting goes with the name
            {
                c.Settings.CompressionByAlias[newName] = c.Settings.CompressionByAlias[ownSetting];
                c.Settings.CompressionByAlias.Remove(ownSetting);
            }
            var ownCopies = c.Settings.CopiesByAlias.Keys.FirstOrDefault(k => k.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (ownCopies != null)    // ... and so does the number of copies
            {
                c.Settings.CopiesByAlias[newName] = c.Settings.CopiesByAlias[ownCopies];
                c.Settings.CopiesByAlias.Remove(ownCopies);
            }
            c.Cat.Save();
            Console.WriteLine($"Renamed '{oldName}' to '{newName}' on {affected.Count:N0} file(s). " +
                              "Discs that were already burned keep the old folder name on the disc; the catalog still finds the files there.");
            return 0;
        }

        throw new UserException("Usage: alias [list] | alias rename <old> <new>");
    }
}
