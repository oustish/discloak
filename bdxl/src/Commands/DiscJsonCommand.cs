namespace Bdxl;

public sealed class DiscJsonCommand : ICommand
{
    public string Name => "discjson";
    public string Group => CommandGroup.ExistingDiscs;
    public string Summary => "Write disc.json onto a disc that can still be written to";
    public string Usage => "discjson <label> <mountPath> [--overwrite]";
    public string Details => "For a disc already in the catalog. Fails with an explanation if the disc is finalized or read-only.";

    /// <summary>discjson &lt;label&gt; &lt;mountPath&gt;: writes disc.json (and manifest.jsonl if missing) onto a disc that can still be written to.</summary>
    public int Run(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: discjson <label> <mountPath> [--overwrite]");
        var disc = c.Arc.FindDisc(c.Args[0]);
        string mount = Path.GetFullPath(c.Args[1]);
        if (!Directory.Exists(mount)) throw new UserException($"Not a directory: {mount}");

        var manifest = c.Cat.Files.SelectMany(f => f.Backups.Where(b => b.Disc == disc.Label).Select(b => (File: f, Copy: b)))
            .GroupBy(x => x.Copy.DiscPath, StringComparer.Ordinal).Select(g => g.First())
            .Select(x => (x.File, x.Copy, FullPath: Path.Combine(mount, x.Copy.DiscPath)))
            .Where(x => File.Exists(x.FullPath))
            .Select(x => ManifestLine.For(x.Copy, new FileInfo(x.FullPath).Length, x.File.Size))
            .ToList();

        if (DiscFiles.TryWriteDiscJson(mount, disc, manifest, c.Opt.Has("overwrite"), out string why))
        {
            Console.WriteLine($"disc.json written for {disc.Label} ({manifest.Count:N0} file(s) found on the disc).");
            return 0;
        }
        Console.WriteLine($"Could not write disc.json: {why}.");
        return 3;
    }
}
