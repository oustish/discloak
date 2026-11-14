namespace Discloak;

public sealed class BurnedCommand : ICommand
{
    public string Name => "burned";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Mark a staged disc (or its appended session) as burned";
    public string Usage => "burned <label> [--date yyyy-MM-dd]";
    public string Details => """
        Records the burn date on the disc and on every file copy it holds. Run 'verify' first ('verify --mark-burned'
        does both: it marks the disc only if everything checked out).
        For a session added with 'stage --append', only the copies of that session get the date.
        """;

    public int Run(Ctx c)
    {
        var disc = c.Arc.FindDisc(c.Arg(0, "burned <label> [--date yyyy-MM-dd]"));
        CheckCanMark(disc);
        var (marked, message) = Mark(c, disc, c.Opt.GetDate("date") ?? DateTime.UtcNow);
        if (marked) c.Cat.Save();
        Console.WriteLine(message);
        return marked ? 0 : 1;
    }

    /// <summary>A lost or destroyed disc cannot be marked as burned (UserException).</summary>
    public static void CheckCanMark(DiscRecord disc)
    {
        if (disc.State is "lost" or "destroyed")
            throw new UserException($"{disc.Label} is {disc.State}. Use 'restage' to rebuild it first.");
    }

    /// <summary>
    /// Marks the staged disc, or its staged session, as burned on `when` (the caller saves the catalog).
    /// Marked false: there was nothing to mark (already burned, no session staged); the message says so.
    /// </summary>
    public static (bool Marked, string Message) Mark(Ctx c, DiscRecord disc, DateTime when)
    {
        CheckCanMark(disc);
        if (disc.State == "burned")
        {
            if (disc.PendingAppend is not { } session) return (false, $"{disc.Label} is already marked as burned.");
            foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label && b.BackedUpUtc == null))
                b.BackedUpUtc = when;
            disc.PendingAppend = null;
            return (true, $"{disc.Label}: session {session.Number} marked as burned on {when:yyyy-MM-dd}.");
        }

        disc.State = "burned";
        disc.BurnedUtc = when;
        foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label))
            b.BackedUpUtc = when;
        return (true, $"{disc.Label} marked as burned on {when:yyyy-MM-dd}.");
    }
}
