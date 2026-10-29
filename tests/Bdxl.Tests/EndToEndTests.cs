namespace Bdxl.Tests;

/// <summary>
/// Runs real commands through Program.Main against a temporary catalog, source folder and "discs" (staged folders
/// used as if they were mounted discs).
/// </summary>
public class EndToEndTests : IDisposable
{
    const string PasswordVar = "BDXL_TEST_PASSWORD";
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    static EndToEndTests() => Environment.SetEnvironmentVariable(PasswordVar, "Correct-Horse-42!");

    int Bdxl(params string[] args) =>
        Program.Main([.. args, "--catalog", tmp.File("cat"), "--password-env", PasswordVar, "--yes"]);

    string Disc(string label) => tmp.File(Path.Combine("stg", label));

    [Fact]
    public void Scan_stage_verify_burn_and_restore()
    {
        var a = tmp.RandomFile("src/docs/a.bin", 300_000, seed: 1);
        File.Copy(tmp.File("src/docs/a.bin"), tmp.File("src/docs/a_copy.bin"), overwrite: true);
        var b = tmp.RandomFile("src/b.bin", 200_000, seed: 2);
        tmp.RandomFile("src/skip.tmp", 10, seed: 3);

        Assert.Equal(0, Bdxl("ignore", "add", "*.tmp"));
        Assert.Equal(0, Bdxl("blanks", "add", "2", "--size", "1MB", "--reserve", "50KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Bdxl("plan"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));

        string disc = Disc("BDXL-0001");
        Assert.True(File.Exists(Path.Combine(disc, "S", "docs", "a.bin")));
        Assert.False(File.Exists(Path.Combine(disc, "S", "docs", "a_copy.bin")));   // dedup: stored once
        Assert.False(File.Exists(Path.Combine(disc, "S", "skip.tmp")));              // ignored
        Assert.True(File.Exists(Path.Combine(disc, "disc.json")));
        Assert.True(File.Exists(Path.Combine(disc, "manifest.jsonl")));                 // plain disc: plain manifest
        Assert.DoesNotContain("encrypted", File.ReadAllText(Path.Combine(disc, "disc.json")));

        Assert.Equal(0, Bdxl("verify", "BDXL-0001", disc));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001", "--date", "2026-10-01"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));   // "nothing to stage" is not an error

        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", disc));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/docs/a.bin")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/docs/a_copy.bin")));
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));
    }

    [Fact]
    public void Scanning_with_several_threads_gives_the_same_catalog_as_one_thread()
    {
        tmp.RandomFile("src/big.bin", 3_000_000, seed: 100);   // finishes last, while the others are hashed beside it
        for (int i = 0; i < 40; i++) tmp.RandomFile($"src/d{i % 3}/f{i}.bin", 1_000 + i * 997, seed: i);

        Assert.Equal(0, Program.Main(["scan", tmp.File("src"), "--name", "S", "--threads", "1", "--catalog", tmp.File("c1")]));
        Assert.Equal(0, Program.Main(["scan", tmp.File("src"), "--name", "S", "--threads", "4", "--catalog", tmp.File("c4")]));

        var one = Catalog.Load(tmp.File("c1"), "test").Files;
        var four = Catalog.Load(tmp.File("c4"), "test").Files;
        Assert.Equal(one.Select(f => (f.RelPath, f.Sha256, f.Size)), four.Select(f => (f.RelPath, f.Sha256, f.Size)));
    }

    [Fact]
    public void A_moved_file_keeps_its_record_and_its_copy()
    {
        var a = tmp.RandomFile("src/docs/a.bin", 100_000);
        tmp.RandomFile("src/b.bin", 50_000, seed: 2);
        Bdxl("blanks", "add", "1", "--size", "1MB");
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));

        Directory.CreateDirectory(tmp.File("src/archive"));
        File.Move(tmp.File("src/docs/a.bin"), tmp.File("src/archive/renamed.bin"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));

        var files = Catalog.Load(tmp.File("cat"), "test").Files;
        Assert.Equal(2, files.Count);                                   // no new record, no record left behind as "gone"
        var moved = files.Single(f => Hashing.SameHash(f.Sha256, Hashing.HashBytes(a)));
        Assert.Equal("archive/renamed.bin", moved.RelPath);
        Assert.Null(moved.MissingSinceUtc);
        Assert.False(Assert.Single(moved.Backups).Alias);              // its own copy, stored as S/docs/a.bin

        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "renamed", "--mount", Disc("BDXL-0001")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/archive/renamed.bin")));
    }

    [Fact]
    public void A_file_moved_between_locations_scanned_separately_is_recognised_once_the_old_one_is_gone()
    {
        var a = tmp.RandomFile("one/a.bin", 100_000);
        Directory.CreateDirectory(tmp.File("two"));
        Bdxl("blanks", "add", "1", "--size", "1MB");
        Assert.Equal(0, Bdxl("scan", tmp.File("one"), "--name", "One"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));

        File.Move(tmp.File("one/a.bin"), tmp.File("two/a.bin"));
        Assert.Equal(0, Bdxl("scan", tmp.File("two"), "--name", "Two"));   // "One" not rescanned: a.bin there is not known to be gone
        Assert.Equal(2, Catalog.Load(tmp.File("cat"), "test").Files.Count);

        Assert.Equal(0, Bdxl("scan", tmp.File("one"), "--name", "One"));
        var record = Assert.Single(Catalog.Load(tmp.File("cat"), "test").Files);
        Assert.Equal(("Two", "a.bin"), (record.RootName, record.RelPath));
        Assert.Null(record.MissingSinceUtc);
        Assert.False(Assert.Single(record.Backups).Alias);
    }

    [Fact]
    public void A_corrupted_disc_fails_verify()
    {
        tmp.RandomFile("src/a.bin", 100_000);
        Bdxl("blanks", "add", "1", "--size", "1MB");
        Bdxl("scan", tmp.File("src"), "--name", "S");
        Bdxl("stage", tmp.File("stg"));

        string stored = Path.Combine(Disc("BDXL-0001"), "S", "a.bin");
        byte[] bytes = File.ReadAllBytes(stored);
        bytes[500] ^= 1;
        File.WriteAllBytes(stored, bytes);

        Assert.Equal(3, Bdxl("verify", "BDXL-0001", Disc("BDXL-0001")));
    }

    [Fact]
    public void Encrypted_split_file_across_three_discs_restores_byte_identical()
    {
        var big = tmp.RandomFile("src/big.bin", 2_500_000, seed: 9);
        Assert.Equal(0, Bdxl("blanks", "add", "3", "--size", "1MB", "--reserve", "50KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));

        Assert.Equal(2, Bdxl("plan"));                    // too big for any disc without --split
        Assert.Equal(0, Bdxl("plan", "--split"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--split", "--encrypt", "--max-discs", "3"));

        var discs = new[] { "BDXL-0001", "BDXL-0002", "BDXL-0003" };
        Assert.All(discs, d => Assert.Equal(0, Bdxl("verify", d, Disc(d), "--deep")));
        Assert.True(File.Exists(Path.Combine(Disc("BDXL-0001"), "data", "000", "000001.enc")));   // hidden name

        // one drive: restore what is there, run again with the next disc, ...
        Assert.Equal(2, Bdxl("restore", tmp.File("out"), "big.bin", "--mount", Disc("BDXL-0002")));
        Assert.False(File.Exists(tmp.File("out/S/big.bin")));
        Assert.Equal(2, Bdxl("restore", tmp.File("out"), "big.bin", "--mount", Disc("BDXL-0003")));
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "big.bin", "--mount", Disc("BDXL-0001")));

        Assert.Equal(big, File.ReadAllBytes(tmp.File("out/S/big.bin")));
        Assert.Empty(Directory.GetFiles(tmp.File("out/S"), "*.bdxl-part.*"));   // the kept parts are cleaned up
    }

    [Fact]
    public void A_lost_disc_is_rebuilt_by_restage()
    {
        tmp.RandomFile("src/a.bin", 100_000);
        Bdxl("blanks", "add", "1", "--size", "1MB");
        Bdxl("scan", tmp.File("src"), "--name", "S");
        Bdxl("stage", tmp.File("stg"));
        Bdxl("burned", "BDXL-0001");

        Assert.Equal(0, Bdxl("lost", "BDXL-0001", "--reason", "test"));
        Assert.Equal(0, Bdxl("restage", "BDXL-0001", tmp.File("re")));

        string rebuilt = Path.Combine(tmp.File("re"), "BDXL-0001");
        Assert.Equal(File.ReadAllBytes(tmp.File("src/a.bin")), File.ReadAllBytes(Path.Combine(rebuilt, "S", "a.bin")));
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", rebuilt));
    }

    [Fact]
    public void A_catalog_edited_by_hand_blocks_commands_until_accepted()
    {
        tmp.RandomFile("src/a.bin", 1000);
        Bdxl("scan", tmp.File("src"), "--name", "S");
        File.AppendAllText(Path.Combine(tmp.File("cat"), "files.jsonl"), "\n");

        Assert.Equal(4, Bdxl("status"));
        Assert.Equal(4, Bdxl("catalog", "check"));
        Assert.Equal(0, Bdxl("catalog", "accept"));
        Assert.Equal(0, Bdxl("status"));
    }

    [Fact]
    public void Every_command_has_help_and_unknown_commands_fail()
    {
        foreach (var cmd in CommandTable.All)
            Assert.Equal(0, Program.Main([cmd.Name, "--help"]));
        Assert.Equal(1, Program.Main(["no-such-command"]));
        Assert.Equal(CommandTable.All.Count, CommandTable.All.Select(c => c.Name).Distinct().Count());
    }
}
