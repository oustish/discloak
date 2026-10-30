namespace Bdxl;

public sealed class FoundCommand : ICommand
{
    public string Name => "found";
    public string Group => CommandGroup.Recovery;
    public string Summary => "A disc marked lost turned up";
    public string Usage => "found <label>";
    public bool NeedsFilesFor(Options opt) => false;   // the disc's state only
    public string Details => "Puts a lost disc back to burned / staged. Check it with 'verify'.";

    public int Run(Ctx c)
    {
        var disc = c.Arc.FindDisc(c.Arg(0, "found <label>"));
        if (disc.State != "lost") throw new UserException("Only discs marked 'lost' can be marked as found.");
        disc.State = disc.BurnedUtc != null ? "burned" : "staged";
        disc.LostUtc = null;
        c.Cat.Save();
        Console.WriteLine($"{disc.Label} is '{disc.State}' again. Check it with:  verify {disc.Label} <mountPath>");
        return 0;
    }
}
