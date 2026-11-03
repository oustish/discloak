namespace Bdxl.Tests;

/// <summary>Compression per location from scan to restore (the stored form itself: PackTests).</summary>
[Collection(nameof(InterruptTests))]   // non-parallel: a test reads what a command prints (Console.Out is the whole process's)
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
        Assert.Equal((3, "zstd"), (copy.Compression, copy.Codec));
        Assert.Null(Cat.Files.Single(f => f.RelPath == "photo.jpg").Backups.Single().Codec);
        var lines = DiscFiles.ReadManifest(Disc, TestKeys.Key(tmp.File("cat")))!;
        Assert.Equal((3, "zstd"), (lines.Single(l => l.Source == "Docs/ledger.csv").Compression, lines.Single(l => l.Source == "Docs/ledger.csv").Codec));
        Assert.Null(lines.Single(l => l.Source == "Docs/photo.jpg").Codec);
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
        var fromDisc = TestKeys.Load(tmp.File("other")).Files.Single(f => f.RelPath == "ledger.csv").Backups.Single();
        Assert.Equal((3, "zstd"), (fromDisc.Compression, fromDisc.Codec));
        Assert.Equal([BuildInfo.CreatedBy], TestKeys.Load(tmp.File("other")).Discs.Single().WrittenBy);   // from its disc.json
    }

    [Fact]
    public void A_copy_without_a_codec_is_zstd_and_one_with_a_codec_this_bdxl_does_not_know_is_refused_with_a_reason()
    {
        var ledger = Text(300_000, seed: 1);
        File.WriteAllBytes(Directory.CreateDirectory(tmp.File("src")).FullName + "/ledger.csv", ledger);
        Assert.Equal(0, Bdxl("blanks", "add", "1", "--size", "2MB"));
        Assert.Equal(0, Bdxl("compression", "set", "--default", "on"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "Docs"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));

        void SetCodec(string? codec)
        {
            var cat = Cat;
            cat.Files.Single().Backups.Single().Codec = codec;
            cat.Save();
        }
        SetCodec(null);   // as in a catalog from before the codec was recorded
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", Disc));
        Assert.Equal(ledger, File.ReadAllBytes(tmp.File("out/Docs/ledger.csv")));

        SetCodec("xz");   // as if a newer bdxl had made it
        var output = new StringWriter();
        var stdout = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.Equal(3, Bdxl("restore", tmp.File("out2"), "*", "--mount", Disc));
            Assert.Equal(3, Bdxl("verify", "BDXL-0001", Disc, "--deep"));
        }
        finally { Console.SetOut(stdout); }
        Assert.Contains("compressed with 'xz', which this bdxl does not know (a newer bdxl reads it)", output.ToString());
        Assert.False(File.Exists(tmp.File("out2/Docs/ledger.csv")));
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
