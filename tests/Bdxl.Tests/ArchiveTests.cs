namespace Bdxl.Tests;

/// <summary>Dedup and planning rules, above all for split files (see HANDOFF.md "Split files").</summary>
public class ArchiveTests
{
    readonly Catalog cat = Catalog.CreateEmpty(Path.Combine(Path.GetTempPath(), "bdxl-never-saved"), "test");
    Archive Arc => new(cat);

    FileRecord AddFile(string rel, long size, string sha)
    {
        var f = new FileRecord { Root = @"C:\src", RootName = "S", RelPath = rel, Size = size, Sha256 = sha };
        cat.Files.Add(f);
        return f;
    }

    void AddBlanks(int count, long usable)
    {
        for (int i = 0; i < count; i++) cat.Blanks.Add(new BlankDisc { Id = $"B{cat.Blanks.Count + 1:D4}", Capacity = usable });
    }

    static BackupRef Whole(string disc, string sha, bool burned = true, bool alias = false) =>
        new() { Disc = disc, DiscPath = "S/x", Sha256 = sha, BackedUpUtc = burned ? DateTime.UtcNow : null, Alias = alias };

    static BackupRef Part(string disc, string sha, int part, int parts, long partSize, long fileSize) => new()
    {
        Disc = disc, DiscPath = "S/big" + SplitLayout.Suffix(part, parts), Sha256 = sha, BackedUpUtc = DateTime.UtcNow,
        Part = part, Parts = parts, Offset = (part - 1) * partSize, Length = Math.Min(partSize, fileSize - (part - 1) * partSize),
        PartSha256 = "P" + part
    };

    // ---- CompleteCopy ---------------------------------------------------------------------

    [Fact]
    public void A_whole_copy_is_complete() =>
        Assert.Single(Archive.CompleteCopy([Whole("D1", "AA")], "AA")!);

    [Fact]
    public void Hash_comparison_ignores_case() =>
        Assert.NotNull(Archive.CompleteCopy([Whole("D1", "aa")], "AA"));

    [Fact]
    public void Some_parts_are_not_a_copy()
    {
        var refs = new[] { Part("D1", "AA", 1, 3, 100, 250), Part("D2", "AA", 2, 3, 100, 250) };
        Assert.Null(Archive.CompleteCopy(refs, "AA"));
    }

    [Fact]
    public void All_parts_of_one_layout_are_a_copy_in_part_order()
    {
        var refs = new[] { Part("D3", "AA", 3, 3, 100, 250), Part("D1", "AA", 1, 3, 100, 250), Part("D2", "AA", 2, 3, 100, 250) };
        Assert.Equal([1, 2, 3], Archive.CompleteCopy(refs, "AA")!.Select(r => r.Part));
    }

    [Fact]
    public void Parts_of_different_layouts_do_not_add_up()
    {
        // parts 1+2 of a 100-byte layout and part 3 of a 90-byte layout: three part numbers, but no complete layout
        var refs = new[] { Part("D1", "AA", 1, 3, 100, 250), Part("D2", "AA", 2, 3, 100, 250), Part("D3", "AA", 3, 3, 90, 250) };
        Assert.Null(Archive.CompleteCopy(refs, "AA"));
    }

    [Fact]
    public void Prefers_burned_then_real_copies()
    {
        var staged = Whole("D1", "AA", burned: false);
        var alias = Whole("D2", "AA", alias: true);
        var real = Whole("D3", "AA");
        Assert.Same(real, Archive.CompleteCopy([staged, alias, real], "AA")![0]);
        Assert.Same(alias, Archive.CompleteCopy([staged, alias], "AA")![0]);
    }

    [Fact]
    public void Copies_of_other_content_do_not_count() =>
        Assert.Null(Archive.CompleteCopy([Whole("D1", "BB")], "AA"));

    // ---- MakePlan -------------------------------------------------------------------------

    [Fact]
    public void Identical_content_is_planned_once()
    {
        AddBlanks(1, 1000);
        AddFile("a.bin", 100, "AA");
        AddFile("copy/a.bin", 100, "AA");
        AddFile("b.bin", 50, "BB");

        var plan = Arc.MakePlan();
        Assert.Equal(2, plan.Pending.Count);
        Assert.Equal(150, plan.PendingBytes);
    }

    [Fact]
    public void Ignored_missing_and_unhashed_files_are_not_pending()
    {
        AddBlanks(1, 1000);
        AddFile("ignored.tmp", 10, "").Ignored = true;
        AddFile("gone.bin", 10, "AA").MissingSinceUtc = DateTime.UtcNow;
        AddFile("unhashed.bin", 10, "");
        Assert.Empty(Arc.MakePlan().Pending);
    }

    [Fact]
    public void A_copy_on_a_lost_disc_does_not_count()
    {
        AddBlanks(1, 1000);
        AddFile("a.bin", 100, "AA").Backups.Add(Whole("D1", "AA"));
        Assert.Empty(Arc.MakePlan().Pending);

        cat.Discs.Add(new DiscRecord { Label = "D1", State = "lost" });
        Assert.Single(Arc.MakePlan().Pending);
    }

    [Fact]
    public void Too_big_without_split_is_reported_and_unplaced()
    {
        AddBlanks(2, 10_000);
        var big = AddFile("big.bin", 25_000, "AA");

        var plan = Arc.MakePlan(split: false);
        Assert.Equal([big], plan.TooBig);
        Assert.Equal(Piece.Whole(big), Assert.Single(plan.Unplaced));
        Assert.All(plan.Bins, b => Assert.Empty(b.Pieces));
    }

    [Fact]
    public void Split_cuts_into_parts_that_fit_even_encrypted()
    {
        AddBlanks(3, 10_000);
        AddFile("big.bin", 25_000, "AA");

        var plan = Arc.MakePlan(split: true);
        long partSize = FileCrypto.MaxPlainSize(10_000);
        Assert.Empty(plan.TooBig);
        Assert.Empty(plan.Unplaced);
        Assert.Equal(3, plan.Pending.Count);
        Assert.All(plan.Pending, p => Assert.True(p.Length <= partSize));
        Assert.Equal(25_000, plan.Pending.Sum(p => p.Length));
        Assert.All(plan.Bins, b => Assert.Single(b.Pieces));   // one part per disc
    }

    [Fact]
    public void Only_missing_parts_are_pending_and_the_layout_is_kept_without_split()
    {
        AddBlanks(1, 10_000);   // the largest blank changed since the first part was staged
        var big = AddFile("big.bin", 250, "AA");
        big.Backups.Add(Part("D1", "AA", 1, 3, 100, 250));
        big.Backups.Add(Part("D3", "AA", 3, 3, 100, 250));

        var plan = Arc.MakePlan(split: false);
        var p = Assert.Single(plan.Pending);
        Assert.Equal((2, 3, 100L, 100L), (p.Part, p.Parts, p.Offset, p.Length));
        Assert.Empty(plan.TooBig);
    }

    [Fact]
    public void A_complete_split_copy_means_nothing_is_pending()
    {
        AddBlanks(1, 10_000);
        var big = AddFile("big.bin", 250, "AA");
        for (int i = 1; i <= 3; i++) big.Backups.Add(Part($"D{i}", "AA", i, 3, 100, 250));
        Assert.Empty(Arc.MakePlan().Pending);
    }

    // ---- LinkDuplicates -------------------------------------------------------------------

    [Fact]
    public void Duplicates_get_alias_refs_only_for_a_complete_copy()
    {
        var big = AddFile("big.bin", 250, "AA");
        var dup = AddFile("dup.bin", 250, "AA");
        big.Backups.Add(Part("D1", "AA", 1, 3, 100, 250));
        big.Backups.Add(Part("D2", "AA", 2, 3, 100, 250));

        Assert.Equal(0, Arc.LinkDuplicates());
        Assert.Empty(dup.Backups);

        big.Backups.Add(Part("D3", "AA", 3, 3, 100, 250));
        Assert.Equal(1, Arc.LinkDuplicates());
        Assert.Equal(3, dup.Backups.Count);
        Assert.All(dup.Backups, b => Assert.True(b.Alias));
        Assert.Equal(0, Arc.LinkDuplicates());   // nothing more to do
    }

    // ---- search / labels ------------------------------------------------------------------

    [Fact]
    public void Matches_path_hash_prefix_and_real_disc_path_but_not_an_alias_disc_path()
    {
        var f = AddFile("photos/2020/a.jpg", 1, "ABCDEF");
        f.Backups.Add(new BackupRef { Disc = "D1", DiscPath = "Old/name.jpg", Sha256 = "ABCDEF" });
        var dup = AddFile("b.jpg", 1, "ABCDEF");
        dup.Backups.Add(new BackupRef { Disc = "D1", DiscPath = "Old/name.jpg", Sha256 = "ABCDEF", Alias = true });

        Assert.True(Archive.Matches(f, "S/photos/2020"));
        Assert.True(Archive.Matches(f, "abcd"));
        Assert.True(Archive.Matches(f, "Old/name"));
        Assert.False(Archive.Matches(dup, "Old/name"));
        Assert.True(Archive.Matches(dup, "*"));
    }

    [Fact]
    public void Labels_continue_after_existing_ones_and_never_reuse_a_number()
    {
        cat.Discs.Add(new DiscRecord { Label = "BDXL-0007" });
        Assert.Equal("BDXL-0008", Arc.NextLabel());

        cat.Settings.LabelPrefix = "ARC-";
        Assert.Equal("ARC-0009", Arc.NextLabel());   // the counter is global, the prefix is not part of it
    }
}
