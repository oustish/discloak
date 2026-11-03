namespace Discloak.Tests;

/// <summary>
/// What a disc shows and hides: its files are in equal packs (no names, sizes or count), its manifest and catalog
/// backup are encrypted. Readable without the password: disc.json (label, date, key id), packs.sha256, discloak.key (the
/// wrapped master key) and README.txt (the formats).
/// </summary>
public class EncryptedDiscTests : IDisposable
{
    const string PasswordVar = "DISCLOAK_TEST_PASSWORD";
    const string WrongVar = "DISCLOAK_TEST_WRONG_PASSWORD";
    const string NoVar = "DISCLOAK_TEST_NO_PASSWORD";   // never set: "no password available"
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    static EncryptedDiscTests()
    {
        Environment.SetEnvironmentVariable(PasswordVar, TestKeys.Password);
        Environment.SetEnvironmentVariable(WrongVar, "Wrong-Horse-42!!");
    }

    int DiscloakIn(string catalog, string passwordVar, params string[] args)
    {
        if (!(args.Length > 1 && args[0] == "catalog" && args[1] == "backup") && args[0] != "init")
            TestKeys.Init(tmp.File(catalog));   // a backup restores into, and init makes, an empty folder
        return Program.Main([.. args, "--catalog", tmp.File(catalog), "--password-env", passwordVar, "--yes"]);
    }

    int Discloak(params string[] args) => DiscloakIn("cat", PasswordVar, args);

    string Disc => tmp.File(Path.Combine("stg", "DISC-0001"));
    string Disc2 => tmp.File(Path.Combine("stg", "DISC-0002"));

    byte[] secret = [], diary = [];

    /// <summary>Two files with telling names in a source with a telling alias, staged onto one disc.</summary>
    void Stage(bool withCatalog = true)
    {
        secret = tmp.RandomFile("src/tax-secret-2024.pdf", 50_000, seed: 1);
        diary = tmp.RandomFile("src/notes/diary.txt", 3_000, seed: 2);
        Assert.Equal(0, Discloak("blanks", "add", "2", "--size", "1MB", "--reserve", "300KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("src"), "--name", "PrivateDocs"));
        Assert.Equal(0, withCatalog ? Discloak("stage", tmp.File("stg"), "--with-catalog") : Discloak("stage", tmp.File("stg")));
    }

    static List<string> FilesIn(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Nothing_on_the_disc_shows_names_hashes_or_plain_metadata()
    {
        Stage();

        var relPaths = FilesIn(Disc);
        string everything = string.Join("\n", relPaths) + "\n" + string.Concat(relPaths.Select(f => Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(Disc, f)))));
        foreach (string telling in new[] { "secret", "diary", "PrivateDocs", "notes", ".pdf", ".txt\"", "fileCount", "usedBytes" })
            Assert.DoesNotContain(telling, everything, StringComparison.OrdinalIgnoreCase);
        foreach (var plain in new[] { secret, diary })
            Assert.DoesNotContain(Hashing.HashBytes(plain), everything, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(["data/000/000001.pack"], TestKeys.Packs(Disc));                     // 53 KB of files: one 64 KiB pack
        Assert.Contains("manifest.enc", relPaths);
        Assert.Contains(DiscFiles.PackListName, relPaths);
        Assert.Contains("_catalog/files.000.jsonl.enc", relPaths);
        Assert.DoesNotContain("_catalog/files.jsonl", relPaths);

        // what the disc needs to be read on its own: its key file (the catalog's) and a description of the formats
        Assert.Equal(File.ReadAllText(KeyFile.PathIn(tmp.File("cat"))), File.ReadAllText(KeyFile.PathIn(Disc)));
        Assert.Contains("DCLKPAK1", File.ReadAllText(Path.Combine(Disc, DiscFiles.ReadmeName)));
        var info = JsonStore.ReadObject<DiscInfo>(Path.Combine(Disc, DiscFiles.InfoName))!;
        Assert.Equal((DiscFormat.Name, DiscFormat.PackData, KeyFile.ReadFrom(Disc)!.KeyId, KeyFile.ReadFrom(Disc)!.CatalogId),
                     (info.Format, info.PackSize, info.KeyId, info.CatalogId));
        Assert.Equal(info.KeyId, DiscFormat.KeyIdOf(Path.Combine(Disc, "data/000/000001.pack")));
        // packs.sha256 checks the disc with any sha256sum tool
        Assert.Equal(Hashing.HashFile(Path.Combine(Disc, "data/000/000001.pack")), DiscFiles.ReadPackList(Disc)[1]);

        // and it still works: verify (with and without decrypting) and restore by the real names
        Assert.Equal(0, Discloak("verify", "DISC-0001", Disc));
        Assert.Equal(0, Discloak("verify", "DISC-0001", Disc, "--deep"));
        Assert.Equal(0, Discloak("restore", tmp.File("out"), "*", "--mount", Disc));
        Assert.Equal(secret, File.ReadAllBytes(tmp.File("out/PrivateDocs/tax-secret-2024.pdf")));
        Assert.Equal(diary, File.ReadAllBytes(tmp.File("out/PrivateDocs/notes/diary.txt")));
    }

    [Fact]
    public void Two_discs_with_different_files_look_alike_from_outside()
    {
        // one disc of 300 tiny files, one of a single larger file: the same data in bytes
        for (int i = 0; i < 300; i++) tmp.RandomFile($"many/{i:D3}.txt", 300, seed: i);
        tmp.RandomFile("one/big.bin", 90_000, seed: 7);
        Assert.Equal(0, Discloak("blanks", "add", "1", "--size", "1MB", "--reserve", "100KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("many"), "--name", "Many"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));
        Assert.Equal(0, Discloak("blanks", "add", "1", "--size", "1MB", "--reserve", "100KB"));
        Assert.Equal(0, Discloak("scan", tmp.File("one"), "--name", "One"));
        Assert.Equal(0, Discloak("stage", tmp.File("stg")));

        static IEnumerable<(string, long)> Sizes(string disc) =>
            FilesIn(disc).Where(f => f != DiscFiles.PackListName).Select(f => (f, new FileInfo(Path.Combine(disc, f)).Length));
        Assert.Equal(Sizes(Disc).Where(x => !x.Item1.EndsWith("disc.json")), Sizes(Disc2).Where(x => !x.Item1.EndsWith("disc.json")));
        var cat = TestKeys.Load(tmp.File("cat"));
        Assert.Equal([300, 1], cat.Discs.OrderBy(d => d.Label).Select(d => d.FileCount));   // only the catalog knows
    }

    [Fact]
    public void A_lost_catalog_is_rebuilt_from_the_disc_with_its_own_key()
    {
        Stage(withCatalog: false);

        // a catalog with another master key cannot read the disc, and says what to do
        Assert.Equal(1, DiscloakIn("stranger", PasswordVar, "scandisc", Disc));
        Assert.Empty(TestKeys.Load(tmp.File("stranger")).Discs);

        // a new catalog from the disc's discloak.key: only with the right password
        Assert.Equal(1, DiscloakIn("other", WrongVar, "init", "--key-from", Disc));
        Assert.Equal(1, DiscloakIn("other", NoVar, "init", "--key-from", Disc));
        Assert.False(File.Exists(KeyFile.PathIn(tmp.File("other"))));
        Assert.Equal(0, DiscloakIn("other", PasswordVar, "init", "--key-from", Disc));

        Assert.Equal(0, DiscloakIn("other", PasswordVar, "scandisc", Disc));
        var cat = TestKeys.Load(tmp.File("other"));
        var rec = Assert.Single(cat.Files, f => f.RelPath == "tax-secret-2024.pdf");
        Assert.Equal("PrivateDocs", rec.RootName);
        var copy = Assert.Single(rec.Backups);
        Assert.True(copy.Unverified);
        var original = TestKeys.Load(tmp.File("cat")).Files.Single(f => f.RelPath == "tax-secret-2024.pdf");
        Assert.Equal((original.Backups[0].At, original.Size, original.ModifiedUtc), (copy.At, rec.Size, rec.ModifiedUtc));
        var disc = cat.Discs.Single();
        Assert.Equal((info().KeyId, 2), (disc.KeyId, disc.FileCount));
        Assert.Equal(TestKeys.Load(tmp.File("cat")).Discs.Single().Packs, disc.Packs);

        Assert.Equal(0, DiscloakIn("other", PasswordVar, "verify", "DISC-0001", Disc));
        Assert.DoesNotContain(TestKeys.Load(tmp.File("other")).Files.SelectMany(f => f.Backups), b => b.Unverified);
        Assert.Equal(0, DiscloakIn("other", PasswordVar, "restore", tmp.File("out"), "*", "--mount", Disc));
        Assert.Equal(secret, File.ReadAllBytes(tmp.File("out/PrivateDocs/tax-secret-2024.pdf")));
        Assert.Equal(diary, File.ReadAllBytes(tmp.File("out/PrivateDocs/notes/diary.txt")));

        DiscInfo info() => JsonStore.ReadObject<DiscInfo>(Path.Combine(Disc, DiscFiles.InfoName))!;
    }

    [Fact]
    public void Scandisc_reads_only_discs_made_by_discloak()
    {
        tmp.RandomFile("old-disc/Photos/2019/x.jpg", 30_000, seed: 5);   // burned long ago, without discloak
        Assert.Equal(1, Discloak("scandisc", tmp.File("old-disc")));
        Assert.Empty(TestKeys.Load(tmp.File("cat")).Discs);
        Assert.Equal(1, Program.Main(["discjson", "--help"]));         // the command is gone
    }

    [Fact]
    public void After_a_password_change_old_discs_open_with_the_old_password_and_new_discs_with_the_new_one()
    {
        Stage(withCatalog: false);
        Environment.SetEnvironmentVariable("DISCLOAK_TEST_NEW_PW", "Brand-New-Pass-99#");
        Assert.Equal(0, Discloak("password", "change", "--new-password-env", "DISCLOAK_TEST_NEW_PW", "--kdf-memory", "8MiB", "--kdf-passes", "1"));

        tmp.RandomFile("src/later.txt", 2_000, seed: 9);
        Assert.Equal(0, DiscloakIn("cat", "DISCLOAK_TEST_NEW_PW", "scan", tmp.File("src"), "--name", "PrivateDocs"));
        Assert.Equal(0, DiscloakIn("cat", "DISCLOAK_TEST_NEW_PW", "stage", tmp.File("stg")));

        Assert.NotNull(KeyFile.ReadFrom(Disc)!.TryUnlock(TestKeys.Password));          // burned before the change
        Assert.Null(KeyFile.ReadFrom(Disc)!.TryUnlock("Brand-New-Pass-99#"));
        Assert.NotNull(KeyFile.ReadFrom(Disc2)!.TryUnlock("Brand-New-Pass-99#"));      // staged after it
        Assert.Null(KeyFile.ReadFrom(Disc2)!.TryUnlock(TestKeys.Password));

        // either way it is one master key: a catalog made from the old disc reads the new one
        Assert.Equal(0, DiscloakIn("other", PasswordVar, "init", "--key-from", Disc));
        Assert.Equal(0, DiscloakIn("other", PasswordVar, "scandisc", Disc2));
        Assert.Equal(0, DiscloakIn("other", PasswordVar, "restore", tmp.File("out"), "later.txt", "--mount", Disc2));
        Assert.True(File.Exists(tmp.File("out/PrivateDocs/later.txt")));
    }

    [Fact]
    public void The_catalog_backup_on_the_disc_is_repaired_without_and_restored_with_the_password()
    {
        Stage();
        string backup = Path.Combine(Disc, "_catalog");
        string damaged = Path.Combine(backup, "files.000.jsonl.enc");
        byte[] bytes = File.ReadAllBytes(damaged);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(damaged, bytes);

        Assert.Equal(1, DiscloakIn("other", NoVar, "catalog", "backup", "verify", backup));               // damaged, found without a password
        Assert.Equal(0, DiscloakIn("other", NoVar, "catalog", "backup", "verify", backup, "--repair"));
        Assert.Equal(0, DiscloakIn("other", NoVar, "catalog", "backup", "verify", backup));

        Assert.Equal(1, DiscloakIn("other", NoVar, "catalog", "backup", "restore", backup));
        Assert.Equal(1, DiscloakIn("other", WrongVar, "catalog", "backup", "restore", backup));
        Assert.False(File.Exists(Path.Combine(tmp.File("other"), "files.000.jsonl.enc")));              // nothing half-restored

        Assert.Equal(0, DiscloakIn("other", PasswordVar, "catalog", "backup", "restore", backup));
        var cat = TestKeys.Load(tmp.File("other"));
        Assert.Equal(IntegrityState.Ok, cat.Integrity.State);
        Assert.Contains(cat.Files, f => f.RelPath == "tax-secret-2024.pdf");
    }

    [Fact]
    public void A_standalone_backup_is_encrypted()
    {
        tmp.RandomFile("src/tax-secret-2024.pdf", 1000);
        Discloak("scan", tmp.File("src"), "--name", "PrivateDocs");

        Assert.Equal(0, Discloak("catalog", "backup", "create", tmp.File("bk")));
        string backup = Assert.Single(Directory.GetDirectories(tmp.File("bk")));
        Assert.True(File.Exists(Path.Combine(backup, "files.000.jsonl.enc")));
        Assert.False(File.Exists(Path.Combine(backup, "files.jsonl")));
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(backup).Select(f => Encoding.Latin1.GetString(File.ReadAllBytes(f)))));

        Assert.Equal(0, DiscloakIn("other", PasswordVar, "catalog", "backup", "restore", backup));
        Assert.Contains(TestKeys.Load(tmp.File("other")).Files, f => f.RelPath == "tax-secret-2024.pdf");
    }

    [Fact]
    public void Restage_keeps_the_layout_and_writes_an_encrypted_manifest()
    {
        Stage(withCatalog: false);
        Discloak("burned", "DISC-0001");
        var before = TestKeys.Load(tmp.File("cat")).Files.SelectMany(f => f.Backups).Select(b => (b.At, b.StoredSize)).Order().ToList();
        Assert.Equal(0, Discloak("lost", "DISC-0001"));

        Assert.Equal(0, Discloak("restage", "DISC-0001", tmp.File("re")));
        string rebuilt = Path.Combine(tmp.File("re"), "DISC-0001");
        Assert.Equal(TestKeys.Packs(Disc), TestKeys.Packs(rebuilt));
        Assert.True(File.Exists(Path.Combine(rebuilt, "manifest.enc")));
        Assert.True(File.Exists(KeyFile.PathIn(rebuilt)));
        Assert.True(File.Exists(Path.Combine(rebuilt, DiscFiles.ReadmeName)));
        Assert.False(File.Exists(Path.Combine(rebuilt, "restage.json")));
        Assert.Equal(before, TestKeys.Load(tmp.File("cat")).Files.SelectMany(f => f.Backups).Select(b => (b.At, b.StoredSize)).Order());
        Assert.Equal(0, Discloak("verify", "DISC-0001", rebuilt, "--deep"));
    }

    [Fact]
    public void Restage_and_restore_refuse_a_disc_of_another_key()
    {
        Stage(withCatalog: false);
        Discloak("burned", "DISC-0001");
        var cat = TestKeys.Load(tmp.File("cat"));
        cat.Discs.Single().KeyId = "0000000000000000";   // as if it had been made with another master key
        cat.Save();

        Assert.Equal(0, Discloak("lost", "DISC-0001"));
        Assert.Equal(1, Discloak("restage", "DISC-0001", tmp.File("re")));
        Assert.False(Directory.Exists(Path.Combine(tmp.File("re"), "DISC-0001")));
        Assert.Equal("lost", TestKeys.Load(tmp.File("cat")).Discs.Single().State);

        var info = JsonStore.ReadObject<DiscInfo>(Path.Combine(Disc, DiscFiles.InfoName))!;
        JsonStore.WriteObject(Path.Combine(Disc, DiscFiles.InfoName), info with { KeyId = "0000000000000000" });
        Assert.Equal(0, Discloak("found", "DISC-0001"));
        Assert.NotEqual(0, Discloak("restore", tmp.File("out"), "*", "--mount", Disc));       // the mount is ignored: another key
        Assert.False(File.Exists(tmp.File("out/PrivateDocs/notes/diary.txt")));
    }
}
