namespace Bdxl.Tests;

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
}
