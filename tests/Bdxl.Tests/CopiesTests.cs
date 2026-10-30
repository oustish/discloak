namespace Bdxl.Tests;

/// <summary>'copies': more than one copy of a location's files, each on a different disc; only burned copies count as made.</summary>
public class CopiesTests : IDisposable
{
    readonly Catalog cat = Catalog.CreateEmpty(Path.Combine(Path.GetTempPath(), "bdxl-never-saved"), "test");
    Archive Arc => new(cat);
    readonly TempDir tmp = new();
    public void Dispose() => tmp.Dispose();

    FileRecord AddFile(string alias, string rel, long size, string sha)
    {
        var f = new FileRecord { Root = @"C:\" + alias, RootName = alias, RelPath = rel, Size = size, Sha256 = sha };
        cat.Files.Add(f);
        return f;
    }

    void AddBlanks(int count, long usable)
    {
        for (int i = 0; i < count; i++) cat.Blanks.Add(new BlankDisc { Id = $"B{cat.Blanks.Count + 1:D4}", Capacity = usable });
    }

    static BackupRef On(string disc, string sha, bool burned = true) =>
        new() { Disc = disc, DiscPath = "data/000/000001.enc", Sha256 = sha, BackedUpUtc = burned ? DateTime.UtcNow : null };

    [Fact]
    public void A_location_that_wants_two_copies_gets_them_on_two_different_discs()
    {
        cat.Settings.CopiesByAlias["photos"] = 2;
        AddBlanks(2, 1_000_000);
        AddFile("photos", "a.jpg", 1000, "AA");
        AddFile("photos", "b.jpg", 1000, "BB");
        AddFile("other", "c.txt", 1000, "CC");                       // the default: one copy

        var plan = Arc.MakePlan();
        Assert.Equal(5, plan.Pending.Count);
        Assert.Empty(plan.Unplaced);
        foreach (string sha in new[] { "AA", "BB" })
            Assert.Equal(2, plan.Bins.Count(b => b.Pieces.Any(p => p.File.Sha256 == sha)));   // one copy per disc
        Assert.Equal(0, plan.FurtherCopies);                         // nothing is on a disc yet
    }

    [Fact]
    public void Two_copies_never_share_a_disc_even_if_it_has_room()
    {
        cat.Settings.Copies = 2;
        AddBlanks(1, 1_000_000);
        AddFile("photos", "a.jpg", 1000, "AA");

        var plan = Arc.MakePlan();
        Assert.Single(plan.Bins[0].Pieces);
        Assert.Single(plan.Unplaced);
    }

    [Fact]
    public void A_burned_or_staged_copy_counts_for_planning_and_only_the_missing_copies_are_pending()
    {
        cat.Settings.Copies = 3;
        AddBlanks(3, 1_000_000);
        var f = AddFile("photos", "a.jpg", 1000, "AA");
        f.Backups.Add(On("D1", "AA"));
        f.Backups.Add(On("D2", "AA", burned: false));                // staged: no copy is planned for it again
        cat.Discs.Add(new DiscRecord { Label = "D1", State = "burned" });
        cat.Discs.Add(new DiscRecord { Label = "D2", State = "staged" });

        var plan = Arc.MakePlan();
        Assert.Single(plan.Pending);
        Assert.Equal(1, plan.FurtherCopies);
        Assert.Equal(1, Archive.BurnedCopies(f, Arc.DeadDiscs()));   // ... but only the burned one is made
        Assert.Single(Arc.TooFewCopies());

        cat.Discs[0].State = "lost";                                 // a lost disc stops counting
        Assert.Equal(2, Arc.MakePlan().Pending.Count);
        Assert.Equal(0, Archive.BurnedCopies(f, Arc.DeadDiscs()));
    }

    [Fact]
    public void Content_in_two_locations_gets_the_higher_number()
    {
        AddBlanks(3, 1_000_000);
        cat.Settings.CopiesByAlias["important"] = 3;
        AddFile("scratch", "a.bin", 1000, "AA");
        AddFile("important", "copy-of-a.bin", 1000, "AA");

        var plan = Arc.MakePlan();
        Assert.Equal(3, plan.Pending.Count);
        Assert.Equal(3, plan.Bins.Count(b => b.Pieces.Count == 1));
    }

    [Fact]
    public void A_further_copy_does_not_go_onto_a_disc_that_holds_the_content_already()
    {
        cat.Settings.Copies = 2;
        var f = AddFile("photos", "a.jpg", 1000, "AA");
        f.Backups.Add(On("D1", "AA"));
        cat.Discs.Add(new DiscRecord { Label = "D1", State = "burned" });

        var onD1 = Arc.MakePlan(into: [new BlankDisc { Id = "D1", Capacity = 1_000_000 }]);   // stage --append D1
        Assert.Empty(onD1.Bins[0].Pieces);
        Assert.Single(onD1.Unplaced);
        var onD2 = Arc.MakePlan(into: [new BlankDisc { Id = "D2", Capacity = 1_000_000 }]);
        Assert.Single(onD2.Bins[0].Pieces);
    }

    [Fact]
    public void A_split_file_gets_each_part_on_as_many_discs_and_its_copies_apart()
    {
        cat.Settings.Copies = 2;
        AddBlanks(6, 10_000);
        AddFile("video", "big.mkv", 15_000, "AA");

        var plan = Arc.MakePlan(split: true);
        Assert.Empty(plan.Unplaced);
        var parts = plan.Pending.GroupBy(p => p.Part).ToList();
        Assert.All(parts, g => Assert.Equal(2, g.Count()));                                     // every part twice
        Assert.All(plan.Bins, b => Assert.True(b.Pieces.Count <= 1));                          // no disc holds two pieces of it
    }

    [Fact]
    public void Copies_end_to_end_stage_burn_lose_and_replan()
    {
        const string pw = "BDXL_TEST_COPIES_PW";
        Environment.SetEnvironmentVariable(pw, "Correct-Horse-42!");
        int Bdxl(params string[] args)
        {
            TestKeys.Init(tmp.File("cat"));
            return Program.Main([.. args, "--catalog", tmp.File("cat"), "--password-env", pw, "--yes"]);
        }
        var a = tmp.RandomFile("src/a.bin", 100_000, seed: 1);
        Assert.Equal(0, Bdxl("blanks", "add", "3", "--size", "1MB", "--reserve", "50KB"));
        Assert.Equal(0, Bdxl("scan", tmp.File("src"), "--name", "S"));
        Assert.Equal(0, Bdxl("copies", "set", "S", "2"));
        Assert.Equal(2, Bdxl("copies", "list"));                                                 // fewer than wanted

        Assert.Equal(0, Bdxl("stage", tmp.File("stg"), "--max-discs", "3"));
        Assert.True(Directory.Exists(tmp.File("stg/BDXL-0001")) && Directory.Exists(tmp.File("stg/BDXL-0002")));
        Assert.False(Directory.Exists(tmp.File("stg/BDXL-0003")));                               // two copies, two discs
        Assert.Equal(0, Bdxl("burned", "BDXL-0001"));
        Assert.Equal(2, Bdxl("copies", "list"));                                                 // the staged one does not count yet
        Assert.Equal(0, Bdxl("burned", "BDXL-0002"));
        Assert.Equal(0, Bdxl("copies", "list"));

        // either disc restores the file
        Assert.Equal(0, Bdxl("restore", tmp.File("out"), "*", "--mount", tmp.File("stg/BDXL-0002")));
        Assert.Equal(a, File.ReadAllBytes(tmp.File("out/S/a.bin")));

        Assert.Equal(0, Bdxl("lost", "BDXL-0001"));
        Assert.Equal(2, Bdxl("copies", "list"));
        Assert.Equal(0, Bdxl("stage", tmp.File("stg")));
        Assert.True(Directory.Exists(tmp.File("stg/BDXL-0003")));                                // the lost copy is made again

        Assert.Equal(0, Bdxl("alias", "rename", "S", "T"));                                      // the number goes with the name
        var settings = TestKeys.Load(tmp.File("cat")).Settings;
        Assert.Equal(2, settings.CopiesFor("T"));
    }
}
