namespace Discloak.Tests;

/// <summary>Dedup and planning rules, above all for split files (see HANDOFF.md "Split files").</summary>
public class ArchiveTests
{
    readonly Catalog cat = Catalog.CreateEmpty(Path.Combine(Path.GetTempPath(), "discloak-never-saved"), "test");
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
        new() { Disc = disc, At = 0, StoredSize = 1, Sha256 = sha, BackedUpUtc = burned ? DateTime.UtcNow : null, Alias = alias };

    static BackupRef Part(string disc, string sha, int part, int parts, long partSize, long fileSize) => new()
    {
        Disc = disc, At = (part - 1) * partSize, Sha256 = sha, BackedUpUtc = DateTime.UtcNow,
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
        AddBlanks(2, 400_000);
        var big = AddFile("big.bin", 1_000_000, "AA");

        var plan = Arc.MakePlan(split: false);
        Assert.Equal([big], plan.TooBig);
        Assert.Equal(Piece.Whole(big), Assert.Single(plan.Unplaced));
        Assert.All(plan.Bins, b => Assert.Empty(b.Pieces));
    }

    [Fact]
    public void The_manifest_share_is_never_less_than_the_real_line()
    {
        foreach (string rel in new[] { "a.txt", "zdjęcia/łódź \"nad\" morzem\\x.jpg", new string('x', 300) + "/y.bin" })
        {
            var f = AddFile(rel, 123_456_789_012, new string('A', 64));
            f.ModifiedUtc = new DateTime(2026, 10, 30, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(9999);
            var whole = new BackupRef
            {
                Disc = "DISC-0001", At = 96_000_000_000, Sha256 = f.Sha256, StoredSize = 123_456_799_999, Length = f.Size, Compression = 19
            };
            Assert.True(JsonStore.LinesToBytes([ManifestLine.For(whole, f)]).Length <= DiscSpace.ManifestShare(Piece.Whole(f)));
            var part = new BackupRef
            {
                Disc = "DISC-0001", At = 1_234_567_890, Sha256 = f.Sha256, StoredSize = 96_000_000_000, Part = 12, Parts = 13,
                Offset = 1_100_000_000_000, Length = 96_000_000_000, PartSha256 = new string('C', 64)
            };
            Assert.True(JsonStore.LinesToBytes([ManifestLine.For(part, f)]).Length <= DiscSpace.ManifestShare(new Piece(f, 12, 13, 0, 0)));
        }
    }

    [Fact]
    public void Small_files_take_only_their_bytes_and_a_manifest_line()
    {
        AddBlanks(1, 1_000_000);
        for (int i = 0; i < 2000; i++) AddFile($"small/{i:D4}.txt", 10, $"{i:X4}");   // in packs: no sectors or file entries of their own

        var plan = Arc.MakePlan();
        long each = 10 + DiscSpace.ManifestShare(Piece.Whole(cat.Files[0]));
        Assert.Equal(DiscFormat.Room(1_000_000), plan.Bins[0].Usable);
        Assert.Equal(Math.Min(2000, DiscFormat.Room(1_000_000) / each), plan.Bins[0].Pieces.Count);
        Assert.True(plan.Bins[0].Used <= plan.Bins[0].Usable);
    }

    [Fact]
    public void A_disc_s_room_is_what_its_packs_can_hold()
    {
        AddBlanks(1, 2_000_000);
        AddFile("a.bin", DiscFormat.Room(2_000_000) - DiscSpace.ManifestShare(Piece.Whole(new FileRecord { RootName = "S", RelPath = "a.bin" })), "AA");
        var plan = Arc.MakePlan();
        Assert.Empty(plan.Unplaced);                       // exactly full
        Assert.Equal(0, plan.Bins[0].Free);

        cat.Files[0].Size++;
        Assert.Single(Arc.MakePlan().Unplaced);            // one byte more does not fit
    }

    [Fact]
    public void DiscSpace_rounds_to_sectors_and_counts_entries()
    {
        Assert.Equal(2048 + 64, DiscSpace.OnDisc(0));
        Assert.Equal(2 * 2048 + 64, DiscSpace.OnDisc(1));
        Assert.Equal(2 * 2048 + 64, DiscSpace.OnDisc(2048));
        Assert.Equal(3 * 2048 + 64, DiscSpace.OnDisc(2049));

        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.File("d/sub"));
        File.WriteAllBytes(tmp.File("d/a.bin"), new byte[3000]);      // 2 sectors + entry
        File.WriteAllBytes(tmp.File("d/sub/b.bin"), new byte[10]);    // 1 sector + entry
        long sub = (2048 + 2048) + 2048 + 2048;                        // b.bin, sub's own entry, sub's list (1 entry)
        long d = (2 * 2048 + 2048) + sub + 2048 + 2048;                // a.bin, sub, d's entry, d's list (2 entries)
        Assert.Equal(d, DiscSpace.Folder(tmp.File("d")));
    }

    [Fact]
    public void Split_cuts_into_parts_that_fill_a_disc_s_packs()
    {
        // a part fills the room of the largest disc, less its manifest line
        var big = AddFile("big.bin", 2_500_000, "AA");
        long partSize = DiscFormat.Room(1_000_000) - DiscSpace.ManifestShare(new Piece(big, 1, 2, 0, 0));
        int parts = (int)((2_500_000 + partSize - 1) / partSize);
        AddBlanks(parts, 1_000_000);

        var plan = Arc.MakePlan(split: true);
        Assert.Empty(plan.TooBig);
        Assert.Empty(plan.Unplaced);
        Assert.Equal(parts, plan.Pending.Count);
        Assert.All(plan.Bins, b => Assert.True(b.Used <= b.Usable));
        Assert.All(plan.Pending, p => Assert.True(p.Length <= partSize));
        Assert.Equal(2_500_000, plan.Pending.Sum(p => p.Length));
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
    public void Matches_path_and_the_hash_of_any_version()
    {
        var f = AddFile("photos/2020/a.jpg", 1, "ABCDEF");
        f.Backups.Add(new BackupRef { Disc = "D1", At = 0, Sha256 = "123456" });   // an older version on a disc

        Assert.True(Archive.Matches(f, "S/photos/2020"));
        Assert.True(Archive.Matches(f, "abcd"));
        Assert.True(Archive.Matches(f, "1234"));
        Assert.False(Archive.Matches(f, "D1"));
        Assert.True(Archive.Matches(f, "*"));
    }

    [Fact]
    public void Labels_continue_after_existing_ones_of_any_prefix()
    {
        cat.Discs.Add(new DiscRecord { Label = "BDXL-0007" });   // a catalog from before the counter, with the old default prefix
        Assert.Equal("DISC-0008", Arc.NextLabel());

        cat.Settings.LabelPrefix = "ARC-";
        Assert.Equal("ARC-0009", Arc.NextLabel());   // the counter is global, the prefix is not part of it
    }

    [Fact]
    public void Reclaiming_goes_back_to_the_highest_number_still_in_the_catalog_and_never_below()
    {
        cat.Discs.Add(new DiscRecord { Label = "DISC-0002", State = "lost" });   // lost: its number stays taken
        cat.Settings.LabelCounter = 5;                                          // 3, 4 and 5 were discarded
        Assert.True(Arc.ReclaimLabels());
        cat.Discs.Add(new DiscRecord { Label = Arc.NextLabel() });             // staged as DISC-0003
        Assert.Equal("DISC-0003", cat.Discs[^1].Label);
        Assert.False(Arc.ReclaimLabels());                                      // nothing above it to give back
        cat.Discs.Clear();
        Assert.True(Arc.ReclaimLabels());
        Assert.Equal("DISC-0001", Arc.NextLabel());
    }
}
