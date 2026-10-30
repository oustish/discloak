namespace Bdxl;

public sealed class DiscJsonCommand : ICommand
{
    public string Name => "discjson";
    public string Group => CommandGroup.ExistingDiscs;
    public string Summary => "Write disc.json onto a disc that can still be written to";
    public string Usage => "discjson <label> <mountPath> [--overwrite]";
    public string Details => """
        For a disc already in the catalog. Fails with an explanation if the disc is finalized or read-only.
        If the disc has no manifest, one is written too; for an encrypted disc it is encrypted (manifest.jsonl.enc)
        with the catalog's master key, and bdxl.key and README.txt are added if they are missing.
        """;

    /// <summary>discjson &lt;label&gt; &lt;mountPath&gt;: writes disc.json (and the manifest if missing) onto a disc that can still be written to.</summary>
    public int Run(Ctx c)
    {
        if (c.Args.Count != 2) throw new UserException("Usage: discjson <label> <mountPath> [--overwrite]");
        var disc = c.Arc.FindDisc(c.Args[0]);
        string mount = Path.GetFullPath(c.Args[1]);
        if (!Directory.Exists(mount)) throw new UserException($"Not a directory: {mount}");

        var onDisc = c.Arc.CopiesOn(disc.Label)
            .Select(x => (x.File, x.Copy, FullPath: Path.Combine(mount, x.Copy.DiscPath)))
            .Where(x => File.Exists(x.FullPath))
            .ToList();
        var manifest = onDisc.Select(x => ManifestLine.For(x.Copy, new FileInfo(x.FullPath).Length, x.File)).ToList();

        if (disc.Encrypted && disc.KeyId != null && !c.Keys.Contains(disc.KeyId))
            throw new UserException($"{disc.Label} is encrypted with a key this catalog does not have (key id {disc.KeyId}).");

        if (DiscFiles.TryWriteDiscJson(mount, disc, manifest, c.Opt.Has("overwrite"), c.Keys.For(disc.Encrypted ? disc.KeyId : null), c.Cat.Dir, out string why))
        {
            Console.WriteLine($"disc.json written for {disc.Label} ({manifest.Count:N0} file(s) found on the disc).");
            return 0;
        }
        Console.WriteLine($"Could not write disc.json: {why}.");
        return 3;
    }
}
