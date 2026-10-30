namespace Bdxl;

public sealed class CompressionCommand : ICommand
{
    public string Name => "compression";
    public string Group => CommandGroup.Indexing;
    public string Summary => "Compress files of a location on the discs (zstd, per location)";
    public string Usage => "compression [list] | compression set <alias> off|auto|on [--level N] | compression set --default off|auto|on [--level N] | compression clear <alias>";
    public string Details => $"""
        Staged copies can be compressed (zstd) inside their encryption: every 1 MiB piece of a file is compressed on its
        own and kept compressed only if that made it smaller, so nothing ever grows by more than a byte per MiB.
          off    never (the default)
          auto   every file except those whose extension says they are compressed already (photos, video, audio,
                 archives, office documents: {string.Join(" ", Compression.CompressedExtensions.Take(12))} ...)
          on     every file
          --level N   zstd level {Compression.MinLevel}-{Compression.MaxLevel} (default {Compression.DefaultLevel}): higher packs a little
                      tighter and is much slower, for 'scan' and 'stage' alike
        A location (alias) uses its own setting, or the default. After a change, run 'scan': it reads the affected
        files once more to count the exact size of their compressed copies, which 'plan' and 'stage' use to fill
        discs (until then they count the uncompressed size, so nothing ever overfills). Parts of split files are
        never compressed. Copies already on discs stay as they are; restoring needs no setting at all.
          compression list    the settings and, per location, how much the counted files shrink
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
                string alias = c.Arg(1, "compression clear <alias>");
                var key = c.Settings.CompressionByAlias.Keys.FirstOrDefault(k => k.Equals(alias, StringComparison.OrdinalIgnoreCase))
                          ?? throw new UserException($"'{alias}' has no setting of its own (it uses the default: {c.Settings.Compression}).");
                c.Settings.CompressionByAlias.Remove(key);
                c.Cat.Save();
                Console.WriteLine($"{key} uses the default again: {c.Settings.Compression}. Run 'scan' to count its sizes for it.");
                return 0;
            }
            default:
                throw new UserException("Usage: " + Usage.Replace(" | ", "\n       "));
        }
    }

    static int Set(Ctx c)
    {
        bool isDefault = c.Opt.Has("default");
        string mode = (isDefault ? c.Opt.Get("default") : c.Args.Count == 3 ? c.Args[2] : null)?.ToLowerInvariant()
                      ?? throw new UserException("Usage: compression set <alias> off|auto|on [--level N]   or   compression set --default off|auto|on");
        int level = c.Opt.GetInt("level", Compression.DefaultLevel);
        Compression.Validate(mode, level);
        var setting = new CompressionSetting { Mode = mode, Level = level };

        if (isDefault)
        {
            c.Settings.Compression = setting;
            Console.WriteLine($"Default compression: {setting}.");
        }
        else
        {
            string alias = c.Args[1];
            string? known = c.Cat.Files.Select(f => f.RootName).FirstOrDefault(n => n.Equals(alias, StringComparison.OrdinalIgnoreCase))
                            ?? throw new UserException($"No location with the alias '{alias}'. See:  alias list");
            var old = c.Settings.CompressionByAlias.Keys.FirstOrDefault(k => k.Equals(known, StringComparison.OrdinalIgnoreCase));
            if (old != null) c.Settings.CompressionByAlias.Remove(old);
            c.Settings.CompressionByAlias[known] = setting;
            Console.WriteLine($"{known}: compression {setting}.");
        }
        c.Cat.Save();
        int toCount = c.Cat.Files.Count(f => !f.Ignored && f.MissingSinceUtc == null && f.Sha256.Length > 0 && Compression.NeedsCounting(c.Settings, f));
        if (toCount > 0)
            Console.WriteLine($"Run 'scan' next: {toCount:N0} file(s) are read once more to count their compressed size " +
                              "(until then 'plan' and 'stage' count them uncompressed).");
        return 0;
    }

    static int List(Ctx c)
    {
        Console.WriteLine($"Default: {c.Settings.Compression}");
        var locations = c.Cat.Files.Where(f => !f.Root.StartsWith("imported:", StringComparison.Ordinal))
            .GroupBy(f => f.RootName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (locations.Count == 0) { Console.WriteLine("No locations scanned yet."); return 0; }
        Console.WriteLine();
        foreach (var g in locations)
        {
            bool own = c.Settings.CompressionByAlias.Keys.Any(k => k.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            var setting = Compression.For(c.Settings, g.Key);
            var live = g.Where(f => !f.Ignored && f.MissingSinceUtc == null && f.Sha256.Length > 0).ToList();
            var compressed = live.Where(f => Compression.LevelFor(c.Settings, f) > 0).ToList();
            var counted = compressed.Where(f => !Compression.NeedsCounting(c.Settings, f)).ToList();
            long plain = counted.Sum(f => FileCrypto.StoredSize(f.Size)), packed = counted.Sum(f => f.CompressedSize ?? 0);
            Console.WriteLine($"  {g.Key,-24} {setting}{(own ? "" : "  (default)")}");
            if (compressed.Count == 0) continue;
            Console.Write($"      {compressed.Count:N0} of {live.Count:N0} file(s) are compressed");
            if (counted.Count > 0)
                Console.Write($"; counted: {SizeText.Bytes(plain)} -> {SizeText.Bytes(packed)} bytes ({100.0 * packed / Math.Max(1, plain):F0}%)");
            if (counted.Count < compressed.Count) Console.Write($"; {compressed.Count - counted.Count:N0} not counted yet (run 'scan')");
            Console.WriteLine();
        }
        return 0;
    }
}
