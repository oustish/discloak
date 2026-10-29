namespace Bdxl;

public sealed class DestroyedCommand : ICommand
{
    public string Name => "destroyed";
    public string Group => CommandGroup.Recovery;
    public string Summary => "Mark a disc as destroyed (final)";
    public string Usage => "destroyed <label> [--reason text]";
    public string Details => "Same as 'lost' but cannot be undone.";

    public int Run(Ctx c) => MarkDeadDisc.Run(c, "destroyed");
}
