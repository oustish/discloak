namespace Bdxl;

/// <summary>Shared by 'lost' and 'destroyed': the disc stops counting as a copy.</summary>
public static class MarkDeadDisc
{
    public static int Run(Ctx c, string state)
    {
        var disc = c.Arc.FindDisc(c.Arg(0, $"{state} <label> [--reason text]"));
        if (disc.State == state) { Console.WriteLine($"{disc.Label} is already marked {state}."); return 1; }
        if (disc.State == "destroyed") throw new UserException($"{disc.Label} is destroyed; that cannot be changed.");
        if (disc.PendingAppend is { } session)
            throw new UserException($"Session {session.Number} of {disc.Label} is staged but not burned: 'discard {disc.Label}' first " +
                                    "(that takes back only the new session).");

        disc.State = state;
        disc.LostUtc = DateTime.UtcNow;
        string? why = c.Opt.Get("reason");
        if (why != null) disc.Note = why;

        var affected = c.Cat.Files.Where(f => f.Backups.Any(b => b.Disc == disc.Label)).ToList();
        int relinked = c.Arc.LinkDuplicates();   // files whose content also exists on another live disc keep a valid copy
        var dead = c.Arc.DeadDiscs();
        var exposed = affected.Where(f => !c.Arc.HasLiveCopyOfCurrent(f, dead)).ToList();
        var rebackup = exposed.Where(f => f.MissingSinceUtc == null && !f.Ignored).ToList();
        var onSource = c.Arc.SourceHashes();
        var gone = exposed.Where(f => f.MissingSinceUtc != null && !onSource.Contains(f.Sha256)).ToList();
        var fewer = affected.Except(exposed).Where(f => !f.Ignored && f.Sha256.Length > 0 &&
                                                       Archive.BurnedCopies(f, dead) < c.Settings.CopiesFor(f.RootName)).ToList();
        c.Cat.Save();

        Console.WriteLine($"{disc.Label} marked as {state}. {affected.Count:N0} catalogued file(s) had a copy on it.");
        if (relinked > 0) Console.WriteLine($"{relinked:N0} of them still have an identical copy on another disc.");
        Console.WriteLine($"{rebackup.Count:N0} file(s) are still on the source and are pending again (next 'plan' / 'stage').");
        if (fewer.Count > 0)
        {
            int cannot = fewer.Count(f => f.MissingSinceUtc != null);
            Console.WriteLine($"{fewer.Count:N0} file(s) still have a copy, but fewer than their location wants; further copies are planned" +
                              (cannot > 0 ? $" ({cannot:N0} of them are gone from the source and cannot be copied again)." : "."));
        }
        if (gone.Count > 0)
        {
            Console.WriteLine($"\nDATA LOST - {gone.Count:N0} file(s) no longer exist on the source and have no other copy:");
            foreach (var f in gone.Take(50)) Console.WriteLine($"  {f.RootName}/{f.RelPath}");
            if (gone.Count > 50) Console.WriteLine($"  ... and {gone.Count - 50:N0} more (use 'find' to list)");
            return 2;
        }
        return 0;
    }
}
