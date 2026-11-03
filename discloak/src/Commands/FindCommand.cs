namespace Discloak;

public sealed class FindCommand : ICommand
{
    public string Name => "find";
    public string Group => CommandGroup.LookingUp;
    public string Summary => "Where is this file backed up?";
    public string Usage => "find <text|hashPrefix|*>";
    public bool ReadOnlyFor(Options opt) => true;
    public string Details => "Matches part of a path, or the start of a SHA-256. Shows every disc holding the file, when, and whether it is the current version.";

    public int Run(Ctx c)
    {
        if (c.Args.Count != 1) throw new UserException("Usage: find <text|hashPrefix|*>");
        string text = c.Args[0];
        var hits = c.Cat.Files.Where(f => Archive.Matches(f, text))
            .OrderBy(f => f.RootName, StringComparer.Ordinal).ThenBy(f => f.RelPath, StringComparer.Ordinal)
            .ToList();

        var dead = c.Arc.DeadDiscs();
        foreach (var f in hits)
        {
            string hashText = f.Sha256.Length >= 16 ? f.Sha256[..16] + "..." : "(not hashed)";
            Console.WriteLine($"{f.RootName}/{f.RelPath}  {SizeText.Bytes(f.Size)} bytes  sha256 {hashText}  " +
                              (f.Ignored ? "[ignored] " : "") +
                              (f.MissingSinceUtc != null ? $"[gone from source since {f.MissingSinceUtc:yyyy-MM-dd}]" : ""));
            if (f.Backups.Count == 0) Console.WriteLine("    not backed up");
            int want = c.Settings.CopiesFor(f.RootName);
            if (want > 1 && f.Sha256.Length > 0)
                Console.WriteLine($"    copies: {Archive.BurnedCopies(f, dead)} of {want} wanted on burned discs");
            foreach (var b in f.Backups)
            {
                var d = c.Cat.Discs.FirstOrDefault(x => x.Label == b.Disc);
                string when = b.BackedUpUtc is { } t ? t.ToString("yyyy-MM-dd") : "not burned yet";
                string ver = Hashing.SameHash(b.Sha256, f.Sha256) ? "current version" : "OLDER version";
                string via = b.Alias ? " - the copy stored for an identical file" : "";
                string part = b.IsPart ? $" [part {b.Part}/{b.Parts}]" : "";
                string unv = b.Unverified ? " [read from the disc's manifest, not checked yet - run 'verify']" : "";
                Console.WriteLine($"    {b.Disc} ({d?.State ?? "?"}, {when}) {ver}{via}{part}{unv}");
            }
        }
        Console.WriteLine($"\n{hits.Count} match(es).");
        return hits.Count > 0 ? 0 : 1;
    }
}
