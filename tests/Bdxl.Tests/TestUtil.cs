using System.Runtime.CompilerServices;

namespace Bdxl.Tests;

/// <summary>
/// Every test catalog is encrypted with one password, with the cheapest Argon2id settings allowed (fast tests).
/// BDXL_PASSWORD is set for the whole test run; the agent is never used.
/// </summary>
public static class TestKeys
{
    public const string Password = "Correct-Horse-42!";
    public static readonly KdfParams Fast = new(Kdf.MinKiB, 1);

    [ModuleInitializer]
    internal static void Setup()
    {
        Environment.SetEnvironmentVariable("BDXL_PASSWORD", Password);
        Environment.SetEnvironmentVariable("BDXL_AGENT", "off");
    }

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

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bdxl-test-" + Guid.NewGuid().ToString("N"));

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
