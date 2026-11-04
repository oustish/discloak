namespace Discloak;

/// <summary>version (or --version) - which build of discloak this is.</summary>
public sealed class VersionCommand : ICommand
{
    public string Name => "version";
    public string Group => CommandGroup.Help;
    public string Summary => "Show the version, the commit it was built from and the build date";
    public string Usage => "version   (or: discloak --version)";
    public string Details => """
        The version (major.minor.patch), Debug or Release, and the git commit and branch it was built from - with *
        when it was built with uncommitted changes - and when it was built (UTC). The last line is the full version
        as every staged disc records it in disc.json ("createdBy"), then the copyright and the license. Include it
        when reporting a problem.
        """;
    public bool AllowBrokenCatalog => true;
    public bool NeedsCatalog => false;

    public int Run(Ctx c) => Print();

    public static int Print()
    {
        Console.WriteLine($"discloak {BuildInfo.Version} ({BuildInfo.Configuration})");
        string commit = BuildInfo.Commit is { } id
            ? $"commit {id}{(BuildInfo.Dirty ? "*" : "")} on {BuildInfo.Branch ?? "a detached HEAD"}"
            : "commit unknown (built without git)";
        string built = BuildInfo.BuiltUtc is { } t ? $", built {t:yyyy-MM-dd HH:mm} UTC" : "";
        Console.WriteLine(commit + built + (BuildInfo.Dirty ? "   (*: with uncommitted changes)" : ""));
        Console.WriteLine($"full version: {BuildInfo.Full}");
        Console.WriteLine($"{BuildInfo.Copyright}. License: {BuildInfo.License} - free to use, not to compete with " +
                          "(LICENSE.md, LICENSING.md and THIRD-PARTY-NOTICES.md come with discloak.exe).");
        Console.WriteLine($"Source, releases, documentation: {BuildInfo.ProjectUrl}");
        return 0;
    }
}
