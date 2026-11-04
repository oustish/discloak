using System.Runtime.CompilerServices;

namespace Discloak.Tests;

/// <summary>
/// Every test catalog is encrypted with one password, with the cheapest Argon2id settings allowed (fast tests).
/// DISCLOAK_PASSWORD is set for the whole test run; the agent is never used. Discs use packs of 64 KiB (8 chunks of
/// 8 KiB) instead of 64 MiB, so test discs of a few hundred KB still hold several packs, and their _program folder
/// holds small stand-ins (ProgramFiles) instead of a 37 MB exe and its source.
/// </summary>
public static class TestKeys
{
    public const string Password = "Correct-Horse-42!";
    public static readonly KdfParams Fast = new(Kdf.MinKiB, 1);
    public const int ChunkSize = 8192, ChunksPerPack = 8;

    /// <summary>What test discs get in _program: a stand-in exe, a small but real zip, and a license file.</summary>
    public static readonly IReadOnlyList<(string Name, byte[] Data)> ProgramFiles =
        [(DiscProgram.ExeName, Encoding.ASCII.GetBytes("a stand-in for discloak.exe")), ("discloak-0.0.0-source.zip", SmallZip()), ("LICENSE.md", "the license"u8.ToArray())];

    static byte[] SmallZip()
    {
        var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = new StreamWriter(zip.CreateEntry("discloak/README.md").Open()))
            entry.Write("the source");
        return bytes.ToArray();
    }

    [ModuleInitializer]
    internal static void Setup()
    {
        Environment.SetEnvironmentVariable("DISCLOAK_PASSWORD", Password);
        Environment.SetEnvironmentVariable("DISCLOAK_AGENT", "off");
        DiscFormat.UseGeometryForTests(ChunkSize, ChunksPerPack);
        DiscProgram.UseFilesForTests(ProgramFiles);
    }

    /// <summary>The pack files of a staged disc folder, as relative paths in order.</summary>
    public static List<string> Packs(string disc) =>
        Directory.Exists(Path.Combine(disc, "data"))
            ? Directory.GetFiles(Path.Combine(disc, "data"), "*.pack", SearchOption.AllDirectories)
                       .Select(f => Path.GetRelativePath(disc, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>An empty encrypted catalog in dir (what 'init' makes), unless there is one.</summary>
    public static string Init(string dir)
    {
        if (System.IO.File.Exists(KeyFile.PathIn(dir))) return dir;
        using var key = KeyRing.Of(MasterKey.CreateNew());
        Directory.CreateDirectory(dir);
        Catalog.CreateEmpty(dir, "init", key).Save();
        KeyFile.Create(key, Password, Fast).WriteTo(dir);
        return dir;
    }

    public static KeyRing Key(string dir) => KeyFile.ReadFrom(dir)!.TryUnlock(Password)!;

    public static Catalog Load(string dir, string command = "test") => Catalog.Load(dir, command, Key(dir));
}

/// <summary>A fresh folder under %TEMP%, deleted again when the test is done.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir() { Directory.CreateDirectory(Path); }

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "discloak-test-" + Guid.NewGuid().ToString("N"));

    public string File(string relPath) => System.IO.Path.Combine(Path, relPath);

    /// <summary>Writes size random bytes to relPath (folders are created) and returns the bytes.</summary>
    public byte[] RandomFile(string relPath, int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        string full = File(relPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, data);
        return data;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
