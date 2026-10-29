namespace Bdxl.Tests;

public class CatalogTests : IDisposable
{
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    string Dir => tmp.File("cat");

    /// <summary>One "command": load, change, save - like Program does.</summary>
    Catalog Run(string command, Action<Catalog> change)
    {
        var cat = Catalog.Load(Dir, command);
        change(cat);
        cat.Save();
        return cat;
    }

    static FileRecord File(string rel) => new() { Root = @"C:\src", RootName = "S", RelPath = rel, Size = 1, Sha256 = "AB" };

    [Fact]
    public void An_empty_folder_is_fresh() => Assert.Equal(IntegrityState.Fresh, Catalog.Load(Dir, "status").Integrity.State);

    [Fact]
    public void Save_and_load_round_trip_with_integrity_ok()
    {
        Run("scan", c =>
        {
            c.Files.Add(File("a.txt"));
            c.Discs.Add(new DiscRecord { Label = "BDXL-0001", State = "burned" });
            c.Blanks.Add(new BlankDisc { Id = "B0001", Capacity = 100, Reserve = 10 });
            c.Settings.Currency = "PLN";
        });

        var back = Catalog.Load(Dir, "status");
        Assert.Equal(IntegrityState.Ok, back.Integrity.State);
        Assert.Equal("a.txt", Assert.Single(back.Files).RelPath);
        Assert.Equal("burned", Assert.Single(back.Discs).State);
        Assert.Equal(90, Assert.Single(back.Blanks).Usable);
        Assert.Equal("PLN", back.Settings.Currency);
    }

    [Fact]
    public void An_edit_outside_bdxl_breaks_the_catalog_and_blocks_saving()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        System.IO.File.AppendAllText(Path.Combine(Dir, "files.jsonl"), "\n");

        var cat = Catalog.Load(Dir, "plan");
        Assert.Equal(IntegrityState.Broken, cat.Integrity.State);
        Assert.Contains(cat.Integrity.Problems, p => p.StartsWith("files.jsonl"));
        Assert.Empty(cat.Files);                                   // a broken catalog is not loaded
        Assert.Throws<InvalidOperationException>(cat.Save);        // and never overwritten
    }

    [Fact]
    public void A_missing_hash_file_means_legacy_and_is_fixed_by_the_next_save()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        System.IO.File.Delete(Path.Combine(Dir, Catalog.IntegrityFile));

        var cat = Catalog.Load(Dir, "scan");
        Assert.Equal(IntegrityState.Legacy, cat.Integrity.State);
        Assert.Single(cat.Files);
        cat.Save();
        Assert.Equal(IntegrityState.Ok, Catalog.CheckIntegrity(Dir).State);
    }

    [Fact]
    public void One_snapshot_per_command_however_often_it_saves()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));             // first save ever: nothing to keep yet
        Assert.Empty(Catalog.ListHistory(Dir));

        var cat = Catalog.Load(Dir, "stage");
        cat.Files.Add(File("b.txt")); cat.Save();
        cat.Files.Add(File("c.txt")); cat.Save();

        var history = Catalog.ListHistory(Dir);
        Assert.Single(history);
        Assert.EndsWith("-stage", history[0]);
        Assert.Empty(Catalog.VerifySnapshot(Dir, history[0]));
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
        string before = Assert.Single(Catalog.ListHistory(Dir));   // the state with only a.txt

        Catalog.RestoreSnapshot(Dir, before, "rollback", keep: 10);

        var cat = Catalog.Load(Dir, "status");
        Assert.Equal(IntegrityState.Ok, cat.Integrity.State);
        Assert.Equal(["a.txt"], cat.Files.Select(f => f.RelPath));
        Assert.Equal(2, Catalog.ListHistory(Dir).Count);           // the state with b.txt was kept as well
    }

    [Fact]
    public void A_tampered_snapshot_is_reported()
    {
        Run("scan", c => c.Files.Add(File("a.txt")));
        Run("scan", c => c.Files.Add(File("b.txt")));
        string id = Catalog.ListHistory(Dir)[0];
        System.IO.File.AppendAllText(Path.Combine(Dir, Catalog.HistoryFolder, id, "files.jsonl"), "x");

        Assert.Contains("files.jsonl: hash mismatch", Catalog.VerifySnapshot(Dir, id));
    }
}
