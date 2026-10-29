namespace Bdxl.Tests;

/// <summary>
/// Discs staged with --encrypt: hidden file names, encrypted manifest and encrypted catalog backup. Nothing but
/// disc.json (label, count, size) may be readable without the password.
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
        Environment.SetEnvironmentVariable(PasswordVar, "Correct-Horse-42!");
        Environment.SetEnvironmentVariable(WrongVar, "Wrong-Horse-42!!");
    }

    int BdxlIn(string catalog, string passwordVar, params string[] args) =>
        Program.Main([.. args, "--catalog", tmp.File(catalog), "--password-env", passwordVar, "--yes"]);

    int Bdxl(params string[] args) => BdxlIn("cat", PasswordVar, args);

    string Disc => tmp.File(Path.Combine("stg", "BDXL-0001"));

    byte[] secret = [], diary = [];

    /// <summary>Two files with telling names in a source with a telling alias, staged onto one encrypted disc.</summary>
    void StageEncrypted(bool withCatalog = true)
    {
        secret = tmp.RandomFile("src/tax-secret-2024.pdf", 50_000, seed: 1);
        diary = tmp.RandomFile("src/notes/diary.txt", 3_000, seed: 2);
        Assert.Equal(0, Bdxl("blanks", "add", "1", "--size", "1MB", "--reserve", "300KB"));
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
        Assert.Contains("_catalog/files.jsonl.enc", relPaths);
        Assert.DoesNotContain("_catalog/files.jsonl", relPaths);
        Assert.Contains("\"encrypted\": true", File.ReadAllText(Path.Combine(Disc, "disc.json")));

        // and it still works: verify (with and without decrypting) and restore by the real names
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", Disc));
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", Disc, "--deep"));
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", Disc));
        Assert.Equal(secret, File.ReadAllBytes(tmp.File("out/PrivateDocs/tax-secret-2024.pdf")));
        Assert.Equal(diary, File.ReadAllBytes(tmp.File("out/PrivateDocs/notes/diary.txt")));
    }

    [Fact]
    public void Scandisc_rebuilds_a_lost_catalog_from_the_encrypted_manifest()
    {
        StageEncrypted(withCatalog: false);

        Assert.Equal(1, BdxlIn("other", WrongVar, "scandisc", Disc));
        Assert.Empty(Catalog.Load(tmp.File("other"), "test").Discs);         // nothing recorded with a wrong password
        Assert.Equal(1, BdxlIn("other", NoVar, "scandisc", Disc));

        Assert.Equal(0, BdxlIn("other", PasswordVar, "scandisc", Disc));
        var cat = Catalog.Load(tmp.File("other"), "test");
        var rec = Assert.Single(cat.Files, f => f.RelPath == "tax-secret-2024.pdf");
        Assert.Equal("PrivateDocs", rec.RootName);
        Assert.Equal("data/000/000001.enc", Assert.Single(rec.Backups).DiscPath);

        Assert.Equal(0, BdxlIn("other", PasswordVar, "restore", tmp.File("out"), "*", "--mount", Disc));
        Assert.Equal(secret, File.ReadAllBytes(tmp.File("out/PrivateDocs/tax-secret-2024.pdf")));
        Assert.Equal(diary, File.ReadAllBytes(tmp.File("out/PrivateDocs/notes/diary.txt")));
    }

    [Fact]
    public void The_catalog_backup_on_the_disc_is_repaired_without_and_restored_with_the_password()
    {
        StageEncrypted();
        string backup = Path.Combine(Disc, "_catalog");
        string damaged = Path.Combine(backup, "files.jsonl.enc");
        byte[] bytes = File.ReadAllBytes(damaged);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(damaged, bytes);

        Assert.Equal(1, BdxlIn("other", NoVar, "catalog", "backup", "verify", backup));               // damaged, found without a password
        Assert.Equal(0, BdxlIn("other", NoVar, "catalog", "backup", "verify", backup, "--repair"));
        Assert.Equal(0, BdxlIn("other", NoVar, "catalog", "backup", "verify", backup));

        Assert.Equal(1, BdxlIn("other", NoVar, "catalog", "backup", "restore", backup));
        Assert.Equal(1, BdxlIn("other", WrongVar, "catalog", "backup", "restore", backup));
        Assert.False(File.Exists(Path.Combine(tmp.File("other"), "files.jsonl")));                  // nothing half-restored

        Assert.Equal(0, BdxlIn("other", PasswordVar, "catalog", "backup", "restore", backup));
        var cat = Catalog.Load(tmp.File("other"), "test");
        Assert.Equal(IntegrityState.Ok, cat.Integrity.State);
        Assert.Contains(cat.Files, f => f.RelPath == "tax-secret-2024.pdf");
    }

    [Fact]
    public void A_standalone_backup_can_be_encrypted()
    {
        tmp.RandomFile("src/tax-secret-2024.pdf", 1000);
        Bdxl("scan", tmp.File("src"), "--name", "PrivateDocs");

        Assert.Equal(0, Bdxl("catalog", "backup", "create", tmp.File("bk"), "--encrypt"));
        string backup = Assert.Single(Directory.GetDirectories(tmp.File("bk")));
        Assert.True(File.Exists(Path.Combine(backup, "files.jsonl.enc")));
        Assert.False(File.Exists(Path.Combine(backup, "files.jsonl")));
        Assert.DoesNotContain("secret", string.Concat(Directory.GetFiles(backup).Select(f => Encoding.Latin1.GetString(File.ReadAllBytes(f)))));

        Assert.Equal(0, BdxlIn("other", PasswordVar, "catalog", "backup", "restore", backup));
        Assert.Contains(Catalog.Load(tmp.File("other"), "test").Files, f => f.RelPath == "tax-secret-2024.pdf");
    }

    [Fact]
    public void Restage_keeps_hidden_names_and_writes_an_encrypted_manifest()
    {
        StageEncrypted(withCatalog: false);
        Bdxl("burned", "BDXL-0001");
        Assert.Equal(0, Bdxl("lost", "BDXL-0001"));
        Assert.Equal(1, BdxlIn("cat", NoVar, "restage", "BDXL-0001", tmp.File("re")));   // the manifest needs the password

        Assert.Equal(0, Bdxl("restage", "BDXL-0001", tmp.File("re")));
        string rebuilt = Path.Combine(tmp.File("re"), "BDXL-0001");
        Assert.True(File.Exists(Path.Combine(rebuilt, "data", "000", "000001.enc")));
        Assert.True(File.Exists(Path.Combine(rebuilt, "manifest.jsonl.enc")));
        Assert.False(File.Exists(Path.Combine(rebuilt, "manifest.jsonl")));
        Assert.Equal(0, Bdxl("verify", "BDXL-0001", rebuilt, "--deep"));
    }

    [Fact]
    public void Discjson_puts_back_an_encrypted_manifest()
    {
        StageEncrypted(withCatalog: false);
        File.Delete(Path.Combine(Disc, "disc.json"));
        File.Delete(Path.Combine(Disc, "manifest.jsonl.enc"));

        Assert.Equal(0, Bdxl("discjson", "BDXL-0001", Disc));
        Assert.True(File.Exists(Path.Combine(Disc, "manifest.jsonl.enc")));
        Assert.False(File.Exists(Path.Combine(Disc, "manifest.jsonl")));

        Assert.Equal(0, BdxlIn("other", PasswordVar, "scandisc", Disc));   // the rewritten manifest is complete
        Assert.Equal(2, Catalog.Load(tmp.File("other"), "test").Files.Count);
    }
}
