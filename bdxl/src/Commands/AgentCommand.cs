namespace Bdxl;

/// <summary>The agent process itself (started by 'unlock'; not meant to be run by hand).</summary>
public sealed class AgentCommand : ICommand
{
    public string Name => "agent";
    public string Group => CommandGroup.Catalog;
    public string Summary => "The background agent that 'unlock' starts (internal)";
    public string Usage => "agent";
    public string Details => "Started by 'unlock' from a copy of the program; holds unlocked catalog keys in memory. See 'unlock'.";
    public bool NeedsCatalog => false;
    public bool Hidden => true;

    static readonly object LogGate = new();

    public int Run(Ctx c)
    {
        using var server = new AgentServer(AgentProtocol.PipeName, Log);
        try
        {
            server.Run();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("not started, another agent holds the pipe: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Log("agent failed: " + ex);
            throw;
        }
    }

    /// <summary>One line in agent.log; the log is cut to its newer half beyond 1 MB.</summary>
    public static void Log(string message)
    {
        lock (LogGate)
        {
            try
            {
                string path = AgentLauncher.LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                {
                    var lines = File.ReadAllLines(path);
                    File.WriteAllLines(path, lines.Skip(lines.Length / 2));
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
