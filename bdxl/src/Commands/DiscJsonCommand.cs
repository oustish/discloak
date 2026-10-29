namespace Bdxl;

public sealed class DiscJsonCommand : ICommand
{
    public string Name => "discjson";
    public string Group => CommandGroup.ExistingDiscs;
    public string Summary => "Write disc.json onto a disc that can still be written to";
    public string Usage => "discjson <label> <mountPath> [--overwrite]";
    public string Details => """
        For a disc already in the catalog. Fails with an explanation if the disc is finalized or read-only.
        If the disc has no manifest, one is written too; for an encrypted disc it is encrypted (manifest.jsonl.enc),
        so the password is needed (BDXL_PASSWORD, --password-env or typed); it is checked against the disc's password.
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

        KeyCache? keys = null;
        bool hadKeyCheck = disc.KeyCheck != null;
        if (disc.Encrypted && manifest.Count > 0 && !DiscFiles.HasManifest(mount))   // the disc's own password, checked against its files
            keys = DiscPassword.Get(c, disc, onDisc.Where(x => x.Copy.Encrypted).Select(x => x.FullPath), allowUnchecked: true);

        if (DiscFiles.TryWriteDiscJson(mount, disc, manifest, c.Opt.Has("overwrite"), keys, keys != null ? FileCrypto.NewSalt() : null, out string why))
        {
            Console.WriteLine($"disc.json written for {disc.Label} ({manifest.Count:N0} file(s) found on the disc).");
            if (!hadKeyCheck && disc.KeyCheck != null) c.Cat.Save();   // the password can be recognised from now on
            return 0;
        }
        Console.WriteLine($"Could not write disc.json: {why}.");
        return 3;
    }
}
