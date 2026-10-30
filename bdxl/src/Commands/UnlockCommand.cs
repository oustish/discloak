namespace Bdxl;

public sealed class UnlockCommand : ICommand
{
    public string Name => "unlock";
    public string Group => CommandGroup.Catalog;
    public string Summary => "Keep the catalog unlocked for a while (no password per command)";
    public string Usage => "unlock [--idle 30m] [--max 8h] | unlock --status";
    public string Details => """
        Asks for the password once and hands the catalog's master key to a background agent; until it is locked,
        commands on this catalog take the key from the agent instead of asking. The agent keeps keys in memory only
        (never on disk), one per catalog, for this Windows user only.
        A catalog is locked again:
          - after --idle without any command using it        (default: settings unlock-idle, 30m)
          - after --max at the latest                         (default: settings unlock-max, 8h)
          - when Windows is locked (Win+L) or goes to sleep   (settings lock-on-win-l / lock-on-sleep)
          - by 'lock' (this catalog) or 'lock --all', and when you sign out
        The agent runs from a copy of the program in %LOCALAPPDATA%\bdxl\agent and ends when it holds no key.
          unlock --status    what is unlocked, and until when
        BDXL_AGENT=off turns the agent off for a command (it then always asks for the password).
        """;

    public bool NeedsCatalogFor(Options opt) => !opt.Has("status");

    public int Run(Ctx c)
    {
        var client = new AgentClient();
        if (c.Opt.Has("status")) return Status(client);
        if (!AgentProtocol.Enabled) throw new UserException("The agent is turned off (BDXL_AGENT=off).");

        var idle = c.Opt.Has("idle") ? SizeText.ParseDuration(c.Opt.Get("idle")!) : TimeSpan.FromMinutes(c.Settings.UnlockIdleMinutes);
        var max = c.Opt.Has("max") ? SizeText.ParseDuration(c.Opt.Get("max")!) : TimeSpan.FromMinutes(c.Settings.UnlockMaxMinutes);
        if (idle < TimeSpan.FromMinutes(1) || max > TimeSpan.FromDays(7) || idle > max)
            throw new UserException("--idle must be at least 1m, --max at most 7d, and --idle not longer than --max.");

        var r = Put(client, c, idle, max);
        string note = "";
        if (r != null && r.Protocol != AgentProtocol.Version)
        {
            Console.WriteLine("An agent of another bdxl version is running; it is replaced (catalogs it held must be unlocked again).");
            client.StopAgent();
            WaitUntilGone(client);
            r = null;
        }
        if (r == null)
        {
            note = AgentLauncher.Start(client);
            r = Put(client, c, idle, max);
        }
        if (r is not { Ok: true }) throw new UserException("The agent did not take the key: " + (r?.Error ?? "no answer") + ".");

        var now = DateTime.Now;
        Console.WriteLine($"Unlocked {c.Cat.Dir}");
        Console.WriteLine($"  locks after {SizeText.Duration(idle)} without use, and at {now + max:yyyy-MM-dd HH:mm} at the latest" +
                          (c.Settings.LockOnSessionLock || c.Settings.LockOnSleep
                              ? "; also when Windows " + string.Join(" or ", new[] { c.Settings.LockOnSessionLock ? "is locked" : null, c.Settings.LockOnSleep ? "goes to sleep" : null }.Where(x => x != null))
                              : "") + ".");
        if (note.Length > 0) Console.WriteLine(note);
        return 0;
    }

    static AgentResponse? Put(AgentClient client, Ctx c, TimeSpan idle, TimeSpan max) =>
        client.Put(c.Keys, c.Cat.Dir, idle, max, c.Settings.LockOnSessionLock, c.Settings.LockOnSleep);

    static void WaitUntilGone(AgentClient client)
    {
        for (int i = 0; i < 50 && client.Status() != null; i++) Thread.Sleep(100);
    }

    static int Status(AgentClient client)
    {
        var r = client.Status();
        if (r == null) { Console.WriteLine("No agent is running: every catalog is locked."); return 0; }
        Console.WriteLine($"Agent: process {r.Pid}, build {r.Build}" + (r.Protocol != AgentProtocol.Version ? $" (another bdxl version: protocol {r.Protocol})" : ""));
        if (r.Entries is not { Count: > 0 }) { Console.WriteLine("Nothing is unlocked."); return 0; }
        foreach (var e in r.Entries)
        {
            var locksAt = e.IdleExpiresUtc < e.MaxExpiresUtc ? e.IdleExpiresUtc : e.MaxExpiresUtc;
            Console.WriteLine($"  {e.CatalogDir ?? e.CatalogId}");
            Console.WriteLine($"      unlocked {e.UnlockedUtc.ToLocalTime():yyyy-MM-dd HH:mm}; locks at {locksAt.ToLocalTime():HH:mm} unless used " +
                              $"(at {e.MaxExpiresUtc.ToLocalTime():yyyy-MM-dd HH:mm} at the latest)");
        }
        return 0;
    }
}
