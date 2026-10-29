namespace Bdxl;

public sealed class BurnedCommand : ICommand
{
    public string Name => "burned";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Mark a staged disc as burned";
    public string Usage => "burned <label> [--date yyyy-MM-dd]";
    public string Details => "Records the burn date on the disc and on every file copy it holds. Run 'verify' first.";

    public int Run(Ctx c)
    {
        var disc = c.Arc.FindDisc(c.Arg(0, "burned <label> [--date yyyy-MM-dd]"));
        if (disc.State is "lost" or "destroyed")
            throw new UserException($"{disc.Label} is {disc.State}. Use 'restage' to rebuild it first.");
        if (disc.State == "burned") { Console.WriteLine($"{disc.Label} is already marked as burned."); return 1; }

        DateTime when = c.Opt.GetDate("date") ?? DateTime.UtcNow;
        disc.State = "burned";
        disc.BurnedUtc = when;
        foreach (var b in c.Cat.Files.SelectMany(f => f.Backups).Where(b => b.Disc == disc.Label))
            b.BackedUpUtc = when;

        c.Cat.Save();
        Console.WriteLine($"{disc.Label} marked as burned on {when:yyyy-MM-dd}.");
        return 0;
    }
}
