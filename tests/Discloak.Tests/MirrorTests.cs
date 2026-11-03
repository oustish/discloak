namespace Discloak.Tests;

/// <summary>The catalog mirror: kept in step by every save; nothing changes unless it is there and in step.</summary>
public class MirrorTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    string Dir => tmp.File("cat");
    string MirrorDir => tmp.File("mirror");

    int Discloak(params string[] args)
    {
        TestKeys.Init(Dir);
        return Program.Main([.. args, "--catalog", Dir, "--yes", "--kdf-memory", "8MiB", "--kdf-passes", "1"]);
    }

    void Mirrored()
    {
        Assert.Equal(0, Discloak("ignore", "add", "*.tmp"));
        Assert.Equal(0, Discloak("settings", "set", "mirror", MirrorDir));
        Assert.Equal(Mirror.State.InSync, Mirror.Check(Dir, TestKeys.Key(Dir)).State);
    }

    static string[] Names(string dir) => Directory.GetFiles(dir).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void An_empty_folder_gets_a_copy_that_every_save_keeps_in_step_and_that_opens_as_a_catalog()
    {
        Mirrored();
        Assert.Equal(Names(Dir).Where(n => n != Mirror.FileName), Names(MirrorDir));   // the catalog files and discloak.key, no mirror.json
        Assert.False(Directory.Exists(Path.Combine(MirrorDir, Catalog.HistoryFolder)));    // no history

        Assert.Equal(0, Discloak("ignore", "add", "*.bak"));
        Assert.Equal(Mirror.State.InSync, Mirror.Check(Dir, TestKeys.Key(Dir)).State);
        Assert.Contains("*.bak", TestKeys.Load(MirrorDir).Settings.IgnoreRules);           // the mirror is a catalog folder itself
        Assert.Equal(0, Discloak("catalog", "mirror", "status"));
    }

    [Fact]
    public void Without_the_mirror_only_read_only_commands_run()
    {
        Mirrored();
        Directory.Move(MirrorDir, MirrorDir + ".away");                                    // the drive is not connected

        Assert.Equal(4, Discloak("ignore", "add", "*.bak"));
        Assert.DoesNotContain("*.bak", TestKeys.Load(Dir).Settings.IgnoreRules);           // nothing changed
        Assert.Equal(0, Discloak("status"));
        Assert.Equal(0, Discloak("ignore", "list"));
        Assert.Equal(0, Discloak("copies", "list"));
        Assert.Equal(2, Discloak("catalog", "mirror", "status"));
        Environment.SetEnvironmentVariable("DISCLOAK_TEST_NEW_PW", "Brand-New-Pass-99#");
        Assert.Equal(1, Discloak("password", "change", "--new-password-env", "DISCLOAK_TEST_NEW_PW"));
        Assert.NotNull(KeyFile.ReadFrom(Dir)!.TryUnlock(TestKeys.Password));               // the password stayed

        Directory.Move(MirrorDir + ".away", MirrorDir);
        Assert.Equal(0, Discloak("ignore", "add", "*.bak"));
        Assert.Contains("*.bak", TestKeys.Load(MirrorDir).Settings.IgnoreRules);
    }

    [Fact]
    public void A_mirror_that_missed_a_save_is_brought_up_to_date_with_sync()
    {
        Mirrored();
        string before = tmp.File("before");
        CopyDir(MirrorDir, before);
        Assert.Equal(0, Discloak("ignore", "add", "*.bak"));
        Directory.Delete(MirrorDir, true);
        CopyDir(before, MirrorDir);                                                         // as if that save had not reached it

        Assert.Equal(Mirror.State.Differs, Mirror.Check(Dir, TestKeys.Key(Dir)).State);
        Assert.Equal(4, Discloak("ignore", "add", "*.old"));
        Assert.Equal(0, Discloak("catalog", "mirror", "sync"));
        Assert.Equal(Mirror.State.InSync, Mirror.Check(Dir, TestKeys.Key(Dir)).State);
        Assert.Equal(0, Discloak("ignore", "add", "*.old"));
    }

    [Fact]
    public void A_damaged_catalog_is_taken_back_from_the_mirror()
    {
        Mirrored();
        Assert.Equal(0, Discloak("ignore", "add", "*.bak"));
        string settings = Path.Combine(Dir, "settings.json.enc");
        var bytes = File.ReadAllBytes(settings);
        bytes[^3] ^= 1;
        File.WriteAllBytes(settings, bytes);

        Assert.Equal(4, Discloak("status"));
        Assert.Equal(0, Discloak("catalog", "mirror", "restore"));
        var back = TestKeys.Load(Dir);
        Assert.Equal(IntegrityState.Ok, back.Integrity.State);
        Assert.Contains("*.bak", back.Settings.IgnoreRules);
        Assert.Contains(Catalog.ListHistory(Dir), id => id.EndsWith("catalog-mirror-restore"));   // the damaged one is kept
        Assert.Equal(0, Discloak("ignore", "add", "*.old"));
    }

    [Fact]
    public void A_newer_mirror_is_not_overwritten_without_asking_and_can_be_taken_back()
    {
        Mirrored();
        string old = tmp.File("old");
        CopyCatalogFiles(Dir, old);
        Assert.Equal(0, Discloak("ignore", "add", "*.bak"));
        CopyCatalogFiles(old, Dir);                                                          // the catalog went back by hand

        var r = Mirror.Check(Dir, TestKeys.Key(Dir));
        Assert.True(r.MirrorGeneration > r.MainGeneration);
        Assert.Contains("MIRROR is newer", Mirror.Advice(r));
        Assert.Equal(4, Discloak("ignore", "add", "*.old"));
        Assert.Equal(0, Discloak("catalog", "mirror", "restore"));
        Assert.Contains("*.bak", TestKeys.Load(Dir).Settings.IgnoreRules);
    }

    [Fact]
    public void A_password_change_reaches_the_mirror()
    {
        Mirrored();
        Environment.SetEnvironmentVariable("DISCLOAK_TEST_NEW_PW2", "Brand-New-Pass-77#");
        Assert.Equal(0, Discloak("password", "change", "--new-password-env", "DISCLOAK_TEST_NEW_PW2"));
        Assert.NotNull(KeyFile.ReadFrom(MirrorDir)!.TryUnlock("Brand-New-Pass-77#"));
        Assert.Equal(Mirror.State.InSync, Mirror.Check(Dir, KeyFile.ReadFrom(Dir)!.TryUnlock("Brand-New-Pass-77#")).State);
    }

    [Fact]
    public void The_mirror_must_be_an_empty_folder_or_this_catalog_outside_the_catalog_and_can_be_dropped()
    {
        TestKeys.Init(tmp.File("other"));
        Assert.Equal(0, Discloak("status"));
        Assert.Equal(1, Discloak("settings", "set", "mirror", tmp.File("other")));              // another catalog
        Directory.CreateDirectory(tmp.File("junk"));
        File.WriteAllText(tmp.File("junk/x.txt"), "x");
        Assert.Equal(1, Discloak("settings", "set", "mirror", tmp.File("junk")));               // not empty
        Assert.Equal(1, Discloak("settings", "set", "mirror", Path.Combine(Dir, "m")));         // inside the catalog
        Assert.Null(Mirror.PathFor(Dir));

        Mirrored();
        Directory.Move(MirrorDir, MirrorDir + ".away");
        Assert.Equal(0, Discloak("settings", "set", "mirror", "none"));                         // works without the mirror
        Assert.Equal(0, Discloak("ignore", "add", "*.bak"));
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
    }

    static void CopyCatalogFiles(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string n in Catalog.StoredFilesIn(to)) File.Delete(Path.Combine(to, n));
        foreach (string n in Catalog.StoredFilesIn(from).Append(Catalog.IntegrityFile))
            File.Copy(Path.Combine(from, n), Path.Combine(to, n), overwrite: true);
    }
}
