namespace Discloak.Tests;

/// <summary>
/// What bdxl (Discloak's name before 0.8.0) wrote, with the old names - bdxl.key, bdxl-pack-1, bdxl-key-2, BDXLPAK1,
/// BDXLENC2 - is read as it is, and gets the new names only where Discloak writes. The fixture was made by bdxl 0.7.1
/// (Fixtures/bdxl-0.7.1/README.md); every test works on a copy of it.
/// </summary>
[Collection(nameof(InterruptTests))]
public class LegacyNamesTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "bdxl-0.7.1");
    string Cat => tmp.File("cat");
    string Disc => tmp.File(Path.Combine("disc", "BDXL-0001"));

    public LegacyNamesTests()
    {
        CopyTree(Path.Combine(Fixture, "catalog"), Cat);
        CopyTree(Path.Combine(Fixture, "disc"), Disc);
    }

    static void CopyTree(string from, string to)
    {
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
    }

    int Discloak(params string[] args) => Program.Main([.. args, "--catalog", Cat, "--yes"]);

    (int Code, string Text) Run(params string[] args)
    {
        var output = new StringWriter();
        var stdout = Console.Out;
        Console.SetOut(output);
        try { return (Discloak(args), output.ToString()); }
        finally { Console.SetOut(stdout); }
    }

    static string Magic(string path) => Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 8);

    // the fixture's source files, made again from the rules in its README
    static byte[] RandomBytes(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
    static readonly byte[] A = RandomBytes(100_000, 11), B = RandomBytes(20_000, 12);
    static readonly byte[] Notes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("invoice total 2024 amount paid\r\n", 1000)));

    void AssertRestored(string dir)
    {
        Assert.Equal(A, File.ReadAllBytes(Path.Combine(dir, "Photos", "a.bin")));
        Assert.Equal(B, File.ReadAllBytes(Path.Combine(dir, "Photos", "b.bin")));
        Assert.Equal(Notes, File.ReadAllBytes(Path.Combine(dir, "Docs", "notes.txt")));
    }

    [Fact]
    public void The_fixture_has_the_old_names()
    {
        Assert.True(File.Exists(Path.Combine(Cat, KeyFile.LegacyFileName)));
        Assert.False(File.Exists(Path.Combine(Cat, KeyFile.FileName)));
        Assert.Equal("BDXLENC2", Magic(Path.Combine(Cat, "discs.jsonl.enc")));
        Assert.Equal("BDXLPAK1", Magic(Path.Combine(Disc, DiscFormat.PackPath(1))));
        Assert.Equal("BDXLENC2", Magic(Path.Combine(Disc, DiscFiles.ManifestName)));
        Assert.Equal(DiscFormat.LegacyName, DiscFiles.ReadInfo(Disc)!.Format);
        Assert.Equal(KeyFile.LegacyFormat2, KeyFile.ReadFrom(Disc)!.Format);
    }

    [Fact]
    public void The_old_catalog_opens_as_it_is_and_takes_the_new_names_when_it_is_saved()
    {
        Assert.Equal(0, Discloak("status"));
        Assert.Equal(0, Discloak("catalog", "check"));
        Assert.True(File.Exists(Path.Combine(Cat, KeyFile.LegacyFileName)));   // a command that only reads changes nothing

        Assert.Equal(0, Discloak("settings", "set", "currency", "PLN"));
        Assert.True(File.Exists(Path.Combine(Cat, KeyFile.FileName)));
        Assert.False(File.Exists(Path.Combine(Cat, KeyFile.LegacyFileName)));
        // one save brings every file to the new magic - also the parts of the file list this command did not read
        Assert.All(Catalog.StoredFilesIn(Cat), name => Assert.Equal("DCLKENC2", Magic(Path.Combine(Cat, name))));
        Assert.Equal(0, Discloak("catalog", "check"));
        var cat = TestKeys.Load(Cat);
        Assert.Equal((3, 1, "PLN"), (cat.Files.Count, cat.Discs.Count, cat.Settings.Currency));
    }

    [Fact]
    public void The_old_disc_verifies_and_restores()
    {
        var (code, text) = Run("verify", "BDXL-0001", Disc, "--deep");
        Assert.Equal(0, code);
        Assert.Contains($"OK       {KeyFile.LegacyFileName} (this catalog's", text);
        Assert.Contains("OK       manifest.enc (decrypts: 3 file(s))", text);
        Assert.Contains("OK       _catalog (", text);
        Assert.Equal(0, Discloak("verify", "BDXL-0001", Disc, "--no-cache"));
        Assert.Equal(0, Discloak("restore", tmp.File("out"), "*", "--mount", Disc));
        AssertRestored(tmp.File("out"));
    }

    [Fact]
    public void A_catalog_is_rebuilt_from_the_old_disc_and_from_its_old_catalog_backup()
    {
        Assert.Equal(0, Program.Main(["init", "--key-from", Disc, "--catalog", tmp.File("rebuilt"), "--yes"]));
        Assert.True(File.Exists(Path.Combine(tmp.File("rebuilt"), KeyFile.FileName)));   // a new catalog: the new name
        Assert.Equal(0, Program.Main(["scandisc", Disc, "--catalog", tmp.File("rebuilt"), "--yes"]));
        Assert.Equal(3, TestKeys.Load(tmp.File("rebuilt")).Files.Count);

        string backup = Path.Combine(Disc, DiscFiles.CatalogBackupName);
        Assert.Equal(0, Program.Main(["catalog", "backup", "verify", backup, "--catalog", tmp.File("unused")]));
        Assert.Equal(0, Program.Main(["catalog", "backup", "restore", backup, "--catalog", tmp.File("restored"), "--yes"]));
        Assert.True(File.Exists(Path.Combine(tmp.File("restored"), KeyFile.FileName)));
        Assert.Equal(3, TestKeys.Load(tmp.File("restored")).Files.Count);
    }

    [Fact]
    public void A_session_appended_to_the_old_disc_has_the_new_names_and_the_disc_reads_as_one()
    {
        var c = tmp.RandomFile("src/c.bin", 30_000, seed: 13);
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "More"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg"), "--append", "BDXL-0001"));
        string session = tmp.File(Path.Combine("stg", "BDXL-0001.s2"));
        Assert.True(File.Exists(Path.Combine(session, KeyFile.FileName)));
        Assert.False(File.Exists(Path.Combine(session, KeyFile.LegacyFileName)));
        Assert.Equal(DiscFormat.Name, DiscFiles.ReadInfo(session)!.Format);
        Assert.Equal("DCLKPAK1", Magic(Path.Combine(session, DiscFormat.PackPath(3))));
        Assert.Equal("DCLKENC2", Magic(Path.Combine(session, DiscFiles.ManifestFor(2))));

        // burned as a multisession disc: the session's files over the disc's
        foreach (string f in Directory.GetFiles(session, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(Disc, Path.GetRelativePath(session, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
        Assert.Equal(0, Discloak("burned", "BDXL-0001"));
        Assert.True(File.Exists(Path.Combine(Disc, KeyFile.LegacyFileName)) && File.Exists(Path.Combine(Disc, KeyFile.FileName)));
        Assert.Equal("BDXLPAK1", Magic(Path.Combine(Disc, DiscFormat.PackPath(1))));    // old and new packs side by side

        var (code, text) = Run("verify", "BDXL-0001", Disc, "--deep");
        Assert.Equal(0, code);
        Assert.Contains($"OK       {KeyFile.FileName} (this catalog's", text);     // the newer key file
        Assert.Contains("OK       the manifests list the 4 file(s) the catalog has on this disc", text);
        Assert.Equal(0, Discloak("restore", tmp.File("out"), "*", "--mount", Disc));
        AssertRestored(tmp.File("out"));
        Assert.Equal(c, File.ReadAllBytes(tmp.File("out/More/c.bin")));
    }
}
