namespace Discloak;

public sealed class BurnedCommand : ICommand
{
    public string Name => "burned";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Mark a staged disc (or its appended session) as burned";
    public string Usage => "burned <label> [--date yyyy-MM-dd]";
    public string Details => """
        Records the burn date on the disc and on every file copy it holds. Run 'verify' first.
        For a session added with 'stage --append', only the copies of that session get the date.
        """;

    public int Run(Ctx c)
    {
        var disc = c.Arc.FindDisc(c.Arg(0, "burned <label> [--date yyyy-MM-dd]"));
        if (disc.State is "lost" or "destroyed")
            throw new UserException($"{disc.Label} is {disc.State}. Use 'restage' to rebuild it first.");
        DateTime when = c.Opt.GetDate("date") ?? DateTime.UtcNow;

        if (disc.State == "burned")
        {
            if (disc.PendingAppend is not { } session) { Console.WriteLine($"{disc.Label} is already marked as burned."); return 1; }
            foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label && b.BackedUpUtc == null))
                b.BackedUpUtc = when;
            disc.PendingAppend = null;
            c.Cat.Save();
            Console.WriteLine($"{disc.Label}: session {session.Number} marked as burned on {when:yyyy-MM-dd}.");
            return 0;
        }

        disc.State = "burned";
        disc.BurnedUtc = when;
        foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label))
            b.BackedUpUtc = when;

        c.Cat.Save();
        Console.WriteLine($"{disc.Label} marked as burned on {when:yyyy-MM-dd}.");
        return 0;
    }
}
