namespace Bdxl.Tests;

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
