namespace Bdxl;

/// <summary>version (or --version) - which build of bdxl this is.</summary>
public sealed class VersionCommand : ICommand
{
    public string Name => "version";
    public string Group => CommandGroup.Help;
    public string Summary => "Show the version, the commit it was built from and the build date";
    public string Usage => "version   (or: bdxl --version)";
    public string Details => """
        The version (major.minor.patch), Debug or Release, and the git commit and branch it was built from - with *
        when it was built with uncommitted changes - and when it was built (UTC). The last line is the full version
        as every staged disc records it in disc.json ("createdBy"). Include it when reporting a problem.
        """;
    public bool AllowBrokenCatalog => true;
    public bool NeedsCatalog => false;

    public int Run(Ctx c) => Print();

    public static int Print()
    {
        Console.WriteLine($"bdxl {BuildInfo.Version} ({BuildInfo.Configuration})");
        string commit = BuildInfo.Commit is { } id
            ? $"commit {id}{(BuildInfo.Dirty ? "*" : "")} on {BuildInfo.Branch ?? "a detached HEAD"}"
            : "commit unknown (built without git)";
        string built = BuildInfo.BuiltUtc is { } t ? $", built {t:yyyy-MM-dd HH:mm} UTC" : "";
        Console.WriteLine(commit + built + (BuildInfo.Dirty ? "   (*: with uncommitted changes)" : ""));
        Console.WriteLine($"full version: {BuildInfo.Full}");
        return 0;
    }
}
