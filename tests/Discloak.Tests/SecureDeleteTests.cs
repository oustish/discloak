namespace Discloak.Tests;

/// <summary>SecureDelete ('catalog import' wipes the plain export): overwritten in place, then gone without a trace in the folder.</summary>
public class SecureDeleteTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    [Theory]
    [InlineData(1)]
    [InlineData(700)]                // small enough to live inside the file system's own record
    [InlineData(3 * 1_048_576 + 5)]
    public void Overwrite_replaces_every_byte_and_keeps_the_length(int size)
    {
        string path = tmp.File("plain.jsonl");
        System.IO.File.WriteAllBytes(path, new byte[size]);   // zeros
        SecureDelete.Overwrite(path);
        byte[] after = System.IO.File.ReadAllBytes(path);
        Assert.Equal(size, after.Length);
        Assert.True(size < 64 || after.Count(b => b == 0) < size / 64);   // random: about 1 in 256 bytes is 0
    }

    [Fact]
    public void A_wiped_folder_is_gone_with_every_file_and_a_folder_with_subfolders_is_not_wiped()
    {
        string dir = tmp.File("export");
        Directory.CreateDirectory(dir);
        foreach (string name in new[] { "files.jsonl", "discs.jsonl", "notes.txt" })
            System.IO.File.WriteAllText(Path.Combine(dir, name), "C:/Users/someone/secret-plans.txt");
        System.IO.File.SetAttributes(Path.Combine(dir, "notes.txt"), FileAttributes.ReadOnly);

        Assert.Null(SecureDelete.Unsafe(dir));
        Assert.Empty(SecureDelete.Folder(dir));
        Assert.False(Directory.Exists(dir));

        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        Assert.Contains("subfolders", SecureDelete.Unsafe(dir));
        Assert.Contains("root of a drive", SecureDelete.Unsafe(Path.GetPathRoot(dir)!));
    }
}
