namespace Bdxl;

/// <summary>find and status</summary>
public static class InfoCommands
{
    public static int Find(Ctx c)
    {
        if (c.Args.Count != 1) throw new UserException("Usage: find <text|hashPrefix|*>");
        string text = c.Args[0];
        var hits = c.Cat.Files.Where(f => Archive.Matches(f, text))
            .OrderBy(f => f.RootName, StringComparer.Ordinal).ThenBy(f => f.RelPath, StringComparer.Ordinal)
            .ToList();

        foreach (var f in hits)
        {
            string hashText = f.Sha256.Length >= 16 ? f.Sha256[..16] + "..." : "(not hashed)";
            Console.WriteLine($"{f.RootName}/{f.RelPath}  {SizeText.Bytes(f.Size)} bytes  sha256 {hashText}  " +
                              (f.Ignored ? "[ignored] " : "") +
                              (f.MissingSinceUtc != null ? $"[gone from source since {f.MissingSinceUtc:yyyy-MM-dd}]" : ""));
            if (f.Backups.Count == 0) Console.WriteLine("    not backed up");
            foreach (var b in f.Backups)
            {
                var d = c.Cat.Discs.FirstOrDefault(x => x.Label == b.Disc);
                string when = b.BackedUpUtc is { } t ? t.ToString("yyyy-MM-dd") : "not burned yet";
                string ver = Hashing.SameHash(b.Sha256, f.Sha256) ? "current version" : "OLDER version";
                string via = b.Alias ? $" - same content stored as {b.DiscPath}" : "";
                string enc = (b.IsPart ? $" [part {b.Part}/{b.Parts}]" : "") + (b.Encrypted ? " [encrypted]" : "");
                string unv = b.Unverified ? " [taken from manifest/catalog, not re-hashed - run 'verify']" : "";
                Console.WriteLine($"    {b.Disc} ({d?.State ?? "?"}, {when}) {ver}{via}{enc}{unv}");
            }
        }
        Console.WriteLine($"\n{hits.Count} match(es).");
        return hits.Count > 0 ? 0 : 1;
    }

    public static int Status(Ctx c)
    {
        Console.WriteLine($"Catalog: {c.Cat.Dir}  (integrity: {c.Cat.Integrity.State})");
        foreach (var g in c.Cat.Files.GroupBy(f => f.RootName))
        {
            int ignored = g.Count(f => f.Ignored);
            Console.WriteLine($"  source '{g.Key}': {g.Count():N0} files, {SizeText.Bytes(g.Sum(f => f.Size))} bytes" +
                              (ignored > 0 ? $" ({ignored:N0} ignored)" : ""));
        }

        var dead = c.Arc.DeadDiscs();
        int onSource = c.Cat.Files.Count(f => f.MissingSinceUtc == null && !f.Ignored);
        int safe = c.Cat.Files.Count(f => f.MissingSinceUtc == null && !f.Ignored &&
            Archive.CompleteCopy(f.Backups.Where(b => !dead.Contains(b.Disc) && b.BackedUpUtc != null), f.Sha256) != null);
        var sourceHashes = c.Arc.SourceHashes();
        int dataLost = c.Cat.Files.Count(f => f.MissingSinceUtc != null && f.Backups.Count > 0 && !Archive.HasAnyLiveCopy(f, dead)
                                              && !sourceHashes.Contains(f.Sha256));
        var plan = c.Arc.MakePlan(encrypt: false);

        Console.WriteLine($"Files still on source (not ignored): {onSource:N0}; current version burned: {safe:N0}");
        Console.WriteLine($"Pending: {plan.PendingFiles:N0} files, {SizeText.Bytes(plan.PendingBytes)} bytes");
        Console.WriteLine($"Blank discs registered: {c.Cat.Blanks.Count}" +
                          (plan.Pending.Count == 0 ? "" :
                           plan.Unplaced.Count == 0 ? " - all pending data fits." :
                           $" - {plan.Unplaced.Count:N0} pending file(s) do NOT fit (run 'plan')."));
        Console.WriteLine($"Discs: {c.Cat.Discs.Count(d => d.State == "burned")} burned, {c.Cat.Discs.Count(d => d.State == "staged")} staged (not yet burned), " +
                          $"{c.Cat.Discs.Count(d => d.State == "lost")} lost, {c.Cat.Discs.Count(d => d.State == "destroyed")} destroyed");
        Console.WriteLine($"Label prefix: {c.Settings.LabelPrefix}  (last number used: {c.Settings.LabelCounter})");
        if (c.Settings.IgnoreRules.Count > 0) Console.WriteLine($"Ignore rules: {c.Settings.IgnoreRules.Count}");
        if (dataLost > 0) Console.WriteLine($"WARNING: {dataLost:N0} file(s) are gone from the source and have no surviving copy on any disc.");
        Archive.PrintTooBig(plan.TooBig);
        return 0;
    }
}
