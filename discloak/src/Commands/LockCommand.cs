namespace Discloak;

public sealed class LockCommand : ICommand
{
    public string Name => "lock";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Lock the catalog again (or every catalog: --all)";
    public string Usage => "lock [--all]";
    public string Details => "Wipes the catalog's key from the agent ('unlock'); commands ask for the password again. " +
                             "With --all every catalog is locked and the agent ends. Needs no password.";
    public bool NeedsCatalog => false;   // only the catalog id from discloak.key is needed

    public int Run(Ctx c)
    {
        var client = new AgentClient();
        AgentResponse? r;
        if (c.Opt.Has("all")) r = client.LockAll();
        else
        {
            var file = KeyFile.ReadFrom(c.Cat.Dir) ?? throw new UserException($"No encrypted catalog in {c.Cat.Dir}.");
            r = client.Lock(file.CatalogId);
        }
        if (r == null) { Console.WriteLine("No agent is running: nothing was unlocked."); return 0; }
        int left = r.Entries?.Count ?? 0;
        Console.WriteLine((c.Opt.Has("all") ? "Every catalog is locked." : $"Locked {c.Cat.Dir}.") +
                          (left > 0 ? $" {left} other catalog(s) stay unlocked (lock --all)." : " The agent ends."));
        return 0;
    }
}
