namespace Bdxl;

public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Group => CommandGroup.LookingUp;
    public string Summary => "Summary of sources, pending data, blank discs and discs";
    public string Usage => "status";
    public string Details => "Also warns about files that exist nowhere any more.";

    public int Run(Ctx c)
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
                          $"{c.Cat.Discs.Count(d => d.State == "lost")} lost, {c.Cat.Discs.Count(d => d.State == "destroyed")} destroyed" +
                          (c.Cat.Discs.Count(d => d.PendingAppend != null) is > 0 and int appending
                              ? $"; {appending} appended session(s) staged, not yet burned" : ""));
        Console.WriteLine($"Label prefix: {c.Settings.LabelPrefix}  (last number used: {c.Settings.LabelCounter})");
        if (c.Settings.IgnoreRules.Count > 0) Console.WriteLine($"Ignore rules: {c.Settings.IgnoreRules.Count}");
        if (dataLost > 0) Console.WriteLine($"WARNING: {dataLost:N0} file(s) are gone from the source and have no surviving copy on any disc.");
        Archive.PrintTooBig(plan.TooBig);
        return 0;
    }
}
