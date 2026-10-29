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
}
