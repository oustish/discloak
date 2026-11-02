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

    int Bdxl(params string[] args)
    {
        TestKeys.Init(tmp.File("cat"));
        return Program.Main([.. args, "--catalog", tmp.File("cat"), "--password-env", PasswordVar, "--yes"]);
    }

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
        Assert.Equal(DiscFormat.PacksFor(500_000), TestKeys.Packs(disc).Count);          // a + b: the copy and skip.tmp are not stored
        Assert.Equal(2, TestKeys.Load(tmp.File("cat")).Discs.Single().FileCount);
        Assert.True(File.Exists(Path.Combine(disc, "disc.json")));
        Assert.True(File.Exists(Path.Combine(disc, "manifest.enc")));

        Assert.Equal(0, Bdxl("verify", "BDXL-0001", disc));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001", "--date", "2026-10-01"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));   // "nothing to stage" is not an error

        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", disc));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/docs/a.bin")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/docs/a_copy.bin")));
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));
    }

    [Fact]
    public void A_disc_of_tiny_files_holds_them_all_in_one_pack_with_its_manifest()
    {
        for (int i = 0; i < 200; i++) tmp.RandomFile($"src/tiny/{i:D3}.txt", 10, seed: i);
        Assert.Equal(0, Bdxl("blanks", "add", "1", "--size", "400KB", "--reserve", "40KB"));   // ~30 KB: folders, disc.json, bdxl.key, README
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));                       // no "will not burn" warning (exit 2)
        Assert.True(DiscSpace.Folder(Disc("BDXL-0001")) <= 400_000);
        Assert.Single(TestKeys.Packs(Disc("BDXL-0001")));                      // 2 KB of files: no sectors or file entries each
        Assert.Equal(200, TestKeys.Load(tmp.File("cat")).Discs.Single().FileCount);

        // and a disc too small for its manifest's lines takes only what fits
        for (int i = 0; i < 2000; i++) tmp.RandomFile($"src/more/{i:D4}.txt", 10, seed: i);
        Assert.Equal(0, Bdxl("blanks", "add", "1", "--size", "300KB", "--reserve", "40KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));
        Assert.True(DiscSpace.Folder(Disc("BDXL-0002")) <= 300_000);
        Assert.InRange(TestKeys.Load(tmp.File("cat")).Discs.Single(d => d.Label == "BDXL-0002").FileCount, 50, 1999);
    }

    [Fact]
    public void Of_discs_equally_full_the_one_with_the_largest_file_is_staged_first()
    {
        // disc 1: a, 100 bytes left; disc 2: b and the small c (too big for disc 1's gap), 50 bytes left - fuller, but
        // only by 50 bytes
        long room = DiscFormat.Room(950_000);
        long Share(string name) => DiscSpace.ManifestShare(Piece.Whole(new FileRecord { RootName = "S", RelPath = name }));
        tmp.RandomFile("src/a.bin", (int)(room - Share("a.bin") - 100), seed: 1);
        tmp.RandomFile("src/b.bin", (int)(room - Share("b.bin") - 3000), seed: 2);
        tmp.RandomFile("src/c.bin", (int)(3000 - Share("c.bin") - 50), seed: 3);
        Assert.Equal(0, Bdxl("blanks", "add", "2", "--size", "1MB", "--reserve", "50KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));
        Assert.Equal("BDXL-0001", TestKeys.Load(tmp.File("cat")).Files.Single(f => f.RelPath == "a.bin").Backups.Single().Disc);
    }

    [Fact]
    public void Scanning_with_several_threads_gives_the_same_catalog_as_one_thread()
    {
        tmp.RandomFile("src/big.bin", 3_000_000, seed: 100);   // finishes last, while the others are hashed beside it
        for (int i = 0; i < 40; i++) tmp.RandomFile($"src/d{i % 3}/f{i}.bin", 1_000 + i * 997, seed: i);

        TestKeys.Init(tmp.File("c1"));
        TestKeys.Init(tmp.File("c4"));
        Assert.Equal(0, Program.Main(["scan", tmp.File("src"), "--name", "S", "--threads", "1", "--catalog", tmp.File("c1")]));
        Assert.Equal(0, Program.Main(["scan", tmp.File("src"), "--name", "S", "--threads", "4", "--catalog", tmp.File("c4")]));

        var one = TestKeys.Load(tmp.File("c1")).Files;
        var four = TestKeys.Load(tmp.File("c4")).Files;
        Assert.Equal(one.Select(f => (f.RelPath, f.Sha256, f.Size)), four.Select(f => (f.RelPath, f.Sha256, f.Size)));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool CreateHardLink(string link, string target, IntPtr security);

    [Fact]
    public void A_file_whose_folder_listing_shows_an_old_size_is_hashed_with_its_real_size()
    {
        if (!OperatingSystem.IsWindows()) return;
        tmp.RandomFile("other/f.bin", 1_000, seed: 1);
        Directory.CreateDirectory(tmp.File("src"));
        Assert.True(CreateHardLink(tmp.File("src/f.bin"), tmp.File("other/f.bin"), IntPtr.Zero));
        using (var fs = new FileStream(tmp.File("other/f.bin"), FileMode.Append)) fs.Write(new byte[50_000]);   // through the other link
        Assert.Equal(1_000, new DirectoryInfo(tmp.File("src")).GetFiles().Single().Length);                     // the listing is behind

        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        var f = TestKeys.Load(tmp.File("cat")).Files.Single();
        Assert.Equal(51_000, f.Size);
        Assert.Equal(Hashing.HashFile(tmp.File("src/f.bin")), f.Sha256);
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

        var files = TestKeys.Load(tmp.File("cat")).Files;
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
        Assert.Equal(2, TestKeys.Load(tmp.File("cat")).Files.Count);

        Assert.Equal(0, Bdxl("scan", tmp.File("one"), "--name", "One"));
        var record = Assert.Single(TestKeys.Load(tmp.File("cat")).Files);
        Assert.Equal(("Two", "a.bin"), (record.RootName, record.RelPath));
        Assert.Null(record.MissingSinceUtc);
        Assert.False(Assert.Single(record.Backups).Alias);
    }

    [Fact]
    public void A_damaged_pack_fails_verify_names_its_files_and_costs_only_them()
    {
        tmp.RandomFile("src/a.bin", 100_000, seed: 1);           // largest first: a in packs 1-2,
        var b = tmp.RandomFile("src/b.bin", 30_000, seed: 2);    // b in pack 3,
        var c = tmp.RandomFile("src/c.bin", 60_000, seed: 3);    // c in packs 2-3
        Bdxl("blanks", "add", "1", "--size", "1MB");
        Bdxl("scan", tmp.File("src"), "--name", "S");
        Bdxl("stage", tmp.File("stg"));
        var copies = TestKeys.Load(tmp.File("cat")).Files.ToDictionary(f => f.RelPath, f => f.Backups.Single());
        Assert.Equal(1, DiscFormat.PackOf(copies["a.bin"].At));

        string pack1 = Path.Combine(Disc("BDXL-0001"), DiscFormat.PackPath(1));
        byte[] bytes = File.ReadAllBytes(pack1);
        bytes[DiscFormat.HeaderSize + 500] ^= 1;   // a.bin's first chunk
        File.WriteAllBytes(pack1, bytes);

        Assert.Equal(3, Bdxl("verify", "BDXL-0001", Disc("BDXL-0001")));
        Assert.Equal(3, Bdxl("verify", "BDXL-0001", Disc("BDXL-0001"), "--deep"));
        Assert.Equal(3, Bdxl("restore", tmp.File("out"), "*", "--mount", Disc("BDXL-0001")));
        Assert.False(File.Exists(tmp.File("out/S/a.bin")));                 // the damaged chunk is a.bin's
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));        // the rest is restored
        Assert.Equal(c, File.ReadAllBytes(tmp.File("out/S/c.bin")));
    }

    [Fact]
    public void A_file_damaged_on_one_disc_is_restored_from_its_copy_on_another()
    {
        var a = tmp.RandomFile("src/a.bin", 50_000, seed: 1);
        Bdxl("blanks", "add", "2", "--size", "1MB");
        Bdxl("scan", tmp.File("src"), "--name", "S");
        Assert.Equal(0, Bdxl("copies", "set", "S", "2"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--max-discs", "2"));
        File.WriteAllBytes(Path.Combine(Disc("BDXL-0001"), DiscFormat.PackPath(1)), new byte[DiscFormat.PackFileSize]);   // unreadable

        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", Disc("BDXL-0001") + ";" + Disc("BDXL-0002")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/a.bin")));
    }

    [Fact]
    public void Restage_takes_intact_packs_from_the_old_disc_and_rebuilds_only_the_damaged_one()
    {
        var a = tmp.RandomFile("src/a.bin", 100_000, seed: 1);   // packs 1-2
        var b = tmp.RandomFile("src/b.bin", 60_000, seed: 2);    // packs 2-3
        Bdxl("blanks", "add", "1", "--size", "1MB");
        Bdxl("scan", tmp.File("src"), "--name", "S");
        Bdxl("stage", tmp.File("stg"));
        Bdxl("burned", "BDXL-0001");
        string old = Disc("BDXL-0001");
        var hashes = TestKeys.Load(tmp.File("cat")).Discs.Single().Packs;
        File.WriteAllBytes(Path.Combine(old, DiscFormat.PackPath(3)), new byte[10]);   // b's last pack damaged on the old disc
        File.Delete(tmp.File("src/a.bin"));                                            // a is only in intact packs 1-2... and pack 2 holds b too

        Assert.Equal(0, Bdxl("lost", "BDXL-0001"));
        Assert.Equal(0, Bdxl("restage", "BDXL-0001", tmp.File("re"), "--from", old));
        var after = TestKeys.Load(tmp.File("cat")).Discs.Single().Packs;
        Assert.Equal(hashes.Take(2), after.Take(2));                                    // taken over as they were
        Assert.NotEqual(hashes[2], after[2]);                                           // rebuilt from b.bin: a new salt
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", tmp.File("re/BDXL-0001"), "--deep"));
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", tmp.File("re/BDXL-0001")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/a.bin")));
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));
    }

    [Fact]
    public void Restage_without_a_source_for_a_damaged_pack_stages_nothing_unless_allowed()
    {
        tmp.RandomFile("src/a.bin", 100_000, seed: 1);   // packs 1-2
        var b = tmp.RandomFile("src/b.bin", 60_000, seed: 2);    // packs 2-3
        Bdxl("blanks", "add", "1", "--size", "1MB");
        Bdxl("scan", tmp.File("src"), "--name", "S");
        Bdxl("stage", tmp.File("stg"));
        Bdxl("burned", "BDXL-0001");
        string old = Disc("BDXL-0001");
        File.Delete(Path.Combine(old, DiscFormat.PackPath(1)));    // a's first pack gone ...
        File.Delete(tmp.File("src/a.bin"));                         // ... and its source too

        Assert.Equal(0, Bdxl("lost", "BDXL-0001"));
        Assert.Equal(3, Bdxl("restage", "BDXL-0001", tmp.File("re"), "--from", old));
        Assert.False(Directory.Exists(tmp.File("re/BDXL-0001")));
        Assert.Equal(0, Bdxl("restage", "BDXL-0001", tmp.File("re"), "--from", old, "--allow-partial"));
        var files = TestKeys.Load(tmp.File("cat")).Files;
        Assert.Empty(files.Single(f => f.RelPath == "a.bin").Backups);   // no longer counts as being on the disc
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", tmp.File("re/BDXL-0001"), "--deep"));
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "b.bin", "--mount", tmp.File("re/BDXL-0001")));
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));
    }

    [Fact]
    public void Encrypted_split_file_across_three_discs_restores_byte_identical()
    {
        var big = tmp.RandomFile("src/big.bin", 2_500_000, seed: 9);
        Assert.Equal(0, Bdxl("blanks", "add", "3", "--size", "1200KB", "--reserve", "50KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));

        Assert.Equal(2, Bdxl("plan"));                    // too big for any disc without --split
        Assert.Equal(0, Bdxl("plan", "--split"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--split", "--max-discs", "3"));

        var discs = new[] { "BDXL-0001", "BDXL-0002", "BDXL-0003" };
        Assert.All(discs, d => Assert.Equal(0, Bdxl("verify", d, Disc(d), "--deep")));
        Assert.All(discs, d => Assert.True(DiscSpace.Folder(Disc(d)) <= 1_200_000));

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
        Assert.Equal(TestKeys.Packs(Disc("BDXL-0001")), TestKeys.Packs(rebuilt));
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", rebuilt));
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", rebuilt, "--deep"));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", rebuilt));
        Assert.Equal(File.ReadAllBytes(tmp.File("src/a.bin")), File.ReadAllBytes(tmp.File("out/S/a.bin")));
    }

    [Fact]
    public void A_catalog_whose_hash_list_was_altered_blocks_commands_until_accepted()
    {
        tmp.RandomFile("src/a.bin", 1000);
        Bdxl("scan", tmp.File("src"), "--name", "S");
        string hashFile = Path.Combine(tmp.File("cat"), Catalog.IntegrityFile);
        var manifest = JsonStore.ReadObject<IntegrityManifest>(hashFile)!;
        manifest.Generation += 100;
        JsonStore.WriteObject(hashFile, manifest);

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
