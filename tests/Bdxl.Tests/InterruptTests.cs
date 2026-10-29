namespace Bdxl.Tests;

/// <summary>Interrupt is process-wide, so these tests must not run beside others that run commands.</summary>
[CollectionDefinition(nameof(InterruptTests), DisableParallelization = true)]
public class InterruptCollection { }

[Collection(nameof(InterruptTests))]
public class InterruptTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    int Bdxl(params string[] args) => Program.Main([.. args, "--catalog", tmp.File("cat"), "--yes"]);

    [Fact]
    public void An_interrupted_scan_saves_what_it_has_marks_nothing_missing_and_can_be_run_again()
    {
        tmp.RandomFile("src/a.bin", 1000, seed: 1);
        tmp.RandomFile("src/b.bin", 1000, seed: 2);
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));

        File.Delete(tmp.File("src/a.bin"));
        tmp.RandomFile("src/c.bin", 1000, seed: 3);
        Interrupt.Request();   // as if Ctrl+C was pressed right away
        Assert.Equal(Interrupt.ExitCode, Bdxl("scan", tmp.File("src"), "--name", "S"));
        Assert.False(Interrupt.Requested);   // reset for the next command

        var files = Catalog.Load(tmp.File("cat"), "test").Files;
        Assert.Null(files.Single(f => f.RelPath == "a.bin").MissingSinceUtc);   // not seen is not the same as gone
        Assert.DoesNotContain(files, f => f.RelPath == "c.bin");

        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        files = Catalog.Load(tmp.File("cat"), "test").Files;
        Assert.NotNull(files.Single(f => f.RelPath == "a.bin").MissingSinceUtc);
        Assert.Contains(files, f => f.RelPath == "c.bin");
    }

    int Interrupted(params string[] args)
    {
        Interrupt.Request();
        return Bdxl(args);
    }

    Catalog Cat => Catalog.Load(tmp.File("cat"), "test");

    [Fact]
    public void Interrupted_stage_records_no_empty_disc_and_keeps_the_blank()
    {
        tmp.RandomFile("src/a.bin", 1000);
        Bdxl("blanks", "add", "1", "--size", "1MB");
        Bdxl("scan", tmp.File("src"), "--name", "S");

        Assert.Equal(Interrupt.ExitCode, Interrupted("stage", tmp.File("stg")));
        Assert.Empty(Cat.Discs);
        Assert.Single(Cat.Blanks);
        Assert.False(Directory.Exists(tmp.File("stg/BDXL-0001")));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));   // and it works afterwards
    }

    [Fact]
    public void Interrupted_restage_restore_verify_and_scandisc_change_nothing()
    {
        var a = tmp.RandomFile("src/a.bin", 1000);
        Bdxl("blanks", "add", "1", "--size", "1MB");
        Bdxl("scan", tmp.File("src"), "--name", "S");
        Bdxl("stage", tmp.File("stg"));
        Bdxl("burned", "BDXL-0001");
        string disc = tmp.File("stg/BDXL-0001");

        Assert.Equal(Interrupt.ExitCode, Interrupted("verify", "BDXL-0001", disc));
        Assert.Equal(Interrupt.ExitCode, Interrupted("restore", tmp.File("out"), "*", "--mount", disc));
        Assert.False(File.Exists(tmp.File("out/S/a.bin")));
        Assert.Equal(Interrupt.ExitCode, Interrupted("scandisc", disc, "--label", "OTHER", "--method", "hash"));
        Assert.DoesNotContain(Cat.Discs, d => d.Label == "OTHER");

        Bdxl("lost", "BDXL-0001");
        Assert.Equal(Interrupt.ExitCode, Interrupted("restage", "BDXL-0001", tmp.File("re")));
        Assert.Equal("lost", Cat.Discs.Single().State);
        Assert.False(Directory.Exists(tmp.File("re/BDXL-0001")));

        Assert.Equal(0, Bdxl("restage", "BDXL-0001", tmp.File("re")));   // nothing left half-done
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", disc));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/a.bin")));
    }
}
