namespace Bdxl.Tests;

/// <summary>
/// Discs staged with --encrypt: files, manifest and catalog backup encrypted with the catalog's master key, hidden
/// file names. Readable without the password: disc.json (label, count, size, key id), bdxl.key (the wrapped master
/// key) and README.txt (the formats).
/// </summary>
public class EncryptedDiscTests : IDisposable
{
    const string PasswordVar = "BDXL_TEST_PASSWORD";
    const string WrongVar = "BDXL_TEST_WRONG_PASSWORD";
    const string NoVar = "BDXL_TEST_NO_PASSWORD";   // never set: "no password available"
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    static EncryptedDiscTests()
    {
        Environment.SetEnvironmentVariable(PasswordVar, TestKeys.Password);
        Environment.SetEnvironmentVariable(WrongVar, "Wrong-Horse-42!!");
    }

    int BdxlIn(string catalog, string passwordVar, params string[] args)
    {
        if (!(args.Length > 1 && args[0] == "catalog" && args[1] == "backup") && args[0] != "init")
            TestKeys.Init(tmp.File(catalog));   // a backup restores into, and init makes, an empty folder
        return Program.Main([.. args, "--catalog", tmp.File(catalog), "--password-env", passwordVar, "--yes"]);
    }

    int Bdxl(params string[] args) => BdxlIn("cat", PasswordVar, args);

    string Disc => tmp.File(Path.Combine("stg", "BDXL-0001"));

    byte[] secret = [], diary = [];

    /// <summary>Two files with telling names in a source with a telling alias, staged onto one encrypted disc.</summary>
    void StageEncrypted(bool withCatalog = true)
    {
        secret = tmp.RandomFile("src/tax-secret-2024.pdf", 50_000, seed: 1);
        diary = tmp.RandomFile("src/notes/diary.txt", 3_000, seed: 2);
        Assert.Equal(0, Bdxl("blanks", "add", "2", "--size", "1MB", "--reserve", "300KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "PrivateDocs"));
        Assert.Equal(0, withCatalog ? Bdxl("stage", tmp.File("stg"), "--encrypt", "--with-catalog") : Bdxl("stage", tmp.File("stg"), "--encrypt"));
    }

    [Fact]
    public void Nothing_on_the_disc_shows_names_hashes_or_plain_metadata()
    {
        StageEncrypted();

        var files = Directory.GetFiles(Disc, "*", SearchOption.AllDirectories);
        var relPaths = files.Select(f => Path.GetRelativePath(Disc, f).Replace('\\', '/')).ToList();
        string everything = string.Join("\n", relPaths) + "\n" + string.Concat(files.Select(f => Encoding.Latin1.GetString(File.ReadAllBytes(f))));

        foreach (string telling in new[] { "secret", "diary", "PrivateDocs", "notes", ".pdf", ".txt\"" })
            Assert.DoesNotContain(telling, everything, StringComparison.OrdinalIgnoreCase);
        foreach (var plain in new[] { secret, diary })
            Assert.DoesNotContain(Hashing.HashBytes(plain), everything, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("data/000/000001.enc", relPaths);
        Assert.Contains("manifest.jsonl.enc", relPaths);
        Assert.DoesNotContain("manifest.jsonl", relPaths);
        Assert.Contains("_catalog/files.000.jsonl.enc", relPaths);
        Assert.DoesNotContain("_catalog/files.jsonl", relPaths);

        // what the disc needs to be read on its own: its key file (the catalog's) and a description of the formats
        Assert.Equal(File.ReadAllText(KeyFile.PathIn(tmp.File("cat"))), File.ReadAllText(KeyFile.PathIn(Disc)));
        Assert.Contains("BDXLENC2", File.ReadAllText(Path.Combine(Disc, DiscFiles.ReadmeName)));
        var info = JsonStore.ReadObject<DiscInfo>(Path.Combine(Disc, DiscFiles.InfoName))!;
        Assert.True(info.Encrypted);
        Assert.Equal(KeyFile.ReadFrom(Disc)!.KeyId, info.KeyId);
        Assert.Equal(KeyFile.ReadFrom(Disc)!.CatalogId, info.CatalogId);
        Assert.All(relPaths.Where(p => p.StartsWith("data/")), p => Assert.Equal(info.KeyId, FileCrypto.KeyIdOf(Path.Combine(Disc, p))));

        // and it still works: verify (with and without decrypting) and restore by the real names
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", Disc));
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", Disc, "--deep"));
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", Disc));
        Assert.Equal(secret, File.ReadAllBytes(tmp.File("out/PrivateDocs/tax-secret-2024.pdf")));
        Assert.Equal(diary, File.ReadAllBytes(tmp.File("out/PrivateDocs/notes/diary.txt")));
    }

    [Fact]
    public void A_lost_catalog_is_rebuilt_from_the_disc_with_its_own_key()
    {
        StageEncrypted(withCatalog: false);

        // a catalog with another master key cannot read the disc, and says what to do
        Assert.Equal(1, BdxlIn("stranger", PasswordVar, "scandisc", Disc));
        Assert.Empty(TestKeys.Load(tmp.File("stranger")).Discs);

        // a new catalog from the disc's bdxl.key: only with the right password
        Assert.Equal(1, BdxlIn("other", WrongVar, "init", "--key-from", Disc));
        Assert.Equal(1, BdxlIn("other", NoVar, "init", "--key-from", Disc));
        Assert.False(File.Exists(KeyFile.PathIn(tmp.File("other"))));
        Assert.Equal(0, BdxlIn("other", PasswordVar, "init", "--key-from", Disc));

        Assert.Equal(0, BdxlIn("other", PasswordVar, "scandisc", Disc));
        var cat = TestKeys.Load(tmp.File("other"));
        var rec = Assert.Single(cat.Files, f => f.RelPath == "tax-secret-2024.pdf");
        Assert.Equal("PrivateDocs", rec.RootName);
        Assert.Equal("data/000/000001.enc", Assert.Single(rec.Backups).DiscPath);
        Assert.Equal(info().KeyId, cat.Discs.Single().KeyId);

        Assert.Equal(0, BdxlIn("other", PasswordVar, "restore", tmp.File("out"), "*", "--mount", Disc));
        Assert.Equal(secret, File.ReadAllBytes(tmp.File("out/PrivateDocs/tax-secret-2024.pdf")));
        Assert.Equal(diary, File.ReadAllBytes(tmp.File("out/PrivateDocs/notes/diary.txt")));

        DiscInfo info() => JsonStore.ReadObject<DiscInfo>(Path.Combine(Disc, DiscFiles.InfoName))!;
    }

    [Fact]
    public void After_a_password_change_old_discs_open_with_the_old_password_and_new_discs_with_the_new_one()
    {
        StageEncrypted(withCatalog: false);
        Environment.SetEnvironmentVariable("BDXL_TEST_NEW_PW", "Brand-New-Pass-99#");
        Assert.Equal(0, Bdxl("password", "change", "--new-password-env", "BDXL_TEST_NEW_PW", "--kdf-memory", "8MiB", "--kdf-passes", "1"));

        tmp.RandomFile("src/later.txt", 2_000, seed: 9);
        Assert.Equal(0, BdxlIn("cat", "BDXL_TEST_NEW_PW", "scan", tmp.File("src"), "--name", "PrivateDocs"));
        Assert.Equal(0, BdxlIn("cat", "BDXL_TEST_NEW_PW", "stage", tmp.File("stg"), "--encrypt"));
        string disc2 = tmp.File(Path.Combine("stg", "BDXL-0002"));

        Assert.NotNull(KeyFile.ReadFrom(Disc)!.TryUnlock(TestKeys.Password));          // burned before the change
        Assert.Null(KeyFile.ReadFrom(Disc)!.TryUnlock("Brand-New-Pass-99#"));
        Assert.NotNull(KeyFile.ReadFrom(disc2)!.TryUnlock("Brand-New-Pass-99#"));       // staged after it
        Assert.Null(KeyFile.ReadFrom(disc2)!.TryUnlock(TestKeys.Password));

        // either way it is one master key: a catalog made from the old disc reads the new one
        Assert.Equal(0, BdxlIn("other", PasswordVar, "init", "--key-from", Disc));
        Assert.Equal(0, BdxlIn("other", PasswordVar, "scandisc", disc2));
        Assert.Equal(0, BdxlIn("other", PasswordVar, "restore", tmp.File("out"), "later.txt", "--mount", disc2));
        Assert.True(File.Exists(tmp.File("out/PrivateDocs/later.txt")));
    }

    [Fact]
    public void The_catalog_backup_on_the_disc_is_repaired_without_and_restored_with_the_password()
    {
        StageEncrypted();
        string backup = Path.Combine(Disc, "_catalog");
        string damaged = Path.Combine(backup, "files.000.jsonl.enc");
        byte[] bytes = File.ReadAllBytes(damaged);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(damaged, bytes);

        Assert.Equal(1, BdxlIn("other", NoVar, "catalog", "backup", "verify", backup));               // damaged, found without a password
        Assert.Equal(0, BdxlIn("other", NoVar, "catalog", "backup", "verify", backup, "--repair"));
        Assert.Equal(0, BdxlIn("other", NoVar, "catalog", "backup", "verify", backup));

        Assert.Equal(1, BdxlIn("other", NoVar, "catalog", "backup", "restore", backup));
        Assert.Equal(1, BdxlIn("other", WrongVar, "catalog", "backup", "restore", backup));
        Assert.False(File.Exists(Path.Combine(tmp.File("other"), "files.000.jsonl.enc")));              // nothing half-restored

        Assert.Equal(0, BdxlIn("other", PasswordVar, "catalog", "backup", "restore", backup));
        var cat = TestKeys.Load(tmp.File("other"));
        Assert.Equal(IntegrityState.Ok, cat.Integrity.State);
        Assert.Contains(cat.Files, f => f.RelPath == "tax-secret-2024.pdf");
    }

    [Fact]
    public void A_standalone_backup_is_encrypted()
    {
        tmp.RandomFile("src/tax-secret-2024.pdf", 1000);
        Bdxl("scan", tmp.File("src"), "--name", "PrivateDocs");

        Assert.Equal(0, Bdxl("catalog", "backup", "create", tmp.File("bk")));
        string backup = Assert.Single(Directory.GetDirectories(tmp.File("bk")));
        Assert.True(File.Exists(Path.Combine(backup, "files.000.jsonl.enc")));
        Assert.False(File.Exists(Path.Combine(backup, "files.jsonl")));
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(backup).Select(f => Encoding.Latin1.GetString(File.ReadAllBytes(f)))));

        Assert.Equal(0, BdxlIn("other", PasswordVar, "catalog", "backup", "restore", backup));
        Assert.Contains(TestKeys.Load(tmp.File("other")).Files, f => f.RelPath == "tax-secret-2024.pdf");
    }

    [Fact]
    public void Restage_keeps_hidden_names_and_writes_an_encrypted_manifest()
    {
        StageEncrypted(withCatalog: false);
        Bdxl("burned", "BDXL-0001");
        Assert.Equal(0, Bdxl("lost", "BDXL-0001"));

        Assert.Equal(0, Bdxl("restage", "BDXL-0001", tmp.File("re")));
        string rebuilt = Path.Combine(tmp.File("re"), "BDXL-0001");
        Assert.True(File.Exists(Path.Combine(rebuilt, "data", "000", "000001.enc")));
        Assert.True(File.Exists(Path.Combine(rebuilt, "manifest.jsonl.enc")));
        Assert.True(File.Exists(KeyFile.PathIn(rebuilt)));
        Assert.True(File.Exists(Path.Combine(rebuilt, DiscFiles.ReadmeName)));
        Assert.False(File.Exists(Path.Combine(rebuilt, "manifest.jsonl")));
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", rebuilt, "--deep"));
    }

    [Fact]
    public void Restage_discjson_and_restore_refuse_a_disc_of_another_key()
    {
        StageEncrypted(withCatalog: false);
        Bdxl("burned", "BDXL-0001");
        var cat = TestKeys.Load(tmp.File("cat"));
        cat.Discs.Single().KeyId = "0000000000000000";   // as if it had been made with another master key
        cat.Save();

        File.Delete(Path.Combine(Disc, "manifest.jsonl.enc"));
        Assert.Equal(1, Bdxl("discjson", "BDXL-0001", Disc, "--overwrite"));
        Assert.False(File.Exists(Path.Combine(Disc, "manifest.jsonl.enc")));

        Assert.Equal(0, Bdxl("lost", "BDXL-0001"));
        Assert.Equal(1, Bdxl("restage", "BDXL-0001", tmp.File("re")));
        Assert.False(Directory.Exists(Path.Combine(tmp.File("re"), "BDXL-0001")));
        Assert.Equal("lost", TestKeys.Load(tmp.File("cat")).Discs.Single().State);

        var info = JsonStore.ReadObject<DiscInfo>(Path.Combine(Disc, DiscFiles.InfoName))!;
        JsonStore.WriteObject(Path.Combine(Disc, DiscFiles.InfoName), info with { KeyId = "0000000000000000" });
        Assert.Equal(0, Bdxl("found", "BDXL-0001"));
        Assert.NotEqual(0, Bdxl("restore", tmp.File("out"), "*", "--mount", Disc));       // the mount is ignored: another key
        Assert.False(File.Exists(tmp.File("out/PrivateDocs/notes/diary.txt")));
    }

    [Fact]
    public void Discjson_puts_back_an_encrypted_manifest_and_the_key_file()
    {
        StageEncrypted(withCatalog: false);
        foreach (string name in new[] { "disc.json", "manifest.jsonl.enc", KeyFile.FileName, DiscFiles.ReadmeName })
            File.Delete(Path.Combine(Disc, name));

        Assert.Equal(0, Bdxl("discjson", "BDXL-0001", Disc));
        Assert.True(File.Exists(Path.Combine(Disc, "manifest.jsonl.enc")));
        Assert.True(File.Exists(KeyFile.PathIn(Disc)));
        Assert.False(File.Exists(Path.Combine(Disc, "manifest.jsonl")));

        Assert.Equal(0, BdxlIn("other", PasswordVar, "init", "--key-from", Disc));
        Assert.Equal(0, BdxlIn("other", PasswordVar, "scandisc", Disc));   // the rewritten manifest is complete
        Assert.Equal(2, TestKeys.Load(tmp.File("other")).Files.Count);
    }
}
