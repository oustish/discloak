namespace Bdxl.Tests;

/// <summary>
/// stage --append: a burned disc that is not full gets one more session with only the new/changed files, in packs
/// that continue the disc's numbering, with a manifest of its own, packs.sha256 and disc.json. Burning a session is
/// simulated by copying its folder over the disc's folder (a multisession burn that continues the disc's file system
/// shows exactly that merged view).
/// </summary>
public class AppendTests : IDisposable
{
    const string PasswordVar = "BDXL_TEST_PASSWORD";
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    static AppendTests()
    {
        Environment.SetEnvironmentVariable(PasswordVar, "Correct-Horse-42!");
    }

    int BdxlIn(string catalog, string passwordVar, params string[] args)
    {
        TestKeys.Init(tmp.File(catalog));
        return Program.Main([.. args, "--catalog", tmp.File(catalog), "--password-env", passwordVar, "--yes"]);
    }

    int Bdxl(params string[] args) => BdxlIn("cat", PasswordVar, args);

    Catalog Cat(string name = "cat") => TestKeys.Load(tmp.File(name));
    DiscRecord Disc1 => Cat().Discs.Single(d => d.Label == "BDXL-0001");
    string DiscDir => tmp.File(Path.Combine("stg", "BDXL-0001"));
    string Session2 => tmp.File(Path.Combine("stg", "BDXL-0001.s2"));

    static List<string> FilesIn(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    /// <summary>The metadata of a session folder and its packs numbered from..to.</summary>
    static List<string> SessionFiles(int session, int from, int to, params string[] more) =>
        new[] { "README.txt", "bdxl.key", "disc.json", DiscFiles.ManifestFor(session), DiscFiles.PackListName }
            .Concat(Enumerable.Range(from, to - from + 1).Select(DiscFormat.PackPath)).Concat(more).Order(StringComparer.Ordinal).ToList();

    /// <summary>One decrypted manifest file of a staged disc folder (default: the first session's).</summary>
    List<ManifestLine> Manifest(string dir, string name = DiscFiles.ManifestName)
    {
        var bytes = FileCrypto.Open(File.ReadAllBytes(Path.Combine(dir, name)), TestKeys.Key(tmp.File("cat")), KeyPurpose.Manifest);
        return JsonStore.ParseLines<ManifestLine>(bytes, "manifest");
    }

    /// <summary>What a multisession burn that continues the file system looks like: the session's files over the disc's.</summary>
    static void BurnSession(string session, string disc)
    {
        foreach (string f in Directory.GetFiles(session, "*", SearchOption.AllDirectories))
        {
            string dst = Path.Combine(disc, Path.GetRelativePath(session, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
    }

    void Scan() => Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));

    /// <summary>One 1 MB blank disc (reserve 100 KB), the source scanned, staged and burned as BDXL-0001.</summary>
    void FirstDisc(string reserve = "100KB", params string[] stageOptions)
    {
        Assert.Equal(0, Bdxl("blanks", "add", "1", "--size", "1MB", "--reserve", reserve));
        Scan();
        Assert.Equal(0, Bdxl(["stage", tmp.File("stg"), .. stageOptions]));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));
    }

    [Fact]
    public void Only_new_and_changed_files_go_into_the_session_with_a_manifest_of_its_own()
    {
        var a = tmp.RandomFile("src/a.bin", 200_000, seed: 1);
        var oldReport = tmp.RandomFile("src/docs/report.txt", 50_000, seed: 2);
        FirstDisc();
        int first = DiscFormat.PacksFor(250_000);
        Assert.Equal(first, Disc1.Packs.Count);

        var newReport = tmp.RandomFile("src/docs/report.txt", 60_000, seed: 3);
        var added = tmp.RandomFile("src/new.bin", 100_000, seed: 4);
        Scan();
        Assert.Equal(0, Bdxl("plan", "--append", "BDXL-0001"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));

        // the old report stays where it is; the new version goes into the session's packs, numbered on
        int last = first + DiscFormat.PacksFor(160_000);
        Assert.Equal(SessionFiles(2, first + 1, last), FilesIn(Session2));
        var own = Manifest(Session2, "manifest.s2.enc");                                   // only this session's files
        Assert.Equal(["S/docs/report.txt", "S/new.bin"], own.Select(m => m.Source).Order(StringComparer.Ordinal));
        Assert.All(own, m => Assert.True(m.At >= first * DiscFormat.PackData));
        Assert.Contains(own, m => m.Source == "S/docs/report.txt" && Hashing.SameHash(m.Sha256, Hashing.HashBytes(newReport)));
        Assert.Equal(2, JsonStore.ReadObject<DiscInfo>(Path.Combine(Session2, "disc.json"))!.Sessions);
        Assert.Equal(last, DiscFiles.ReadPackList(Session2).Count);                         // every pack of the disc

        Assert.NotNull(Disc1.PendingAppend);
        Assert.NotEqual(0, Bdxl("stage", tmp.File("stg2"), "--append", "BDXL-0001"));   // one session at a time

        BurnSession(Session2, DiscDir);
        var whole = DiscFiles.ReadManifest(DiscDir, TestKeys.Key(tmp.File("cat")))!;         // the burned disc: both manifests, in order
        Assert.Equal(4, whole.Count);
        var reports = whole.Where(m => m.Source == "S/docs/report.txt").ToList();
        Assert.Equal(2, reports.Count);
        Assert.True(Hashing.SameHash(reports[^1].Sha256, Hashing.HashBytes(newReport)));      // the later session's line last
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", DiscDir));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));
        Assert.Equal(1, Bdxl("burned", "BDXL-0001"));
        Assert.All(Cat().Files.SelectMany(f => f.Backups), b => Assert.NotNull(b.BackedUpUtc));
        Assert.Null(Disc1.PendingAppend);

        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", DiscDir));
        Assert.Equal(newReport, File.ReadAllBytes(tmp.File("out/S/docs/report.txt")));
        Assert.Equal(added, File.ReadAllBytes(tmp.File("out/S/new.bin")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/a.bin")));

        // a lost catalog is rebuilt from the disc: the later copy is the newer version of report.txt
        Assert.Equal(0, Program.Main(["init", "--key-from", DiscDir, "--catalog", tmp.File("other")]));
        Assert.Equal(0, BdxlIn("other", PasswordVar, "scandisc", DiscDir));
        var report = Cat("other").Files.Single(f => f.RelPath == "docs/report.txt");
        Assert.Equal(Hashing.HashBytes(newReport), report.Sha256, ignoreCase: true);
        Assert.Contains(report.Backups, b => Hashing.SameHash(b.Sha256, Hashing.HashBytes(newReport)));
        Assert.Contains(report.Backups, b => Hashing.SameHash(b.Sha256, Hashing.HashBytes(oldReport)));
        Assert.Equal(last, Cat("other").Discs.Single().Packs.Count);
    }

    [Fact]
    public void A_disc_s_manifests_read_as_one_list_in_session_order()
    {
        TestKeys.Init(tmp.File("cat"));
        var key = TestKeys.Key(tmp.File("cat")).Current;
        static ManifestLine L(long at, string hash) => new("S/" + hash, at, 10, 10, hash);
        void Write(string name, params ManifestLine[] lines) => File.WriteAllBytes(tmp.File("mixed/" + name), DiscFiles.SealManifest(lines, key));
        Directory.CreateDirectory(tmp.File("mixed"));
        Write("manifest.enc", L(0, "A1"), L(100, "B1"));
        Write("manifest.s3.enc", L(100, "B2"), L(200, "C1"));               // a later line for the same place replaces the earlier one
        Write("manifest.s10.enc", L(0, "A2"));                              // session 10 comes after 3 (not "s1" < "s3")

        var lines = DiscFiles.ReadManifest(tmp.File("mixed"), TestKeys.Key(tmp.File("cat")))!;
        Assert.Equal(["0=A2", "100=B2", "200=C1"], lines.Select(l => $"{l.At}={l.Sha256}"));
        Assert.Equal(3, DiscFiles.Manifests(tmp.File("mixed")).Count);
        Assert.Equal(DiscFormat.ManifestOnDisc(0), DiscSpace.OnDisc(new FileInfo(tmp.File("mixed/manifest.enc")).Length));   // padded
    }

    [Fact]
    public void Free_space_counts_earlier_sessions_and_keeps_the_reserve_for_every_session()
    {
        tmp.RandomFile("src/a.bin", 300_000);
        FirstDisc();
        var disc = Disc1;
        long ManifestSpace(string dir) => DiscSpace.OnDisc(new FileInfo(DiscFiles.Manifests(dir).Single().Path).Length);
        // its packs and its manifest, and the reserve
        Assert.Equal(DiscFormat.PacksFor(300_000) * DiscFormat.PackOnDisc + ManifestSpace(DiscDir) + 100_000, disc.OccupiedBytes);
        long free = 1_000_000 - disc.OccupiedBytes - 100_000;
        Assert.Equal(free, AppendTarget.FreeFor(disc));

        tmp.RandomFile("src/big.bin", (int)DiscFormat.Room(free) + 1, seed: 2);
        Scan();
        Assert.Equal(2, Bdxl("plan", "--append", "BDXL-0001"));
        Assert.Equal(2, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));
        Assert.False(Directory.Exists(Session2));
        Assert.Null(Disc1.PendingAppend);

        tmp.RandomFile("src/small.bin", 150_000, seed: 3);
        Scan();
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));
        int packs = disc.Packs.Count;
        Assert.Equal(SessionFiles(2, packs + 1, packs + DiscFormat.PacksFor(150_000)), FilesIn(Session2));
        var after = Disc1;
        Assert.Equal((2, disc.OccupiedBytes + DiscFormat.PacksFor(150_000) * DiscFormat.PackOnDisc + ManifestSpace(Session2) + 100_000),
                     (after.Sessions, after.OccupiedBytes));
        Assert.Contains(Cat().Files, f => f.RelPath == "big.bin" && f.Backups.Count == 0);   // still pending
    }

    [Fact]
    public void Discard_takes_back_only_the_appended_session()
    {
        tmp.RandomFile("src/a.bin", 100_000);
        FirstDisc();
        var before = Disc1;
        tmp.RandomFile("src/new.bin", 50_000, seed: 2);
        Scan();
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));
        Assert.True(Disc1.Packs.Count > before.Packs.Count);

        Assert.Equal(0, Bdxl("discard", "BDXL-0001"));
        var disc = Disc1;
        Assert.Equal("burned", disc.State);
        Assert.Null(disc.PendingAppend);
        Assert.Equal((before.FileCount, before.UsedBytes, before.OccupiedBytes, 1), (disc.FileCount, disc.UsedBytes, disc.OccupiedBytes, disc.Sessions));
        Assert.Equal(before.Packs, disc.Packs);
        var cat = Cat();
        Assert.Empty(cat.Files.Single(f => f.RelPath == "new.bin").Backups);
        Assert.NotNull(Assert.Single(cat.Files.Single(f => f.RelPath == "a.bin").Backups).BackedUpUtc);

        Assert.Equal(1, Bdxl("discard", "BDXL-0001"));                                    // the burned disc itself stays
        Assert.Equal(0, Bdxl("stage", tmp.File("stg2"), "--append", "BDXL-0001"));         // and can be appended to again
        Assert.True(File.Exists(Path.Combine(tmp.File("stg2/BDXL-0001.s2"), DiscFormat.PackPath(before.Packs.Count + 1))));
    }

    [Fact]
    public void Only_a_burned_disc_with_no_unburned_session_takes_an_append()
    {
        tmp.RandomFile("src/a.bin", 100_000);
        Assert.Equal(0, Bdxl("blanks", "add", "2", "--size", "1MB", "--reserve", "100KB"));
        Scan();
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));
        tmp.RandomFile("src/new.bin", 50_000, seed: 2);
        Scan();
        Assert.Equal(1, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));         // staged, not burned
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));

        Assert.Equal(1, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001", "--max-discs", "2"));
        Directory.CreateDirectory(tmp.File("other-disc"));
        Assert.Equal(1, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001", "--mount", tmp.File("other-disc")));   // not this disc
        Assert.Null(Disc1.PendingAppend);

        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001", "--mount", DiscDir));
        Assert.Equal(1, Bdxl("lost", "BDXL-0001"));                                        // discard the session first
        Assert.Equal(1, Bdxl("restage", "BDXL-0001", tmp.File("re")));
        Assert.Equal("burned", Disc1.State);
    }

    [Fact]
    public void A_session_is_encrypted_with_the_disc_s_master_key_and_reveals_nothing()
    {
        tmp.RandomFile("src/tax-secret-2024.pdf", 50_000, seed: 1);
        tmp.RandomFile("src/notes/diary.txt", 3_000, seed: 2);
        FirstDisc("300KB", "--with-catalog");
        Assert.Equal(TestKeys.Key(tmp.File("cat")).KeyId, Disc1.KeyId);

        var added = tmp.RandomFile("src/notes/new-secret.txt", 4_000, seed: 3);
        Scan();
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001", "--with-catalog"));
        var files = FilesIn(Session2);
        Assert.Contains(DiscFormat.PackPath(2), files);                                    // numbering goes on
        Assert.Contains("manifest.s2.enc", files);                                         // its own manifest
        Assert.Contains("_catalog/files.000.jsonl.enc", files);
        string everything = string.Join("\n", files) + string.Concat(Directory.GetFiles(Session2, "*", SearchOption.AllDirectories)
                                                                         .Select(f => Encoding.Latin1.GetString(File.ReadAllBytes(f))));
        foreach (string telling in new[] { "secret", "diary", "notes" })
            Assert.DoesNotContain(telling, everything, StringComparison.OrdinalIgnoreCase);

        BurnSession(Session2, DiscDir);
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", DiscDir, "--deep"));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));

        // the catalog is lost: a new one with the disc's own key reads it
        Assert.Equal(0, Program.Main(["init", "--key-from", DiscDir, "--catalog", tmp.File("other")]));
        Assert.Equal(0, BdxlIn("other", PasswordVar, "scandisc", DiscDir));             // both sessions' manifests
        Assert.Equal(3, Cat("other").Files.Count);
        Assert.Equal(Disc1.KeyId, Cat("other").Discs.Single().KeyId);
        Assert.Equal(0, BdxlIn("other", PasswordVar, "restore", tmp.File("out"), "*", "--mount", DiscDir));
        Assert.Equal(added, File.ReadAllBytes(tmp.File("out/S/notes/new-secret.txt")));
    }

    [Fact]
    public void A_disc_encrypted_with_another_key_gets_no_session()
    {
        tmp.RandomFile("src/a.bin", 50_000);
        FirstDisc("300KB");
        tmp.RandomFile("src/b.bin", 5_000, seed: 2);
        Scan();

        var info = JsonStore.ReadObject<DiscInfo>(Path.Combine(DiscDir, DiscFiles.InfoName))!;
        JsonStore.WriteObject(Path.Combine(DiscDir, DiscFiles.InfoName), info with { KeyId = "0000000000000000" });
        Assert.Equal(1, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001", "--mount", DiscDir));   // the disc in the drive says so

        var cat = Cat();
        cat.Discs.Single().KeyId = "0000000000000000";                                                    // or the catalog does
        cat.Save();
        Assert.Equal(1, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));
        Assert.False(Directory.Exists(Session2));
    }
}
