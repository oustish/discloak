using System.Reflection;

namespace Discloak;

/// <summary>
/// The program on every disc, in _program\: the discloak.exe that staged it (self-contained: nothing needs to be
/// installed to run it), its source code (the exact tree it was built from, embedded in the exe at build time: the
/// DiscloakSourceZip target in Discloak.csproj) and the licenses that go with every copy of it. A disc found decades
/// later carries what reads it. 'stage' and 'restage' write it unless --without-program; plan counts its space.
/// </summary>
public static class DiscProgram
{
    public const string FolderName = "_program";
    public const string ExeName = "discloak.exe";
    const string SourceResource = "discloak-source.zip";
    static readonly string[] LicenseResources = ["LICENSE.md", "LICENSING.md", "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES-dotnet.txt"];
    static readonly Assembly Me = typeof(DiscProgram).Assembly;

    /// <summary>
    /// The exe that goes onto discs: the running discloak.exe when it is the published single file (it has no
    /// discloak.dll next to it); null when it runs from a build folder (dotnet run, the tests), which has no single
    /// exe to copy.
    /// </summary>
    public static string? ExePath { get; } = PublishedExe();

    static string? PublishedExe()
    {
        string? exe = Environment.ProcessPath;
        return exe != null && Path.GetFileName(exe).Equals(ExeName, StringComparison.OrdinalIgnoreCase) &&
               !File.Exists(Path.Combine(AppContext.BaseDirectory, "discloak.dll")) ? exe : null;
    }

    /// <summary>"discloak-0.9.0-source.zip".</summary>
    public static string SourceFileName => $"discloak-{BuildInfo.Version}-source.zip";

    /// <summary>The space to keep for the program on every disc and session (0 with --without-program).</summary>
    public static long SpaceFor(Options opt) => opt.Has("without-program") ? 0 : SpaceOnDisc();

    /// <summary>What the folder takes on a disc: counted as DiscSpace.Folder counts it once written, plus its entry in the disc's list.</summary>
    public static long SpaceOnDisc()
    {
        var sizes = Files().Select(f => f.Size).ToList();
        return sizes.Sum(s => DiscSpace.RoundUp(s) + DiscSpace.Sector) + DiscSpace.Sector + DiscSpace.RoundUp(sizes.Count * (long)DiscSpace.FolderEntry)
               + DiscSpace.FolderEntry;
    }

    /// <summary>What a written program folder takes (the same count as SpaceOnDisc).</summary>
    public static long SpaceOf(string discDir) => DiscSpace.Folder(Path.Combine(discDir, FolderName)) + DiscSpace.FolderEntry;

    static IReadOnlyList<(string Name, byte[] Data)>? testFiles;

    /// <summary>Tests: small stand-ins for the folder's files (the real exe and source would not fit test discs of 1 MB).</summary>
    public static void UseFilesForTests(IReadOnlyList<(string Name, byte[] Data)>? files) => testFiles = files;

    /// <summary>The files of the folder: name, size and where the bytes come from.</summary>
    static IEnumerable<(string Name, long Size, Func<Stream> Open)> Files()
    {
        if (testFiles != null)
        {
            foreach (var (name, data) in testFiles) yield return (name, data.Length, () => new MemoryStream(data, writable: false));
            yield break;
        }
        if (ExePath is { } exe && File.Exists(exe))
            yield return (ExeName, new FileInfo(exe).Length, () => File.OpenRead(exe));
        foreach (var (resource, name) in LicenseResources.Select(r => (r, r)).Prepend((SourceResource, SourceFileName)))
            if (Me.GetManifestResourceInfo(resource) != null)
            {
                using var probe = Me.GetManifestResourceStream(resource)!;
                yield return (name, probe.Length, () => Me.GetManifestResourceStream(resource)!);
            }
    }

    /// <summary>Writes the folder into a disc folder. Returns what had to be left out, or null.</summary>
    public static string? Write(string discDir)
    {
        string dir = Path.Combine(discDir, FolderName);
        Directory.CreateDirectory(dir);
        foreach (var (name, _, open) in Files())
        {
            using var input = open();
            using var output = new FileStream(Path.Combine(dir, name), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            input.CopyTo(output);
        }
        if (testFiles != null) return null;
        var missing = new List<string>();
        if (ExePath == null) missing.Add($"{ExeName} (this is not the published {ExeName}: it runs from a build folder)");
        if (Me.GetManifestResourceInfo(SourceResource) == null) missing.Add("the source code (this exe was built without git)");
        return missing.Count == 0 ? null : string.Join("; ", missing);
    }
}
