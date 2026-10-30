namespace Bdxl.Tests;

public class CatalogTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    string Dir => tmp.File("cat");
    MasterKey Key => TestKeys.Key(TestKeys.Init(Dir));

    /// <summary>One "command": load, change, save - like Program does.</summary>
    Catalog Run(string command, Action<Catalog> change)
    {
        var cat = Catalog.Load(Dir, command, Key);
        change(cat);
        cat.Save();
        return cat;
    }

    int Bdxl(params string[] args) => Program.Main([.. args, "--catalog", Dir, "--yes", "--kdf-memory", "8MiB", "--kdf-passes", "1"]);

    static FileRecord File(string rel) => new() { Root = @"C:\src", RootName = "S", RelPath = rel, Size = 1, Sha256 = "AB" };

    [Fact]
    public void A_new_catalog_is_encrypted_and_nothing_in_it_is_readable()
    {
        Run("scan", c =>
        {
            c.Files.Add(File("tax-secret-2024.pdf"));
            c.Discs.Add(new DiscRecord { Label = "BDXL-0001", State = "burned" });
            c.Blanks.Add(new BlankDisc { Id = "B0001", Capacity = 100, Reserve = 10 });
            c.Settings.Currency = "PLN";
        });

        Assert.Equal(CatalogKind.Encrypted, Catalog.Detect(Dir));
        Assert.DoesNotContain(Catalog.CoveredFiles, n => System.IO.File.Exists(Path.Combine(Dir, n)));
        foreach (string path in Directory.GetFiles(Dir))
        {
            string text = Encoding.Latin1.GetString(System.IO.File.ReadAllBytes(path));
            Assert.DoesNotContain("tax-secret", text);
            Assert.DoesNotContain("PLN", text);
        }

        var back = Catalog.Load(Dir, "status", Key);
        Assert.Equal(IntegrityState.Ok, back.Integrity.State);
        Assert.Equal("tax-secret-2024.pdf", Assert.Single(back.Files).RelPath);
        Assert.Equal("burned", Assert.Single(back.Discs).State);
        Assert.Equal(90, Assert.Single(back.Blanks).Usable);
        Assert.Equal("PLN", back.Settings.Currency);
    }

    [Fact]
    public void A_damaged_file_breaks_the_catalog_and_blocks_saving()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        var bytes = System.IO.File.ReadAllBytes(Path.Combine(Dir, "files.jsonl.enc"));
        bytes[^5] ^= 1;
        System.IO.File.WriteAllBytes(Path.Combine(Dir, "files.jsonl.enc"), bytes);

        var cat = Catalog.Load(Dir, "plan", Key);
        Assert.Equal(IntegrityState.Broken, cat.Integrity.State);
        Assert.Contains(cat.Integrity.Problems, p => p.StartsWith("files.jsonl.enc"));
        Assert.Empty(cat.Files);                                   // a broken catalog is not loaded
        Assert.Throws<InvalidOperationException>(cat.Save);        // and never overwritten
        Assert.Equal(IntegrityState.Broken, Catalog.CheckIntegrity(Dir, null).State);   // damage shows without the key
    }

    [Fact]
    public void Files_from_different_saves_fail_the_hmac_even_with_their_hashes_put_right()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        Run("scan", c => c.Discs.Add(new DiscRecord { Label = "BDXL-0001" }));
        string old = Catalog.ListHistory(Dir)[0];
        System.IO.File.Copy(Path.Combine(Dir, Catalog.HistoryFolder, old, "discs.jsonl.enc"), Path.Combine(Dir, "discs.jsonl.enc"), overwrite: true);

        // someone without the key fixes up the hash list: the HMAC no longer matches
        var manifest = JsonStore.ReadObject<IntegrityManifest>(Path.Combine(Dir, Catalog.IntegrityFile))!;
        manifest.Files["discs.jsonl.enc"] = Hashing.HashFile(Path.Combine(Dir, "discs.jsonl.enc"));
        JsonStore.WriteObject(Path.Combine(Dir, Catalog.IntegrityFile), manifest);

        Assert.Equal(IntegrityState.Ok, Catalog.CheckIntegrity(Dir, null).State);
        var cat = Catalog.Load(Dir, "status", Key);
        Assert.Equal(IntegrityState.Broken, cat.Integrity.State);
        Assert.Contains(cat.Integrity.Problems, p => p.Contains("HMAC"));

        Assert.Equal(4, Bdxl("status"));
        Assert.Equal(0, Bdxl("catalog", "accept"));                  // every file still decrypts: the user may accept it
        Assert.Equal(0, Bdxl("status"));
    }

    [Fact]
    public void One_snapshot_per_command_however_often_it_saves()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));             // after init: one version to keep (the empty catalog)
        var cat = Catalog.Load(Dir, "stage", Key);
        cat.Files.Add(File("b.txt")); cat.Save();
        cat.Files.Add(File("c.txt")); cat.Save();

        var history = Catalog.ListHistory(Dir);
        Assert.Equal(2, history.Count);
        Assert.EndsWith("-stage", history[0]);
        Assert.Empty(Catalog.VerifySnapshot(Dir, history[0], Key));
        Assert.False(System.IO.File.Exists(Path.Combine(Dir, Catalog.HistoryFolder, history[0], KeyFile.FileName)));   // never the key file
    }

    [Fact]
    public void History_is_pruned_to_history_keep()
    {
        Run("settings", c => c.Settings.HistoryKeep = 2);
        for (int i = 0; i < 5; i++) Run("scan", c => c.Files.Add(File($"f{i}.txt")));
        Assert.Equal(2, Catalog.ListHistory(Dir).Count);
    }

    [Fact]
    public void Rollback_brings_an_older_version_back_and_can_itself_be_undone()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        Run("scan", c => c.Files.Add(File("b.txt")));
        string before = Catalog.ListHistory(Dir)[0];   // the state with only a.txt

        Catalog.RestoreSnapshot(Dir, before, "rollback", keep: 10, Key);

        var cat = Catalog.Load(Dir, "status", Key);
        Assert.Equal(IntegrityState.Ok, cat.Integrity.State);
        Assert.Equal(["a.txt"], cat.Files.Select(f => f.RelPath));
        Assert.Equal(3, Catalog.ListHistory(Dir).Count);           // the state with b.txt was kept as well
    }

    [Fact]
    public void A_tampered_snapshot_is_reported()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        Run("scan", c => c.Files.Add(File("b.txt")));
        string id = Catalog.ListHistory(Dir)[0];
        System.IO.File.AppendAllText(Path.Combine(Dir, Catalog.HistoryFolder, id, "files.jsonl.enc"), "x");

        Assert.Contains(Catalog.VerifySnapshot(Dir, id, Key), p => p.StartsWith("files.jsonl.enc: contents do not match"));
    }

    [Fact]
    public void Commands_need_init_first_and_the_right_password()
    {
        Assert.Equal(1, Bdxl("status"));
        Assert.Equal(0, Bdxl("init"));
        Assert.Equal(1, Bdxl("init"));                               // only once
        Assert.Equal(0, Bdxl("status"));

        Environment.SetEnvironmentVariable("BDXL_TEST_WRONG_PW", "Wrong-Horse-42!!");
        Assert.Equal(1, Bdxl("status", "--password-env", "BDXL_TEST_WRONG_PW"));
    }

    // ---- migration of a plaintext catalog ------------------------------------------------------------------------

    /// <summary>A catalog as the version before encryption wrote it: plain JSON + SHA-256 hash file, one older version in history.</summary>
    void WritePlainCatalog(string dir)
    {
        void Write(string folder, string relPath)
        {
            Directory.CreateDirectory(folder);
            JsonStore.WriteLines(Path.Combine(folder, "files.jsonl"), [File(relPath)]);
            JsonStore.WriteLines(Path.Combine(folder, "discs.jsonl"), new List<DiscRecord>());
            JsonStore.WriteObject(Path.Combine(folder, "settings.json"), new Settings { Currency = "PLN" });
            var m = new IntegrityManifest { CreatedUtc = DateTime.UtcNow };
            foreach (string n in new[] { "files.jsonl", "discs.jsonl", "settings.json" }) m.Files[n] = Hashing.HashFile(Path.Combine(folder, n));
            JsonStore.WriteObject(Path.Combine(folder, Catalog.IntegrityFile), m);
        }
        Write(Path.Combine(dir, Catalog.HistoryFolder, "20260101-120000-scan"), "old.txt");
        Write(dir, "secret-plans.txt");
    }

    [Fact]
    public void A_plaintext_catalog_and_its_history_are_encrypted_and_the_plaintext_deleted()
    {
        WritePlainCatalog(Dir);
        Assert.Equal(CatalogKind.Plain, Catalog.Detect(Dir));

        Assert.Equal(0, Bdxl("status"));                             // any command offers it (--yes accepts)
        Assert.Equal(CatalogKind.Encrypted, Catalog.Detect(Dir));
        var all = Directory.GetFiles(Dir, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(all, p => Catalog.CoveredFiles.Contains(Path.GetFileName(p)));
        Assert.DoesNotContain(all, p => Encoding.Latin1.GetString(System.IO.File.ReadAllBytes(p)).Contains("secret-plans"));
        Assert.False(Directory.Exists(Path.Combine(Dir, ".plaintext")));

        var cat = TestKeys.Load(Dir);
        Assert.Equal("secret-plans.txt", Assert.Single(cat.Files).RelPath);
        Assert.Equal("PLN", cat.Settings.Currency);
        string old = Assert.Single(Catalog.ListHistory(Dir));
        Assert.Empty(Catalog.VerifySnapshot(Dir, old, TestKeys.Key(Dir)));

        Assert.Equal(0, Bdxl("catalog", "rollback", old));          // the old version is usable
        Assert.Equal("old.txt", Assert.Single(TestKeys.Load(Dir).Files).RelPath);
    }

    [Fact]
    public void A_damaged_plaintext_catalog_is_only_encrypted_with_force()
    {
        WritePlainCatalog(Dir);
        System.IO.File.AppendAllText(Path.Combine(Dir, "files.jsonl"), "\n");
        Assert.Equal(1, Bdxl("init"));
        Assert.Equal(CatalogKind.Plain, Catalog.Detect(Dir));
        Assert.Equal(0, Bdxl("init", "--force"));
        Assert.Single(TestKeys.Load(Dir).Files);
    }

    [Fact]
    public void A_migration_cut_off_before_the_key_file_is_undone()
    {
        WritePlainCatalog(Dir);
        // as if the process died after moving the plaintext away and the encrypted files in, before bdxl.key
        Directory.CreateDirectory(Path.Combine(Dir, ".plaintext"));
        foreach (string n in new[] { "files.jsonl", "discs.jsonl", "settings.json", Catalog.IntegrityFile })
            System.IO.File.Move(Path.Combine(Dir, n), Path.Combine(Dir, ".plaintext", n));
        System.IO.File.WriteAllText(Path.Combine(Dir, "files.jsonl.enc"), "half-written");

        Assert.Equal(0, Bdxl("status"));                             // put back, then migrated properly
        Assert.Equal("secret-plans.txt", Assert.Single(TestKeys.Load(Dir).Files).RelPath);
    }

    // ---- export / import, password, recovery key ------------------------------------------------------------------

    [Fact]
    public void Export_and_import_round_trip_through_plain_json()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        Assert.Equal(0, Bdxl("catalog", "export", tmp.File("plain")));
        Assert.True(System.IO.File.Exists(tmp.File("plain/files.jsonl")));
        Assert.Equal(1, Bdxl("catalog", "export", tmp.File("plain")));   // not over existing files

        var files = JsonStore.ReadLines<FileRecord>(tmp.File("plain/files.jsonl"));
        files.Add(File("edited-by-hand.txt"));
        JsonStore.WriteLines(tmp.File("plain/files.jsonl"), files);
        Assert.Equal(0, Bdxl("catalog", "import", tmp.File("plain")));
        Assert.Equal(2, TestKeys.Load(Dir).Files.Count);
    }

    [Fact]
    public void A_password_change_keeps_everything_and_the_recovery_key_replaces_a_lost_password()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        string keyBefore = Convert.ToHexString(TestKeys.Key(Dir).Bytes);

        Environment.SetEnvironmentVariable("BDXL_TEST_NEW_PW", "Brand-New-Pass-99#");
        Environment.SetEnvironmentVariable("BDXL_TEST_WRONG_PW", "Wrong-Horse-42!!");
        Assert.Equal(1, Bdxl("password", "change", "--password-env", "BDXL_TEST_WRONG_PW", "--new-password-env", "BDXL_TEST_NEW_PW"));
        Assert.Equal(0, Bdxl("password", "change", "--new-password-env", "BDXL_TEST_NEW_PW"));
        Assert.Equal(1, Bdxl("status"));                                                     // the old password no longer opens it
        Assert.Equal(0, Bdxl("status", "--password-env", "BDXL_TEST_NEW_PW"));
        using (var key = KeyFile.ReadFrom(Dir)!.TryUnlock("Brand-New-Pass-99#")!)
            Assert.Equal(keyBefore, Convert.ToHexString(key.Bytes));                         // same master key

        var output = new StringWriter();
        var stdout = Console.Out;
        Console.SetOut(output);
        try { Assert.Equal(0, Bdxl("recovery-key", "create", "--password-env", "BDXL_TEST_NEW_PW")); }
        finally { Console.SetOut(stdout); }
        string recovery = output.ToString().Split('\n').Select(l => l.Trim()).Single(l => l.Length == 65 && l.Count(ch => ch == '-') == 10);

        // the password is forgotten: the recovery key sets a new one (the test password again)
        Environment.SetEnvironmentVariable(CatalogSetup.RecoveryKeyEnv, recovery);
        Environment.SetEnvironmentVariable("BDXL_TEST_BACK_PW", TestKeys.Password);
        try { Assert.Equal(0, Bdxl("password", "change", "--recovery", "--new-password-env", "BDXL_TEST_BACK_PW")); }
        finally { Environment.SetEnvironmentVariable(CatalogSetup.RecoveryKeyEnv, null); }
        Assert.Equal(0, Bdxl("status"));
        Assert.Single(TestKeys.Load(Dir).Files);
    }

    [Fact]
    public void A_backup_restores_into_an_empty_folder_and_into_its_own_catalog_but_not_another()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        Assert.Equal(0, Bdxl("catalog", "backup", "create", tmp.File("bk")));
        string backup = Directory.GetDirectories(tmp.File("bk")).Single();
        Assert.Contains(Directory.GetFiles(backup), p => Path.GetFileName(p) == KeyFile.FileName);

        Assert.Equal(0, Program.Main(["catalog", "backup", "restore", backup, "--catalog", tmp.File("fresh")]));
        Assert.Equal("a.txt", Assert.Single(TestKeys.Load(tmp.File("fresh")).Files).RelPath);

        Run("scan", c => c.Files.Add(File("b.txt")));
        Assert.Equal(1, Bdxl("catalog", "backup", "restore", backup));                   // exists: needs --force
        Assert.Equal(0, Bdxl("catalog", "backup", "restore", backup, "--force"));
        Assert.Single(TestKeys.Load(Dir).Files);

        TestKeys.Init(tmp.File("other"));
        Assert.Equal(1, Program.Main(["catalog", "backup", "restore", backup, "--catalog", tmp.File("other"), "--force"]));
    }
}
