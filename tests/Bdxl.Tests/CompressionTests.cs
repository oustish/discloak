namespace Bdxl.Tests;

/// <summary>Compression per location from scan to restore (the stored form itself: PackTests).</summary>
public class CompressionTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    /// <summary>Text-like data that compresses well.</summary>
    static byte[] Text(int size, int seed = 1)
    {
        var words = new[] { "invoice ", "total ", "2024 ", "amount ", "paid ", "\r\n", "customer ", "number " };
        var rnd = new Random(seed);
        var sb = new StringBuilder();
        while (sb.Length < size) sb.Append(words[rnd.Next(words.Length)]);
        return Encoding.ASCII.GetBytes(sb.ToString(0, size));
    }

    static byte[] Random(int size, int seed = 1)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void Auto_skips_compressed_formats_and_off_is_the_default()
    {
        var s = new Settings();
        var doc = new FileRecord { RootName = "Docs", RelPath = "a/report.txt" };
        var photo = new FileRecord { RootName = "Docs", RelPath = "a/IMG_1.JPG" };
        Assert.Equal(0, Compression.LevelFor(s, doc));

        s.CompressionByAlias["docs"] = new CompressionSetting { Mode = "auto", Level = 5 };
        Assert.Equal(5, Compression.LevelFor(s, doc));
        Assert.Equal(0, Compression.LevelFor(s, photo));
        s.CompressionByAlias["docs"] = new CompressionSetting { Mode = "on", Level = 5 };
        Assert.Equal(5, Compression.LevelFor(s, photo));
    }

    // ---- end to end ------------------------------------------------------------------------------------------------

    int Bdxl(params string[] args) => Program.Main([.. args, "--catalog", TestKeys.Init(tmp.File("cat")), "--yes"]);
    Catalog Cat => TestKeys.Load(tmp.File("cat"));
    string Disc => tmp.File("stg/BDXL-0001");

    [Fact]
    public void A_location_that_compresses_fits_on_a_disc_it_would_overflow_otherwise()
    {
        var ledger = Text(3_000_000, seed: 1);
        var photo = Random(60_000, seed: 2);
        File.WriteAllBytes(Directory.CreateDirectory(tmp.File("src")).FullName + "/ledger.csv", ledger);
        File.WriteAllBytes(tmp.File("src/photo.jpg"), photo);
        Assert.Equal(0, Bdxl("blanks", "add", "1", "--size", "2MB", "--reserve", "100KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "Docs"));
        Assert.Equal(2, Bdxl("plan"));                                              // 3 MB do not fit on 2 MB

        Assert.Equal(0, Bdxl("compression", "set", "Docs", "auto"));
        Assert.Equal(2, Bdxl("plan"));                                              // not counted yet: still counted uncompressed
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "Docs"));             // reads ledger.csv again, not photo.jpg
        var ledgerRec = Cat.Files.Single(f => f.RelPath == "ledger.csv");
        Assert.True(ledgerRec.CompressedSize < 1_000_000);
        Assert.Null(Cat.Files.Single(f => f.RelPath == "photo.jpg").CompressedSize);
        Assert.Equal(0, Bdxl("plan"));
        Assert.Equal(0, Bdxl("compression", "list"));

        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));
        var copy = Cat.Files.Single(f => f.RelPath == "ledger.csv").Backups.Single();
        Assert.Equal(3, copy.Compression);
        Assert.Equal(ledgerRec.CompressedSize, copy.StoredSize);                     // exactly as counted
        Assert.Equal(0, Cat.Files.Single(f => f.RelPath == "photo.jpg").Backups.Single().Compression);
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", Disc, "--deep"));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", Disc));
        Assert.Equal(ledger, File.ReadAllBytes(tmp.File("out/Docs/ledger.csv")));
        Assert.Equal(photo, File.ReadAllBytes(tmp.File("out/Docs/photo.jpg")));

        // rebuilt at the same level, so the same size (the disc's layout holds); a new catalog from the disc knows the level
        Assert.Equal(0, Bdxl("lost", "BDXL-0001"));
        Assert.Equal(0, Bdxl("compression", "set", "Docs", "off"));                  // today's setting does not matter
        Assert.Equal(0, Bdxl("restage", "BDXL-0001", tmp.File("re")));
        Assert.Equal(copy.StoredSize, Cat.Files.Single(f => f.RelPath == "ledger.csv").Backups.Single().StoredSize);
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", tmp.File("re/BDXL-0001"), "--deep"));

        Assert.Equal(0, Program.Main(["init", "--key-from", Disc, "--catalog", tmp.File("other")]));
        Assert.Equal(0, Program.Main(["scandisc", Disc, "--catalog", tmp.File("other")]));
        Assert.Equal(3, TestKeys.Load(tmp.File("other")).Files.Single(f => f.RelPath == "ledger.csv").Backups.Single().Compression);
    }

    [Fact]
    public void A_new_level_is_counted_again_and_an_alias_keeps_its_setting_when_renamed()
    {
        File.WriteAllBytes(Directory.CreateDirectory(tmp.File("src")).FullName + "/a.txt", Text(200_000));
        Assert.Equal(0, Bdxl("compression", "set", "--default", "on"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "Docs"));
        Assert.Equal(Compression.Signature(3), Cat.Files.Single().CompressedWith);

        Assert.Equal(0, Bdxl("compression", "set", "Docs", "on", "--level", "9"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "Docs"));
        Assert.Equal(Compression.Signature(9), Cat.Files.Single().CompressedWith);

        Assert.Equal(0, Bdxl("alias", "rename", "Docs", "Papers"));
        Assert.Equal(9, Compression.For(Cat.Settings, "Papers").Level);
        Assert.Equal(0, Bdxl("compression", "clear", "Papers"));
        Assert.Equal(3, Compression.For(Cat.Settings, "Papers").Level);
        Assert.Equal(1, Bdxl("compression", "set", "Nope", "on"));
        Assert.Equal(1, Bdxl("compression", "set", "Papers", "max"));
    }
}
