namespace Bdxl.Tests;

/// <summary>
/// stage --append: a burned disc that is not full gets one more session with only the new/changed files, a manifest
/// of the whole disc and disc.json. Burning a session is simulated by copying its folder over the disc's folder
/// (a multisession burn that continues the disc's file system shows exactly that merged view).
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

    /// <summary>The decrypted manifest of a staged (encrypted) disc folder.</summary>
    List<ManifestLine> Manifest(string dir)
    {
        var bytes = FileCrypto.Open(File.ReadAllBytes(Path.Combine(dir, DiscFiles.EncryptedManifestName)), TestKeys.Key(tmp.File("cat")), KeyPurpose.Manifest);
        using var ms = new MemoryStream(bytes);
        return JsonStore.ParseLines<ManifestLine>(ms, "manifest");
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
    public void Only_new_and_changed_files_go_into_the_session_and_its_manifest_lists_the_whole_disc()
    {
        var a = tmp.RandomFile("src/a.bin", 200_000, seed: 1);
        var oldReport = tmp.RandomFile("src/docs/report.txt", 50_000, seed: 2);
        FirstDisc();

        var newReport = tmp.RandomFile("src/docs/report.txt", 60_000, seed: 3);
        var added = tmp.RandomFile("src/new.bin", 100_000, seed: 4);
        Scan();
        Assert.Equal(0, Bdxl("plan", "--append", "BDXL-0001"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));

        // the old report stays where it is; the new version gets the next hidden name
        Assert.Equal(new[] { "README.txt", "bdxl.key", "data/000/000003.enc", "data/000/000004.enc", "disc.json", "manifest.jsonl.enc" }, FilesIn(Session2));
        var manifest = Manifest(Session2);
        Assert.Equal(new[] { "data/000/000001.enc", "data/000/000002.enc", "data/000/000003.enc", "data/000/000004.enc" }, manifest.Select(m => m.Path).Order(StringComparer.Ordinal));
        var reports = manifest.Where(m => m.Source == "S/docs/report.txt").ToList();
        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, m => Hashing.SameHash(m.PlainSha256!, Hashing.HashBytes(oldReport)) && m.Size == FileCrypto.StoredSize(oldReport.Length));   // sizes of earlier sessions come from the catalog
        Assert.Contains(reports, m => Hashing.SameHash(m.PlainSha256!, Hashing.HashBytes(newReport)));
        var info = JsonStore.ReadObject<DiscInfo>(Path.Combine(Session2, "disc.json"))!;
        Assert.Equal((4, 2), (info.FileCount, info.Sessions));

        Assert.NotNull(Disc1.PendingAppend);
        Assert.NotEqual(0, Bdxl("stage", tmp.File("stg2"), "--append", "BDXL-0001"));   // one session at a time

        BurnSession(Session2, DiscDir);
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
    }

    [Fact]
    public void Free_space_counts_earlier_sessions_and_keeps_the_reserve_for_every_session()
    {
        tmp.RandomFile("src/a.bin", 300_000);
        FirstDisc();
        var disc = Disc1;
        Assert.Equal(DiscSpace.OnDisc(disc.UsedBytes) + 100_000, disc.OccupiedBytes);   // its one file in whole sectors + entries, and the reserve
        long free = 1_000_000 - disc.OccupiedBytes - 100_000;
        Assert.Equal(free, AppendTarget.FreeFor(disc));

        tmp.RandomFile("src/big.bin", (int)free + 1, seed: 2);
        Scan();
        Assert.Equal(2, Bdxl("plan", "--append", "BDXL-0001"));
        Assert.Equal(2, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));
        Assert.False(Directory.Exists(Session2));
        Assert.Null(Disc1.PendingAppend);

        tmp.RandomFile("src/small.bin", 200_000, seed: 3);
        Scan();
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));
        Assert.Equal(new[] { "README.txt", "bdxl.key", "data/000/000002.enc", "disc.json", "manifest.jsonl.enc" }, FilesIn(Session2));
        var after = Disc1;
        Assert.Equal((2, disc.OccupiedBytes + DiscSpace.OnDisc(FileCrypto.StoredSize(200_000)) + 100_000), (after.Sessions, after.OccupiedBytes));
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

        Assert.Equal(0, Bdxl("discard", "BDXL-0001"));
        var disc = Disc1;
        Assert.Equal("burned", disc.State);
        Assert.Null(disc.PendingAppend);
        Assert.Equal((before.FileCount, before.UsedBytes, before.OccupiedBytes, 1), (disc.FileCount, disc.UsedBytes, disc.OccupiedBytes, disc.Sessions));
        var cat = Cat();
        Assert.Empty(cat.Files.Single(f => f.RelPath == "new.bin").Backups);
        Assert.NotNull(Assert.Single(cat.Files.Single(f => f.RelPath == "a.bin").Backups).BackedUpUtc);

        Assert.Equal(1, Bdxl("discard", "BDXL-0001"));                                    // the burned disc itself stays
        Assert.Equal(0, Bdxl("stage", tmp.File("stg2"), "--append", "BDXL-0001"));         // and can be appended to again
        Assert.True(File.Exists(tmp.File("stg2/BDXL-0001.s2/data/000/000002.enc")));
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

        var cat = Cat();
        cat.Discs.Single().Encrypted = false;                                               // a plain disc (imported from before)
        cat.Save();
        Assert.Equal(1, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001"));           // takes no session: bdxl adds only encrypted data
        cat = Cat();
        cat.Discs.Single().Encrypted = true;
        cat.Save();
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
    public void An_encrypted_disc_gets_an_encrypted_session_with_the_same_master_key()
    {
        tmp.RandomFile("src/tax-secret-2024.pdf", 50_000, seed: 1);
        tmp.RandomFile("src/notes/diary.txt", 3_000, seed: 2);
        FirstDisc("300KB", "--encrypt", "--with-catalog");
        Assert.Equal(TestKeys.Key(tmp.File("cat")).KeyId, Disc1.KeyId);

        var added = tmp.RandomFile("src/notes/new-secret.txt", 4_000, seed: 3);
        Scan();
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--append", "BDXL-0001", "--with-catalog"));
        var files = FilesIn(Session2);
        Assert.Contains("data/000/000003.enc", files);                                    // numbering goes on
        Assert.Contains("manifest.jsonl.enc", files);
        Assert.Contains("_catalog/files.jsonl.enc", files);
        Assert.DoesNotContain("manifest.jsonl", files);
        string everything = string.Join("\n", files) + string.Concat(Directory.GetFiles(Session2, "*", SearchOption.AllDirectories)
                                                                         .Select(f => Encoding.Latin1.GetString(File.ReadAllBytes(f))));
        foreach (string telling in new[] { "secret", "diary", "notes" })
            Assert.DoesNotContain(telling, everything, StringComparison.OrdinalIgnoreCase);

        BurnSession(Session2, DiscDir);
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", DiscDir, "--deep"));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));

        // the catalog is lost: a new one with the disc's own key reads it
        Assert.Equal(0, Program.Main(["init", "--key-from", DiscDir, "--catalog", tmp.File("other")]));
        Assert.Equal(0, BdxlIn("other", PasswordVar, "scandisc", DiscDir));             // the session's manifest covers the whole disc
        Assert.Equal(3, Cat("other").Files.Count);
        Assert.Equal(Disc1.KeyId, Cat("other").Discs.Single().KeyId);
        Assert.Equal(0, BdxlIn("other", PasswordVar, "restore", tmp.File("out"), "*", "--mount", DiscDir));
        Assert.Equal(added, File.ReadAllBytes(tmp.File("out/S/notes/new-secret.txt")));
    }

    [Fact]
    public void A_disc_encrypted_with_another_key_gets_no_session()
    {
        tmp.RandomFile("src/a.bin", 50_000);
        FirstDisc("300KB", "--encrypt");
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
