namespace Bdxl.Tests;

/// <summary>password change --rotate-key: a new master key for everything new; older keys stay in the ring for older discs.</summary>
public class KeyRotationTests : IDisposable
{
    const string NewPw = "Rotated-Pass-2026!";
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    static KeyRotationTests() => Environment.SetEnvironmentVariable("BDXL_TEST_ROTATED_PW", NewPw);

    int Bdxl(params string[] args) => Program.Main([.. args, "--catalog", TestKeys.Init(tmp.File("cat")), "--yes", "--kdf-memory", "8MiB", "--kdf-passes", "1"]);
    int AfterRotation(params string[] args) => Bdxl([.. args, "--password-env", "BDXL_TEST_ROTATED_PW"]);
    int In(string catalog, string passwordVar, params string[] args) =>
        Program.Main([.. args, "--catalog", tmp.File(catalog), "--password-env", passwordVar, "--yes"]);
    KeyRing Keys => KeyFile.ReadFrom(tmp.File("cat"))!.TryUnlock(NewPw)!;
    string Disc(int n) => tmp.File($"stg/BDXL-000{n}");

    [Fact]
    public void Old_discs_stay_readable_new_ones_use_the_new_key_and_an_old_password_opens_only_the_old()
    {
        var a = tmp.RandomFile("src/a.bin", 50_000, seed: 1);
        Assert.Equal(0, Bdxl("blanks", "add", "2", "--size", "1MB", "--reserve", "100KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--with-catalog"));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));
        Assert.Equal(0, Bdxl("recovery-key", "create"));
        string oldKey = TestKeys.Key(tmp.File("cat")).KeyId;
        var disc1KeyFile = File.ReadAllText(KeyFile.PathIn(Disc(1)));

        // rotate: new password, new master key, the catalog re-encrypted with it, a new recovery key
        Environment.SetEnvironmentVariable("BDXL_NEW_PASSWORD", NewPw);
        try { Assert.Equal(0, Bdxl("password", "change", "--rotate-key")); }
        finally { Environment.SetEnvironmentVariable("BDXL_NEW_PASSWORD", null); }
        Assert.Equal(1, Bdxl("status"));                                                  // the old password is gone
        using var keys = Keys;
        Assert.Equal(2, keys.All.Count);
        Assert.Equal(oldKey, keys.All[0].KeyId);
        Assert.NotEqual(oldKey, keys.KeyId);
        Assert.Equal(keys.KeyId, JsonStore.ReadObject<IntegrityManifest>(Path.Combine(tmp.File("cat"), Catalog.IntegrityFile))!.KeyId);
        Assert.Equal(keys.KeyId, FileCrypto.KeyIdOf(Path.Combine(tmp.File("cat"), "files.000.jsonl.enc")));
        Assert.Equal(0, AfterRotation("catalog", "history"));                            // versions from before (old key) still check out
        Assert.All(Catalog.ListHistory(tmp.File("cat")), id => Assert.Empty(Catalog.VerifySnapshot(tmp.File("cat"), id, keys)));

        // a new disc uses the new key; the old one is still verified and restored through the ring
        var b = tmp.RandomFile("src/b.bin", 50_000, seed: 2);
        Assert.Equal(0, AfterRotation("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, AfterRotation("stage", tmp.File("stg")));
        Assert.Equal(keys.KeyId, FileCrypto.KeyIdOf(Path.Combine(Disc(2), "data", "000", "000001.enc")));
        Assert.Equal(oldKey, FileCrypto.KeyIdOf(Path.Combine(Disc(1), "data", "000", "000001.enc")));
        Assert.Equal(0, AfterRotation("verify", "BDXL-0001", Disc(1), "--deep"));
        Assert.Equal(0, AfterRotation("burned", "BDXL-0002"));
        Assert.Equal(0, AfterRotation("restore", tmp.File("out"), "*", "--mount", Disc(1) + ";" + Disc(2)));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/a.bin")));
        Assert.Equal(b, File.ReadAllBytes(tmp.File("out/S/b.bin")));

        // what each disc's bdxl.key opens: the old disc only the old key (old password), the new one both (new password)
        Assert.Equal([oldKey], KeyFile.ReadFrom(Disc(1))!.TryUnlock(TestKeys.Password)!.All.Select(k => k.KeyId));
        Assert.Null(KeyFile.ReadFrom(Disc(2))!.TryUnlock(TestKeys.Password));
        Assert.Equal(2, KeyFile.ReadFrom(Disc(2))!.TryUnlock(NewPw)!.All.Count);
        Assert.Equal(disc1KeyFile, File.ReadAllText(KeyFile.PathIn(Disc(1))));               // burned discs never change

        // a leaked old password + the old disc: a catalog that reads the old disc, not the new one
        Assert.Equal(0, In("leak", "BDXL_PASSWORD", "init", "--key-from", Disc(1)));
        Assert.Equal(0, In("leak", "BDXL_PASSWORD", "scandisc", Disc(1)));
        Assert.Equal(1, In("leak", "BDXL_PASSWORD", "scandisc", Disc(2)));
        // the newest password + the newest disc: everything
        Assert.Equal(0, In("found", "BDXL_TEST_ROTATED_PW", "init", "--key-from", Disc(2)));
        Assert.Equal(0, In("found", "BDXL_TEST_ROTATED_PW", "scandisc", Disc(1)));
        Assert.Equal(0, In("found", "BDXL_TEST_ROTATED_PW", "scandisc", Disc(2)));
    }

    [Fact]
    public void A_session_and_a_restage_keep_the_disc_s_own_key()
    {
        tmp.RandomFile("src/a.bin", 50_000, seed: 1);
        Assert.Equal(0, Bdxl("blanks", "add", "1", "--size", "1MB", "--reserve", "100KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));
        string oldKey = TestKeys.Key(tmp.File("cat")).KeyId;

        Environment.SetEnvironmentVariable("BDXL_NEW_PASSWORD", NewPw);
        try { Assert.Equal(0, Bdxl("password", "change", "--rotate-key")); }
        finally { Environment.SetEnvironmentVariable("BDXL_NEW_PASSWORD", null); }

        tmp.RandomFile("src/later.bin", 20_000, seed: 3);
        Assert.Equal(0, AfterRotation("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, AfterRotation("stage", tmp.File("stg"), "--append", "BDXL-0001"));
        string session = tmp.File("stg/BDXL-0001.s2");
        Assert.Equal(oldKey, FileCrypto.KeyIdOf(Path.Combine(session, "data", "000", "000002.enc")));
        Assert.Equal(oldKey, FileCrypto.KeyIdOf(Path.Combine(session, DiscFiles.EncryptedManifestFor(2))));
        Assert.Equal(oldKey, JsonStore.ReadObject<DiscInfo>(Path.Combine(session, DiscFiles.InfoName))!.KeyId);

        foreach (string f in Directory.GetFiles(session, "*", SearchOption.AllDirectories))   // burn the session onto the disc
        {
            string dst = Path.Combine(Disc(1), Path.GetRelativePath(session, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
        Assert.Equal(0, AfterRotation("burned", "BDXL-0001"));
        Assert.Equal(0, AfterRotation("verify", "BDXL-0001", Disc(1), "--deep"));

        Assert.Equal(0, AfterRotation("lost", "BDXL-0001"));
        Assert.Equal(0, AfterRotation("restage", "BDXL-0001", tmp.File("re")));
        Assert.All(Directory.GetFiles(tmp.File("re/BDXL-0001/data"), "*.enc", SearchOption.AllDirectories),
                   f => Assert.Equal(oldKey, FileCrypto.KeyIdOf(f)));
        Assert.Equal(0, AfterRotation("verify", "BDXL-0001", tmp.File("re/BDXL-0001"), "--deep"));
    }

    [Fact]
    public void A_broken_catalog_is_not_rotated()
    {
        Assert.Equal(0, Bdxl("blanks", "add", "1", "--size", "1MB"));
        var bytes = File.ReadAllBytes(Path.Combine(tmp.File("cat"), "blanks.jsonl.enc"));
        bytes[^3] ^= 1;
        File.WriteAllBytes(Path.Combine(tmp.File("cat"), "blanks.jsonl.enc"), bytes);

        Environment.SetEnvironmentVariable("BDXL_NEW_PASSWORD", NewPw);
        try { Assert.Equal(1, Bdxl("password", "change", "--rotate-key")); }
        finally { Environment.SetEnvironmentVariable("BDXL_NEW_PASSWORD", null); }
        Assert.Single(TestKeys.Key(tmp.File("cat")).All);                                // nothing changed: old password, one key
    }
}
