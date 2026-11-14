namespace Discloak.Tests;

/// <summary>
/// Runs real commands through Program.Main against a temporary catalog, source folder and "discs" (staged folders
/// used as if they were mounted discs). Non-parallel: some tests read what a command prints, and Console.Out is
/// the whole process's.
/// </summary>
[Collection(nameof(InterruptTests))]
public class EndToEndTests : IDisposable
{
    const string PasswordVar = "DISCLOAK_TEST_PASSWORD";
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    static EndToEndTests() => Environment.SetEnvironmentVariable(PasswordVar, "Correct-Horse-42!");

    int Discloak(params string[] args)
    {
        TestKeys.Init(tmp.File("cat"));
        return Program.Main([.. args, "--catalog", tmp.File("cat"), "--password-env", PasswordVar, "--yes"]);
    }

    /// <summary>A command's exit code and what it printed.</summary>
    (int Code, string Text) Run(params string[] args)
    {
        var output = new StringWriter();
        var stdout = Console.Out;
        Console.SetOut(output);
        try { return (Discloak(args), output.ToString()); }
        finally { Console.SetOut(stdout); }
    }

    string Disc(string label) => tmp.File(Path.Combine("stg", label));

    [Fact]
    public void Scan_stage_verify_burn_and_restore()
    {
        var a = tmp.RandomFile("src/docs/a.bin", 300_000, seed: 1);
        File.Copy(tmp.File("src/docs/a.bin"), tmp.File("src/docs/a_copy.bin"), overwrite: true);
        var b = tmp.RandomFile("src/b.bin", 200_000, seed: 2);
        tmp.RandomFile("src/skip.tmp", 10, seed: 3);

        Assert.Equal(0, Discloak("ignore", "add", "*.tmp"));
        Assert.Equal(0, Discloak("blanks", "add", "2", "--size", "1MB", "--reserve", "50KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Discloak("plan"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));

        string disc = Disc("DISC-0001");
        Assert.Equal(DiscFormat.PacksFor(500_000), TestKeys.Packs(disc).Count);          // a + b: the copy and skip.tmp are not stored
        Assert.Equal(2, TestKeys.Load(tmp.File("cat")).Discs.Single().FileCount);
        Assert.True(File.Exists(Path.Combine(disc, "disc.json")));
        Assert.True(File.Exists(Path.Combine(disc, "manifest.enc")));

        Assert.Equal(0, Discloak("verify", "DISC-0001", disc));
        Assert.Equal(0, Discloak("burned", "DISC-0001", "--date", "2026-10-01"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));   // "nothing to stage" is not an error

        Assert.Equal(0, Discloak("restore", tmp.File("out"), "*", "--mount", disc));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/docs/a.bin")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/docs/a_copy.bin")));
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));
    }

    [Fact]
    public void A_disc_of_tiny_files_holds_them_all_in_one_pack_with_its_manifest()
    {
        for (int i = 0; i < 200; i++) tmp.RandomFile($"src/tiny/{i:D3}.txt", 10, seed: i);
        Assert.Equal(0, Discloak("blanks", "add", "1", "--size", "400KB", "--reserve", "40KB"));   // ~30 KB: folders, disc.json, discloak.key, README
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));                       // no "will not burn" warning (exit 2)
        Assert.True(DiscSpace.Folder(Disc("DISC-0001")) <= 400_000);
        Assert.Single(TestKeys.Packs(Disc("DISC-0001")));                      // 2 KB of files: no sectors or file entries each
        Assert.Equal(200, TestKeys.Load(tmp.File("cat")).Discs.Single().FileCount);

        // and a disc too small for its manifest's lines takes only what fits
        for (int i = 0; i < 2000; i++) tmp.RandomFile($"src/more/{i:D4}.txt", 10, seed: i);
        Assert.Equal(0, Discloak("blanks", "add", "1", "--size", "300KB", "--reserve", "40KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));
        Assert.True(DiscSpace.Folder(Disc("DISC-0002")) <= 300_000);
        Assert.InRange(TestKeys.Load(tmp.File("cat")).Discs.Single(d => d.Label == "DISC-0002").FileCount, 50, 1999);
    }

    [Fact]
    public void Of_discs_equally_full_the_one_with_the_largest_file_is_staged_first()
    {
        // disc 1: a, 100 bytes left; disc 2: b and the small c (too big for disc 1's gap), 50 bytes left - fuller, but
        // only by 50 bytes
        long room = DiscFormat.Room(950_000 - DiscProgram.SpaceOnDisc());   // the program folder comes out of every disc
        long Share(string name) => DiscSpace.ManifestShare(Piece.Whole(new FileRecord { RootName = "S", RelPath = name }));
        tmp.RandomFile("src/a.bin", (int)(room - Share("a.bin") - 100), seed: 1);
        tmp.RandomFile("src/b.bin", (int)(room - Share("b.bin") - 3000), seed: 2);
        tmp.RandomFile("src/c.bin", (int)(3000 - Share("c.bin") - 50), seed: 3);
        Assert.Equal(0, Discloak("blanks", "add", "2", "--size", "1MB", "--reserve", "50KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));
        Assert.Equal("DISC-0001", TestKeys.Load(tmp.File("cat")).Files.Single(f => f.RelPath == "a.bin").Backups.Single().Disc);
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

        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        var f = TestKeys.Load(tmp.File("cat")).Files.Single();
        Assert.Equal(51_000, f.Size);
        Assert.Equal(Hashing.HashFile(tmp.File("src/f.bin")), f.Sha256);
    }

    [Fact]
    public void A_moved_file_keeps_its_record_and_its_copy()
    {
        var a = tmp.RandomFile("src/docs/a.bin", 100_000);
        tmp.RandomFile("src/b.bin", 50_000, seed: 2);
        Discloak("blanks", "add", "1", "--size", "1MB");
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));
        Assert.Equal(0, Discloak("burned", "DISC-0001"));

        Directory.CreateDirectory(tmp.File("src/archive"));
        File.Move(tmp.File("src/docs/a.bin"), tmp.File("src/archive/renamed.bin"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));

        var files = TestKeys.Load(tmp.File("cat")).Files;
        Assert.Equal(2, files.Count);                                   // no new record, no record left behind as "gone"
        var moved = files.Single(f => Hashing.SameHash(f.Sha256, Hashing.HashBytes(a)));
        Assert.Equal("archive/renamed.bin", moved.RelPath);
        Assert.Null(moved.MissingSinceUtc);
        Assert.False(Assert.Single(moved.Backups).Alias);              // its own copy, stored as S/docs/a.bin

        Assert.Equal(0, Discloak("restore", tmp.File("out"), "renamed", "--mount", Disc("DISC-0001")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/archive/renamed.bin")));
    }

    [Fact]
    public void A_file_moved_between_locations_scanned_separately_is_recognised_once_the_old_one_is_gone()
    {
        var a = tmp.RandomFile("one/a.bin", 100_000);
        Directory.CreateDirectory(tmp.File("two"));
        Discloak("blanks", "add", "1", "--size", "1MB");
        Assert.Equal(0, Discloak("scan", tmp.File("one"), "--name", "One"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));
        Assert.Equal(0, Discloak("burned", "DISC-0001"));

        File.Move(tmp.File("one/a.bin"), tmp.File("two/a.bin"));
        Assert.Equal(0, Discloak("scan", tmp.File("two"), "--name", "Two"));   // "One" not rescanned: a.bin there is not known to be gone
        Assert.Equal(2, TestKeys.Load(tmp.File("cat")).Files.Count);

        Assert.Equal(0, Discloak("scan", tmp.File("one"), "--name", "One"));
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
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        var copies = TestKeys.Load(tmp.File("cat")).Files.ToDictionary(f => f.RelPath, f => f.Backups.Single());
        Assert.Equal(1, DiscFormat.PackOf(copies["a.bin"].At));

        string pack1 = Path.Combine(Disc("DISC-0001"), DiscFormat.PackPath(1));
        byte[] bytes = File.ReadAllBytes(pack1);
        bytes[DiscFormat.HeaderSize + 500] ^= 1;   // a.bin's first chunk
        File.WriteAllBytes(pack1, bytes);

        Assert.Equal(3, Discloak("verify", "DISC-0001", Disc("DISC-0001")));
        Assert.Equal(3, Discloak("verify", "DISC-0001", Disc("DISC-0001"), "--deep"));
        Assert.Equal(3, Discloak("restore", tmp.File("out"), "*", "--mount", Disc("DISC-0001")));
        Assert.False(File.Exists(tmp.File("out/S/a.bin")));                 // the damaged chunk is a.bin's
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));        // the rest is restored
        Assert.Equal(c, File.ReadAllBytes(tmp.File("out/S/c.bin")));
    }

    [Fact]
    public void Verify_says_what_it_checks_names_every_file_with_deep_and_reports_damage_as_it_is_read()
    {
        tmp.RandomFile("src/a.bin", 100_000, seed: 1);   // packs 1-2
        tmp.RandomFile("src/b.bin", 30_000, seed: 2);
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        string disc = Disc("DISC-0001");
        string Verify(params string[] more) => Run(["verify", "DISC-0001", disc, .. more]).Text;

        string plain = Verify();
        Assert.Contains($"Verifying DISC-0001 in {disc}: ", plain);
        Assert.Contains("nothing is decrypted", plain);
        Assert.DoesNotContain("S/a.bin", plain);                      // the files only show in the status line
        string deep = Verify("--deep");
        Assert.Contains("--deep: every pack is hashed and decrypted", deep);
        Assert.Contains("\n  OK       S/a.bin (100,000 bytes)", deep.ReplaceLineEndings("\n"));   // no status in front of a checked file
        Assert.Contains("\n  OK       S/b.bin (30,000 bytes)", deep.ReplaceLineEndings("\n"));

        string pack1 = Path.Combine(disc, DiscFormat.PackPath(1));
        byte[] bytes = File.ReadAllBytes(pack1);
        bytes[DiscFormat.HeaderSize + 500] ^= 1;
        File.WriteAllBytes(pack1, bytes);
        deep = Verify("--deep");
        Assert.True(deep.IndexOf("  DAMAGED  pack 1 differs from the catalog", StringComparison.Ordinal) is >= 0 and var at &&
                    at < deep.IndexOf("S/a.bin (", StringComparison.Ordinal));   // as soon as the pack is read
        Assert.Contains("  DAMAGED  S/a.bin (100,000 bytes): pack 1, chunk 0 is damaged", deep);
        Assert.Contains("Damaged pack(s): 1", deep);
        Assert.Contains("DAMAGED  pack 1 differs from the catalog", Verify());
        Assert.Contains("DAMAGED  pack 1 differs from the catalog", Verify("--no-cache"));                 // read past the cache
        Assert.Contains("  DAMAGED  S/a.bin (100,000 bytes): pack 1, chunk 0 is damaged", Verify("--deep", "--no-cache"));
    }

    /// <summary>Three packs: a.bin in packs 1-2, c.bin in 2-3, b.bin in 3 (largest first). Returns the disc's folder.</summary>
    string ThreePackDisc()
    {
        tmp.RandomFile("src/a.bin", 100_000, seed: 1);
        tmp.RandomFile("src/b.bin", 30_000, seed: 2);
        tmp.RandomFile("src/c.bin", 60_000, seed: 3);
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        return Disc("DISC-0001");
    }

    /// <summary>Flips a bit in chunk 4 of pack 3 (b.bin's data); returns how to put it back.</summary>
    static Action DamageB(string disc)
    {
        string pack3 = Path.Combine(disc, DiscFormat.PackPath(3));
        byte[] before = File.ReadAllBytes(pack3), bytes = (byte[])before.Clone();
        bytes[DiscFormat.HeaderSize + 4 * (TestKeys.ChunkSize + DiscFormat.TagSize) + 100] ^= 1;
        File.WriteAllBytes(pack3, bytes);
        return () => File.WriteAllBytes(pack3, before);
    }

    [Fact]
    public void Verify_packs_checks_only_the_packs_given_and_with_deep_the_files_in_them()
    {
        string disc = ThreePackDisc();
        string Lines(string text) => text.ReplaceLineEndings("\n");

        var (code, text) = Run("verify", "DISC-0001", disc, "--packs", "3");
        Assert.Equal(0, code);
        Assert.Contains("--packs: only pack(s) 3, 2 file(s) with data in them", text);
        Assert.Contains("DISC-0001: 1 of 1 pack(s) given OK, 0 damaged", text);

        (code, text) = Run("verify", "DISC-0001", disc, "--packs", "3", "--deep");
        Assert.Equal(0, code);
        Assert.Contains("\n  OK       S/b.bin (30,000 bytes)\n", Lines(text));   // wholly in pack 3: checked against its hash
        Assert.Contains("  OK       S/c.bin (60,000 bytes), its data in pack(s) 3 (the file spans packs 2-3: not checked against its hash)", text);
        Assert.DoesNotContain("S/a.bin", text);
        Assert.Contains("2 of 2 file(s) checked intact (1 of them only their part in these packs)", text);

        var restore = DamageB(disc);
        (code, text) = Run("verify", "DISC-0001", disc, "--packs", "2-3", "--deep");
        Assert.Equal(3, code);
        Assert.Contains("  DAMAGED  S/b.bin (30,000 bytes): pack 3, chunk 4 is damaged", text);
        Assert.Contains("  OK       S/c.bin (60,000 bytes)\n", Lines(text));   // wholly in packs 2-3 now
        Assert.Contains("DISC-0001: 1 of 2 pack(s) given OK, 1 damaged", text);
        // not marked as burned yet: burn the damaged pack again from the staging folder
        Assert.Contains("DISC-0001 is not marked as burned yet", text);
        Assert.Contains("  " + DiscFormat.PackPath(3).Replace('/', Path.DirectorySeparatorChar) + "\n", Lines(text));
        Assert.Contains($"discloak verify DISC-0001 {disc} --packs 3 --deep", text);
        Assert.Equal(0, Discloak("verify", "DISC-0001", disc, "--packs", "1"));        // the other packs are fine
        Assert.Equal(0, Discloak("verify", "DISC-0001", disc, "--packs", "1,2", "--deep"));
        restore();

        Assert.NotEqual(0, Discloak("verify", "DISC-0001", disc, "--packs", "4"));
        Assert.Contains("there is no pack 4", Assert.Throws<UserException>(() => VerifyCommand.ParsePacks("4", 3)).Message);
        Assert.Contains("'x' is not a pack number", Assert.Throws<UserException>(() => VerifyCommand.ParsePacks("1,x", 3)).Message);
        Assert.Contains("'3-1' is not a pack number", Assert.Throws<UserException>(() => VerifyCommand.ParsePacks("3-1", 3)).Message);
        Assert.Equal([1, 2, 3], VerifyCommand.ParsePacks(" 3, 1-2 ,2", 3));
        Assert.Equal("1-3,5,7-8", VerifyCommand.Ranges([8, 1, 2, 3, 5, 7]));
    }

    [Fact]
    public void Verify_mark_burned_marks_the_disc_only_when_everything_checks_out()
    {
        string disc = ThreePackDisc();
        DiscRecord Record() => TestKeys.Load(tmp.File("cat")).Discs.Single();

        var restore = DamageB(disc);
        var (code, text) = Run("verify", "DISC-0001", disc, "--mark-burned");
        Assert.Equal(3, code);
        Assert.Contains("Not marked as burned (--mark-burned): not everything checked out.", text);
        Assert.Equal("staged", Record().State);
        Assert.NotEqual(0, Discloak("verify", "DISC-0001", disc, "--packs", "1", "--mark-burned"));   // "leave out --packs"
        Assert.Equal("staged", Record().State);
        restore();

        (code, text) = Run("verify", "DISC-0001", disc, "--deep", "--mark-burned");
        Assert.Equal(0, code);
        Assert.Contains("DISC-0001 marked as burned on ", text);
        Assert.Equal("burned", Record().State);
        Assert.All(TestKeys.Load(tmp.File("cat")).Files.SelectMany(f => f.Backups), b => Assert.NotNull(b.BackedUpUtc));
        Assert.Contains("DISC-0001 is already marked as burned.", Run("verify", "DISC-0001", disc, "--mark-burned").Text);

        // burned: the advice is about the copy that the catalog still counts
        restore = DamageB(disc);
        text = Run("verify", "DISC-0001", disc).Text;
        Assert.Contains("Restore these files from another copy if they have one", text);
        Assert.DoesNotContain("not marked as burned yet", text);
        restore();
    }

    [Fact]
    public void Verify_records_damaged_copies_which_stop_counting_and_a_later_pass_removes_the_mark_with_a_warning()
    {
        string disc = ThreePackDisc();
        Catalog Cat() => TestKeys.Load(tmp.File("cat"));
        BackupRef CopyOf(string name) => Cat().Files.Single(f => f.RelPath == name).Backups.Single();
        int Pending() { var cat = Cat(); return new Archive(cat).MakePlan().PendingFiles; }
        Assert.Equal(0, Discloak("verify", "DISC-0001", disc, "--mark-burned"));
        Assert.Equal(0, Pending());

        // a plain verify: every file with data in the damaged pack 3 (b, and c which starts in pack 2)
        var restore = DamageB(disc);
        var (code, text) = Run("verify", "DISC-0001", disc);
        Assert.Equal(3, code);
        Assert.Contains("Recorded in the catalog: 2 file copy(ies) on DISC-0001 are damaged", text);
        Assert.Equal("pack 3 differs from the catalog", CopyOf("b.bin").Damage);
        Assert.NotNull(CopyOf("c.bin").DamagedUtc);
        Assert.Null(CopyOf("a.bin").DamagedUtc);
        Assert.Equal(2, Pending());                                              // on the source: pending again
        Assert.Equal(0, Archive.BurnedCopies(Cat().Files.Single(f => f.RelPath == "b.bin"), []));
        Assert.Contains("WARNING: 2 file copies are recorded as damaged by 'verify', on DISC-0001", Run("status").Text);
        Assert.Contains("[DAMAGED, found by verify on ", Run("find", "b.bin").Text);
        Assert.Contains("stay recorded as damaged", Run("verify", "DISC-0001", disc).Text);   // again: nothing new

        // --deep is precise: c decrypts intact (the damage is in b's chunk), so its mark goes, with the warning
        (code, text) = Run("verify", "DISC-0001", disc, "--deep");
        Assert.Equal(3, code);
        Assert.Null(CopyOf("c.bin").DamagedUtc);
        Assert.Contains("WARNING  1 file copy(ies) on DISC-0001 recorded as damaged read back intact now", text);
        Assert.Contains("  S/c.bin  (recorded as damaged on ", text);
        Assert.Equal("pack 3, chunk 4 is damaged", CopyOf("b.bin").Damage);   // the reason, made precise
        Assert.Equal(1, Pending());

        // a check of other packs changes nothing; one that reads pack 3 right again removes the last mark
        restore();
        Assert.Equal(0, Discloak("verify", "DISC-0001", disc, "--packs", "1-2"));
        Assert.NotNull(CopyOf("b.bin").DamagedUtc);
        (code, text) = Run("verify", "DISC-0001", disc, "--packs", "3");
        Assert.Equal(0, code);
        Assert.Contains("WARNING  1 file copy(ies) on DISC-0001 recorded as damaged read back intact now", text);
        Assert.Contains("unreliable reads", text);
        Assert.All(Cat().Files.SelectMany(f => f.Backups), b => Assert.False(b.IsDamaged));
        Assert.Equal(0, Pending());
        Assert.DoesNotContain("recorded as damaged", Run("status").Text);

        // restage rebuilds the disc: its copies are no longer damaged
        restore = DamageB(disc);
        Discloak("verify", "DISC-0001", disc);
        Assert.NotNull(CopyOf("b.bin").DamagedUtc);
        Assert.Equal(0, Discloak("restage", "DISC-0001", tmp.File("re")));
        Assert.All(Cat().Files.SelectMany(f => f.Backups), b => Assert.False(b.IsDamaged));
        restore();
    }

    [Fact]
    public void Verify_checks_the_discs_other_files_and_its_catalog_backup()
    {
        tmp.RandomFile("src/a.bin", 100_000, seed: 1);
        tmp.RandomFile("src/b.bin", 30_000, seed: 2);
        Discloak("blanks", "add", "1", "--size", "2MB", "--reserve", "600KB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"), "--with-catalog");
        string disc = Disc("DISC-0001");

        var (code, text) = Run("verify", "DISC-0001", disc);
        Assert.Equal(0, code);
        foreach (string ok in new[] { "OK       disc.json", "OK       packs.sha256 (2 packs, as in the catalog)", "OK       manifest.enc (SHA-256 as in the catalog)",
                                      "OK       discloak.key (this catalog's", "OK       README.txt", "OK       _catalog (" })
            Assert.Contains(ok, text);
        Assert.Contains("the disc's other files OK", text);
        Assert.Contains("OK       the manifests list the 2 file(s) the catalog has on this disc", Run("verify", "DISC-0001", disc, "--deep").Text);
        (code, text) = Run("verify", "DISC-0001", disc, "--deep", "--no-cache");
        Assert.Equal(0, code);
        Assert.Contains("--no-cache: every byte is read from the disc itself", text);
        Assert.Contains("OK       _catalog (", text);

        // each damage is found (by a plain verify: no decrypting; also reading past the cache), then the file is put back
        void Damaged(string file, Action<string> damage, string expected)
        {
            string path = Path.Combine(disc, file);
            byte[] before = File.ReadAllBytes(path);
            damage(path);
            foreach (string[] more in new[] { Array.Empty<string>(), ["--no-cache"] })
            {
                var (code, text) = Run(["verify", "DISC-0001", disc, .. more]);
                Assert.Equal(3, code);
                Assert.Contains(expected, text);
                Assert.Contains("1 of the disc's other files with a problem", text);
            }
            File.WriteAllBytes(path, before);
        }
        void Flip(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 1;
            File.WriteAllBytes(path, bytes);
        }
        Damaged(DiscFiles.ManifestName, Flip, "PROBLEM  manifest.enc: differs from the catalog");
        string pack2 = TestKeys.Load(tmp.File("cat")).Discs[0].Packs[1].ToLowerInvariant();
        Damaged(DiscFiles.PackListName, p => File.WriteAllText(p, File.ReadAllText(p).Replace(pack2, new string('0', 64))),
                "PROBLEM  packs.sha256: 1 of the 2 pack hashes are missing or differ from the catalog");
        Damaged(DiscFiles.InfoName, p => File.WriteAllText(p, "{"), "PROBLEM  disc.json: cannot be read");
        Damaged(KeyFile.FileName, File.Delete, "PROBLEM  discloak.key: is missing");
        Damaged(DiscFiles.ReadmeName, File.Delete, "PROBLEM  README.txt: is missing");
        Damaged("_catalog/discloak.key", File.Delete, "PROBLEM  _catalog: 1 file(s) damaged, but the repair data can rebuild them");
        Damaged("_catalog/catalog.hash.json.par", File.Delete, "PROBLEM  _catalog: 1 file(s) damaged, but the repair data can rebuild them");
        Assert.Equal(0, Discloak("verify", "DISC-0001", disc, "--deep"));   // all put back
    }

    [Fact]
    public void A_disc_without_manifest_hashes_gets_them_from_its_first_deep_verify_which_also_compares_the_manifests_with_the_catalog()
    {
        tmp.RandomFile("src/a.bin", 100_000, seed: 1);
        tmp.RandomFile("src/b.bin", 30_000, seed: 2);
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        string disc = Disc("DISC-0001");
        var cat = TestKeys.Load(tmp.File("cat"));
        Assert.Single(cat.Discs[0].Manifests);
        cat.Discs[0].Manifests.Clear();   // as on a disc staged before the hashes were kept
        cat.Save();

        Assert.Contains("OK       manifest.enc (readable; the catalog has no hash of it: --deep checks it)", Run("verify", "DISC-0001", disc).Text);
        var (code, text) = Run("verify", "DISC-0001", disc, "--deep");
        Assert.Equal(0, code);
        Assert.Contains("OK       manifest.enc (decrypts: 2 file(s))", text);
        Assert.Contains("their SHA-256 is now in the catalog", text);
        Assert.Contains("OK       manifest.enc (SHA-256 as in the catalog)", Run("verify", "DISC-0001", disc).Text);

        cat = TestKeys.Load(tmp.File("cat"));
        cat.Files.Single(f => f.RelPath == "b.bin").Backups.Clear();   // the catalog no longer has b on the disc
        cat.Save();
        (code, text) = Run("verify", "DISC-0001", disc, "--deep");
        Assert.Equal(3, code);
        Assert.Contains("PROBLEM  the manifests: list 1 file(s) the catalog does not have on this disc", text);
    }

    [Fact]
    public void Stage_restore_and_restage_show_their_progress()
    {
        tmp.RandomFile("src/a.bin", 100_000, seed: 1);
        tmp.RandomFile("src/b.bin", 30_000, seed: 2);
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");

        string stage = Run("stage", tmp.File("stg")).Text;
        Assert.Contains($"DISC-0001: 2 file(s), 130,000 bytes to store in {Disc("DISC-0001")}", stage);
        Assert.Matches(@"\[1/2 +\d+\.\d% .*\] S/a\.bin \(100,000 bytes\)", stage);
        Assert.Matches(@"\[2/2 +100\.0% .*\] S/b\.bin \(30,000 bytes\)", stage);
        Discloak("burned", "DISC-0001");

        string restore = Run("restore", tmp.File("out"), "*", "--mount", Disc("DISC-0001")).Text;
        Assert.Contains($"Restoring 2 file(s) into {tmp.File("out")}: 130,000 bytes to read from DISC-0001 ({Disc("DISC-0001")})", restore);
        Assert.Matches(@"\[1/2 +\d+\.\d% .*\] restored S/a\.bin", restore);
        Assert.Matches(@"\[2/2 +100\.0% .*\] restored S/b\.bin", restore);
        Assert.Matches(@"\[1/2 +0\.0% .*\] EXISTS   S/a\.bin", Run("restore", tmp.File("out"), "*", "--mount", Disc("DISC-0001")).Text);

        Discloak("lost", "DISC-0001");
        string restage = Run("restage", "DISC-0001", tmp.File("re")).Text;
        Assert.Contains($"Restaging DISC-0001 into {Path.Combine(tmp.File("re"), "DISC-0001")}: 2 pack(s)", restage);
        Assert.Matches(@"pack 1 of 2: rebuilt +50\.0% ", restage);
        Assert.Matches(@"pack 2 of 2: rebuilt +100\.0% ", restage);
    }

    [Fact]
    public void Version_names_the_build_and_every_staged_disc_records_it()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+\+([0-9a-f]{7,}(-dirty)?\.)?\d{8}T\d{4}Z$", BuildInfo.Full);   // semver build metadata
        var (code, text) = Run("version");
        Assert.Equal(0, code);
        Assert.Matches(@"^discloak \d+\.\d+\.\d+ \((Debug|Release)\)\r?\n", text);
        Assert.Matches(@"\ncommit ([0-9a-f]{7,}\*? on .+, built \d{4}-\d\d-\d\d \d\d:\d\d UTC|unknown)", text);
        Assert.Contains($"full version: {BuildInfo.Full}", text);
        Assert.Contains("Copyright 2026 oustish (https://github.com/oustish). License: PolyForm Shield 1.0.0", text);
        Assert.Equal(text, Run("--version").Text);

        tmp.RandomFile("src/a.bin", 30_000, seed: 1);
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        Assert.Equal(BuildInfo.CreatedBy, DiscFiles.ReadInfo(Disc("DISC-0001"))!.CreatedBy);
        Assert.Equal([BuildInfo.CreatedBy], TestKeys.Load(tmp.File("cat")).Discs.Single().WrittenBy);
        Assert.Contains($"by {BuildInfo.CreatedBy}", Run("discs").Text);

        var cat = TestKeys.Load(tmp.File("cat"));
        cat.Discs.Single().WrittenBy.Clear();   // as on a disc staged before 0.6.0
        cat.Save();
        Discloak("lost", "DISC-0001");
        Discloak("restage", "DISC-0001", tmp.File("re"));
        Assert.Equal([BuildInfo.CreatedBy], TestKeys.Load(tmp.File("cat")).Discs.Single().WrittenBy);   // the build that rebuilt it
    }

    [Theory]
    [InlineData(4, 5)]   // the 4th disc is burned: the next is the 5th (1-3 below it are not reused)
    [InlineData(1, 2)]   // the 1st is burned: 2 is free again
    public void Discard_gives_numbers_back_down_to_the_highest_disc_still_in_the_catalog(int burned, int next)
    {
        for (int i = 1; i <= 4; i++) tmp.RandomFile($"src/{i}.bin", 600_000 - i * 1000, seed: i);   // one per disc
        Discloak("blanks", "add", "4", "--size", "1MB", "--reserve", "100KB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Assert.Equal(0, Discloak("stage", tmp.File("stg"), "--max-discs", "4"));
        Assert.Equal(4, TestKeys.Load(tmp.File("cat")).Discs.Count);
        Assert.Equal(0, Discloak("burned", $"DISC-000{burned}"));
        foreach (int n in Enumerable.Range(1, 4).Where(n => n != burned))
            Assert.Equal(0, Discloak("discard", $"DISC-000{n}"));

        Assert.Equal(0, Discloak("stage", tmp.File("stg2")));
        Assert.Equal([$"DISC-000{burned}", $"DISC-000{next}"], TestKeys.Load(tmp.File("cat")).Discs.Select(d => d.Label).Order());
    }

    [Fact]
    public void Cost_and_discs_line_up_labels_of_any_length()
    {
        tmp.RandomFile("src/a.bin", 30_000, seed: 1);
        Discloak("blanks", "add", "2", "--size", "1MB", "--cost", "45");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));                                        // DISC-0001
        Discloak("settings", "set", "label-prefix", "DISC-ARCHIVE-");
        tmp.RandomFile("src/b.bin", 20_000, seed: 2);
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));                                        // DISC-ARCHIVE-0002

        var cost = Run("cost", "report").Text.Split('\n').Select(l => l.TrimEnd('\r')).SkipWhile(l => !l.StartsWith("Disc")).Take(3).ToList();
        Assert.Equal(["Disc", "DISC-0001", "DISC-ARCHIVE-0002"], cost.Select(l => l.Split(' ')[0]));
        Assert.All(cost, l => Assert.Equal(cost[0].IndexOf("State"), l.IndexOf(l == cost[0] ? "State" : "staged")));
        Assert.All(cost, l => Assert.Equal(cost[0].Length, l.Length));       // the amounts end in one column

        var discs = Run("discs").Text.Split('\n').Where(l => l.StartsWith("DISC-")).ToList();
        Assert.Equal(2, discs.Count);
        Assert.Equal(discs[0].IndexOf("staged"), discs[1].IndexOf("staged"));
    }

    [Fact]
    public void A_file_damaged_on_one_disc_is_restored_from_its_copy_on_another()
    {
        var a = tmp.RandomFile("src/a.bin", 50_000, seed: 1);
        Discloak("blanks", "add", "2", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Assert.Equal(0, Discloak("copies", "set", "S", "2"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg"), "--max-discs", "2"));
        File.WriteAllBytes(Path.Combine(Disc("DISC-0001"), DiscFormat.PackPath(1)), new byte[DiscFormat.PackFileSize]);   // unreadable

        Assert.Equal(0, Discloak("restore", tmp.File("out"), "*", "--mount", Disc("DISC-0001") + ";" + Disc("DISC-0002")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/a.bin")));
    }

    [Fact]
    public void Restage_takes_intact_packs_from_the_old_disc_and_rebuilds_only_the_damaged_one()
    {
        var a = tmp.RandomFile("src/a.bin", 100_000, seed: 1);   // packs 1-2
        var b = tmp.RandomFile("src/b.bin", 60_000, seed: 2);    // packs 2-3
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        Discloak("burned", "DISC-0001");
        string old = Disc("DISC-0001");
        var hashes = TestKeys.Load(tmp.File("cat")).Discs.Single().Packs;
        File.WriteAllBytes(Path.Combine(old, DiscFormat.PackPath(3)), new byte[10]);   // b's last pack damaged on the old disc
        File.Delete(tmp.File("src/a.bin"));                                            // a is only in intact packs 1-2... and pack 2 holds b too

        Assert.Equal(0, Discloak("lost", "DISC-0001"));
        Assert.Equal(0, Discloak("restage", "DISC-0001", tmp.File("re"), "--from", old));
        var after = TestKeys.Load(tmp.File("cat")).Discs.Single().Packs;
        Assert.Equal(hashes.Take(2), after.Take(2));                                    // taken over as they were
        Assert.NotEqual(hashes[2], after[2]);                                           // rebuilt from b.bin: a new salt
        Assert.Equal(0, Discloak("verify", "DISC-0001", tmp.File("re/DISC-0001"), "--deep"));
        Assert.Equal(0, Discloak("restore", tmp.File("out"), "*", "--mount", tmp.File("re/DISC-0001")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/a.bin")));
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));
    }

    [Fact]
    public void Restage_without_a_source_for_a_damaged_pack_stages_nothing_unless_allowed()
    {
        tmp.RandomFile("src/a.bin", 100_000, seed: 1);   // packs 1-2
        var b = tmp.RandomFile("src/b.bin", 60_000, seed: 2);    // packs 2-3
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        Discloak("burned", "DISC-0001");
        string old = Disc("DISC-0001");
        File.Delete(Path.Combine(old, DiscFormat.PackPath(1)));    // a's first pack gone ...
        File.Delete(tmp.File("src/a.bin"));                         // ... and its source too

        Assert.Equal(0, Discloak("lost", "DISC-0001"));
        Assert.Equal(3, Discloak("restage", "DISC-0001", tmp.File("re"), "--from", old));
        Assert.False(Directory.Exists(tmp.File("re/DISC-0001")));
        Assert.Equal(0, Discloak("restage", "DISC-0001", tmp.File("re"), "--from", old, "--allow-partial"));
        var files = TestKeys.Load(tmp.File("cat")).Files;
        Assert.Empty(files.Single(f => f.RelPath == "a.bin").Backups);   // no longer counts as being on the disc
        Assert.Equal(0, Discloak("verify", "DISC-0001", tmp.File("re/DISC-0001"), "--deep"));
        Assert.Equal(0, Discloak("restore", tmp.File("out"), "b.bin", "--mount", tmp.File("re/DISC-0001")));
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));
    }

    [Fact]
    public void Encrypted_split_file_across_three_discs_restores_byte_identical()
    {
        var big = tmp.RandomFile("src/big.bin", 2_500_000, seed: 9);
        Assert.Equal(0, Discloak("blanks", "add", "3", "--size", "1200KB", "--reserve", "50KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "S"));

        Assert.Equal(2, Discloak("plan"));                    // too big for any disc without --split
        Assert.Equal(0, Discloak("plan", "--split"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg"), "--split", "--max-discs", "3"));

        var discs = new[] { "DISC-0001", "DISC-0002", "DISC-0003" };
        Assert.All(discs, d => Assert.Equal(0, Discloak("verify", d, Disc(d), "--deep")));
        Assert.All(discs, d => Assert.True(DiscSpace.Folder(Disc(d)) <= 1_200_000));

        // one drive: restore what is there, run again with the next disc, ...
        Assert.Equal(2, Discloak("restore", tmp.File("out"), "big.bin", "--mount", Disc("DISC-0002")));
        Assert.False(File.Exists(tmp.File("out/S/big.bin")));
        Assert.Equal(2, Discloak("restore", tmp.File("out"), "big.bin", "--mount", Disc("DISC-0003")));
        Assert.Equal(0, Discloak("restore", tmp.File("out"), "big.bin", "--mount", Disc("DISC-0001")));

        Assert.Equal(big, File.ReadAllBytes(tmp.File("out/S/big.bin")));
        Assert.Empty(Directory.GetFiles(tmp.File("out/S"), "*.discloak-part.*"));   // the kept parts are cleaned up
    }

    [Fact]
    public void A_lost_disc_is_rebuilt_by_restage()
    {
        tmp.RandomFile("src/a.bin", 100_000);
        Discloak("blanks", "add", "1", "--size", "1MB");
        Discloak("scan", tmp.File("src"), "--name", "S");
        Discloak("stage", tmp.File("stg"));
        Discloak("burned", "DISC-0001");

        Assert.Equal(0, Discloak("lost", "DISC-0001", "--reason", "test"));
        Assert.Equal(0, Discloak("restage", "DISC-0001", tmp.File("re")));

        string rebuilt = Path.Combine(tmp.File("re"), "DISC-0001");
        Assert.Equal(TestKeys.Packs(Disc("DISC-0001")), TestKeys.Packs(rebuilt));
        Assert.Equal(0, Discloak("verify", "DISC-0001", rebuilt));
        Assert.Equal(0, Discloak("verify", "DISC-0001", rebuilt, "--deep"));
        Assert.Equal(0, Discloak("burned", "DISC-0001"));
        Assert.Equal(0, Discloak("restore", tmp.File("out"), "*", "--mount", rebuilt));
        Assert.Equal(File.ReadAllBytes(tmp.File("src/a.bin")), File.ReadAllBytes(tmp.File("out/S/a.bin")));
    }

    [Fact]
    public void A_catalog_whose_hash_list_was_altered_blocks_commands_until_accepted()
    {
        tmp.RandomFile("src/a.bin", 1000);
        Discloak("scan", tmp.File("src"), "--name", "S");
        string hashFile = Path.Combine(tmp.File("cat"), Catalog.IntegrityFile);
        var manifest = JsonStore.ReadObject<IntegrityManifest>(hashFile)!;
        manifest.Generation += 100;
        JsonStore.WriteObject(hashFile, manifest);

        Assert.Equal(4, Discloak("status"));
        Assert.Equal(4, Discloak("catalog", "check"));
        Assert.Equal(0, Discloak("catalog", "accept"));
        Assert.Equal(0, Discloak("status"));
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
