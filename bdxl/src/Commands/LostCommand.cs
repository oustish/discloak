namespace Bdxl;

public sealed class LostCommand : ICommand
{
    public string Name => "lost";
    public string Group => CommandGroup.Recovery;
    public string Summary => "Mark a disc as lost (it may turn up again)";
    public string Usage => "lost <label> [--reason text]";
    public string Details => "Copies on it stop counting. Files still on the source become pending again; files that exist nowhere else are listed as DATA LOST.";

    public int Run(Ctx c) => MarkDeadDisc.Run(c, "lost");
}
