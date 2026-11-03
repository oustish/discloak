namespace Discloak.Tests;

/// <summary>Interrupt is process-wide, so these tests must not run beside others that run commands.</summary>
[CollectionDefinition(nameof(InterruptTests), DisableParallelization = true)]
public class InterruptCollection { }

[Collection(nameof(InterruptTests))]
public class InterruptTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    int Discloak(params string[] args) => Program.Main([.. args, "--catalog", TestKeys.Init(tmp.File("cat")), "--yes"]);

    [Fact]
    public void An_interrupted_scan_saves_what_it_has_marks_nothing_missing_and_can_be_run_again()
    {
        tmp.RandomFile("src/a.bin", 1000, seed: 1);
        tmp.RandomFile("src/b.bin", 1000, seed: 2);
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));

        File.Delete(tmp.File("src/a.bin"));
        tmp.RandomFile("src/c.bin", 1000, seed: 3);
        Interrupt.Request();   // as if Ctrl+C was pressed right away
        Assert.Equal(Interrupt.ExitCode, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.False(Interrupt.Requested);   // reset for the next command

        var files = TestKeys.Load(tmp.File("cat")).Files;
        Assert.Null(files.Single(f => f.RelPath == "a.bin").MissingSinceUtc);   // not seen is not the same as gone
        Assert.DoesNotContain(files, f => f.RelPath == "c.bin");

        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        files = TestKeys.Load(tmp.File("cat")).Files;
        Assert.NotNull(files.Single(f => f.RelPath == "a.bin").MissingSinceUtc);
        Assert.Contains(files, f => f.RelPath == "c.bin");
    }

    int Interrupted(params string[] args)
    {
        Interrupt.Request();
        return Discloak(args);
    }

    Catalog Cat => TestKeys.Load(tmp.File("cat"));

    [Fact]
    public void Interrupted_stage_records_no_empty_disc_and_keeps_the_blank()
    {
        tmp.RandomFile("src/a.bin", 1000);
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");

        Assert.Equal(Interrupt.ExitCode, Interrupted("stage", tmp.File("stg")));
        Assert.Empty(Cat.Discs);
        Assert.Single(Cat.Blanks);
        Assert.False(Directory.Exists(tmp.File("stg/DISC-0001")));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));   // and it works afterwards
    }

    [Fact]
    public void Interrupted_restage_restore_verify_and_scandisc_change_nothing()
    {
        var a = tmp.RandomFile("src/a.bin", 1000);
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        Discloak("burned", "DISC-0001");
        string disc = tmp.File("stg/DISC-0001");

        Assert.Equal(Interrupt.ExitCode, Interrupted("verify", "DISC-0001", disc));
        Assert.Equal(Interrupt.ExitCode, Interrupted("restore", tmp.File("out"), "*", "--mount", disc));
        Assert.False(File.Exists(tmp.File("out/S/a.bin")));
        Assert.Equal(Interrupt.ExitCode, Interrupted("scandisc", disc, "--update"));
        Assert.DoesNotContain(Cat.Files.SelectMany(f => f.Backups), b => b.Unverified);   // nothing recorded

        Discloak("lost", "DISC-0001");
        Assert.Equal(Interrupt.ExitCode, Interrupted("restage", "DISC-0001", tmp.File("re")));
        Assert.Equal("lost", Cat.Discs.Single().State);
        Assert.False(Directory.Exists(tmp.File("re/DISC-0001")));

        Assert.Equal(0, Discloak("restage", "DISC-0001", tmp.File("re")));   // nothing left half-done
        Assert.Equal(0, Discloak("restore", tmp.File("out"), "*", "--mount", disc));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/a.bin")));
    }

    /// <summary>Console output that presses Ctrl+C when a line with `when` is written, and keeps everything written.</summary>
    sealed class CtrlCWhen(string when) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains(when) == true) Interrupt.Request();
        }
    }

    [Fact]
    public void An_interrupted_restage_continues_with_the_packs_that_hold_only_checked_files()
    {
        tmp.RandomFile("src/a.bin", 100_000, seed: 1);   // packs 1-2
        tmp.RandomFile("src/b.bin", 60_000, seed: 2);    // packs 2-3
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        Discloak("burned", "DISC-0001");
        Discloak("lost", "DISC-0001");

        var console = Console.Out;
        var first = new CtrlCWhen("pack 2 of 3: rebuilt");
        try
        {
            Console.SetOut(first);
            Assert.Equal(Interrupt.ExitCode, Discloak("restage", "DISC-0001", tmp.File("re")));
        }
        finally { Console.SetOut(console); }
        string partial = tmp.File("re/DISC-0001.partial");
        Assert.True(File.Exists(Path.Combine(partial, "restage.json")));
        Assert.Equal("lost", Cat.Discs.Single().State);

        // pack 1 holds only a (checked when it ended in pack 2); pack 2 also holds the start of b, which was not checked yet
        var second = new StringWriter();
        try
        {
            Console.SetOut(second);
            Assert.Equal(0, Discloak("restage", "DISC-0001", tmp.File("re"), "--from", partial));
        }
        finally { Console.SetOut(console); }
        Assert.Contains("pack 1 of 3: taken over", second.ToString());
        Assert.Contains("pack 2 of 3: rebuilt", second.ToString());
        Assert.Equal(0, Discloak("verify", "DISC-0001", tmp.File("re/DISC-0001"), "--deep"));
    }
}
