namespace Bdxl;

public sealed class DiscardCommand : ICommand
{
    public string Name => "discard";
    public string Group => CommandGroup.MakingDiscs;
    public string Summary => "Forget a staged disc (or appended session) that was never burned";
    public string Usage => "discard <label>";
    public string Details => """
        Its files become pending again and its blank disc goes back to the inventory. The staged folder is not deleted.
        For a burned disc with a session staged by 'stage --append', only that session is taken back; what was burned
        before stays recorded.
        """;

    public int Run(Ctx c)
    {
        var disc = c.Arc.FindDisc(c.Arg(0, "discard <label>"));
        if (disc.PendingAppend is { } session)
        {
            foreach (var f in c.Cat.Files) f.Backups.RemoveAll(b => b.Disc == disc.Label && b.BackedUpUtc == null);
            disc.FileCount = session.FileCountBefore;
            disc.UsedBytes = session.UsedBytesBefore;
            disc.OccupiedBytes = session.OccupiedBytesBefore;
            disc.Sessions = session.Number - 1;
            disc.PendingAppend = null;
            c.Cat.Save();
            Console.WriteLine($"{disc.Label}: session {session.Number} discarded; its files are pending again. " +
                              "What was burned on the disc before stays recorded. (The staged folder, if any, was not deleted.)");
            return 0;
        }
        if (disc.State != "staged") throw new UserException("Only staged (not burned) discs can be discarded.");

        foreach (var f in c.Cat.Files) f.Backups.RemoveAll(b => b.Disc == disc.Label);
        c.Cat.Discs.Remove(disc);
        bool returned = disc.Capacity is not null;
        if (disc.Capacity is { } capacity)   // the blank disc it was going to use is available again
            c.Cat.Blanks.Add(new BlankDisc
            {
                Id = c.Arc.NextBlankId(), Capacity = capacity, Reserve = disc.Reserve ?? 0, Cost = disc.Cost, AddedUtc = DateTime.UtcNow
            });
        c.Cat.Save();
        Console.WriteLine($"{disc.Label} discarded; its files are pending again" + (returned ? " and its blank disc is back in the inventory" : "") +
                          ". (The staged folder, if any, was not deleted.)");
        return 0;
    }
}
