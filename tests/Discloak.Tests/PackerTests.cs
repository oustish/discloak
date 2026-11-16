namespace Discloak.Tests;

public class PackerTests
{
    static Piece P(string name, long size) => Piece.Whole(new FileRecord { RootName = "S", RelPath = name, Size = size });
    static BlankDisc Blank(string id, long usable) => new() { Id = id, Capacity = usable };

    static (List<Bin> Bins, List<Piece> Unplaced) Pack(IEnumerable<Piece> pieces, params BlankDisc[] blanks) =>
        Packer.Allocate(pieces, blanks, p => p.Length);

    [Fact]
    public void Largest_first_into_the_tightest_disc()
    {
        var (bins, unplaced) = Pack([P("a", 60), P("b", 50), P("c", 40), P("d", 10)], Blank("B1", 100), Blank("B2", 60));

        Assert.Empty(unplaced);
        Assert.Equal(["b", "c", "d"], bins[0].Pieces.Select(p => p.File.RelPath));   // 60 went to B2, a perfect fit
        Assert.Equal(["a"], bins[1].Pieces.Select(p => p.File.RelPath));
        Assert.All(bins, b => Assert.Equal(0, b.Free));
    }

    [Fact]
    public void Pieces_that_fit_nowhere_are_returned_not_dropped()
    {
        var pieces = new[] { P("huge", 500), P("a", 70), P("b", 70) };
        var (bins, unplaced) = Pack(pieces, Blank("B1", 100));

        Assert.Equal(["a"], bins[0].Pieces.Select(p => p.File.RelPath));
        Assert.Equal(["huge", "b"], unplaced.Select(p => p.File.RelPath));
        Assert.Equal(pieces.Length, bins.Sum(b => b.Pieces.Count) + unplaced.Count);
    }

    [Fact]
    public void No_blank_discs_means_everything_is_unplaced()
    {
        var (bins, unplaced) = Pack([P("a", 1), P("b", 2)]);
        Assert.Empty(bins);
        Assert.Equal(2, unplaced.Count);
    }

    [Fact]
    public void Never_overfills_a_disc()
    {
        var rnd = new Random(7);
        var pieces = Enumerable.Range(0, 300).Select(i => P($"f{i:D3}", rnd.Next(1, 400))).ToList();
        var blanks = Enumerable.Range(1, 20).Select(i => Blank($"B{i:D2}", rnd.Next(500, 3000))).ToArray();

        var (bins, unplaced) = Pack(pieces, blanks);

        Assert.All(bins, b => Assert.True(b.Used <= b.Usable));
        Assert.Equal(pieces.Count, bins.Sum(b => b.Pieces.Count) + unplaced.Count);
        Assert.Equal(pieces.Count, bins.SelectMany(b => b.Pieces).Concat(unplaced).Distinct().Count());
    }

    [Fact]
    public void Uses_the_stored_size_from_sizeOf()
    {
        // 90 bytes plain fits a 100-byte disc, but not when every piece takes 20 bytes more (e.g. encryption)
        var (_, unplaced) = Packer.Allocate([P("a", 90)], [Blank("B1", 100)], p => p.Length + 20);
        Assert.Single(unplaced);
    }

    [Fact]
    public void Result_is_deterministic()
    {
        var pieces = new[] { P("x", 10), P("y", 10), P("z", 10) };
        var first = Pack(pieces, Blank("B1", 20), Blank("B2", 20));
        var second = Pack(Enumerable.Reverse(pieces), Blank("B2", 20), Blank("B1", 20));
        Assert.Equal(first.Bins.Select(b => string.Join(",", b.Pieces.Select(p => p.Name))),
                     second.Bins.Select(b => string.Join(",", b.Pieces.Select(p => p.Name))));
    }

    static (List<Bin> Bins, List<Piece> Unplaced) Sequential(IEnumerable<IEnumerable<Piece>> runs, params BlankDisc[] blanks) =>
        Packer.Sequential(runs, blanks, p => p.Length, p => p.File.RelPath);   // the name stands for the content

    [Fact]
    public void Sequential_fills_disc_after_disc_in_the_order_given_and_never_goes_back_to_fill_a_gap()
    {
        var (bins, unplaced) = Sequential([[P("a", 40), P("b", 40), P("c", 30), P("d", 10), P("e", 60)]],
                                          Blank("B1", 100), Blank("B2", 100));
        Assert.Empty(unplaced);
        Assert.Equal(["a", "b"], bins[0].Pieces.Select(p => p.File.RelPath));        // c did not fit: B1 keeps 20 free,
        Assert.Equal(["c", "d", "e"], bins[1].Pieces.Select(p => p.File.RelPath));   // d (10) does not go back there
        Assert.Equal(20, bins[0].Free);
    }

    [Fact]
    public void Sequential_returns_what_fits_on_no_disc_left_and_goes_on_with_the_rest()
    {
        var (bins, unplaced) = Sequential([[P("a", 50), P("huge", 500), P("b", 50), P("c", 50)]], Blank("B1", 100));
        Assert.Equal(["a", "b"], bins[0].Pieces.Select(p => p.File.RelPath));
        Assert.Equal(["huge", "c"], unplaced.Select(p => p.File.RelPath));
    }

    [Fact]
    public void Sequential_starts_every_run_of_copies_on_a_disc_of_its_own()
    {
        // first copies of a, b; second copies of the same: a's two copies would otherwise share B1
        var (bins, unplaced) = Sequential([[P("a", 30), P("b", 30)], [P("a", 30), P("b", 30)]],
                                          Blank("B1", 100), Blank("B2", 100), Blank("B3", 100));
        Assert.Empty(unplaced);
        Assert.Equal(["a", "b"], bins[0].Pieces.Select(p => p.File.RelPath));
        Assert.Equal(["a", "b"], bins[1].Pieces.Select(p => p.File.RelPath));
        Assert.Empty(bins[2].Pieces);

        // no disc of its own left for the second copies: they wait
        (bins, unplaced) = Sequential([[P("a", 30)], [P("a", 30)]], Blank("B1", 100));
        Assert.Single(bins[0].Pieces);
        Assert.Equal("a", Assert.Single(unplaced).File.RelPath);
    }

    [Fact]
    public void Explorer_order_keeps_folders_together_with_subfolders_first_and_numbers_by_value()
    {
        string[] paths = ["Music/Album 2/01.flac", "Music/Album/Track 10.flac", "Music/Album/cover.jpg", "Music/Album/Track 2.flac",
                          "Music/Album/CD1/01.flac", "Music/readme.txt", "Music/Album/track 1.flac"];
        Assert.Equal(["Music/Album/CD1/01.flac", "Music/Album/cover.jpg", "Music/Album/track 1.flac", "Music/Album/Track 2.flac",
                      "Music/Album/Track 10.flac", "Music/Album 2/01.flac", "Music/readme.txt"],
                     paths.Order(ExplorerOrder.Instance));
    }
}
