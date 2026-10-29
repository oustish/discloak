namespace Bdxl.Tests;

public class SplitLayoutTests
{
    static FileRecord File(long size) => new() { RootName = "S", RelPath = "big.bin", Size = size };

    [Fact]
    public void Parts_cover_the_file_exactly_and_only_the_last_is_shorter()
    {
        var pieces = SplitLayout.Pieces(File(2500), 1000);

        Assert.Equal([1, 2, 3], pieces.Select(p => p.Part));
        Assert.All(pieces, p => Assert.Equal(3, p.Parts));
        Assert.Equal([0L, 1000, 2000], pieces.Select(p => p.Offset));
        Assert.Equal([1000L, 1000, 500], pieces.Select(p => p.Length));
    }

    [Fact]
    public void Exact_multiple_gives_equal_parts()
    {
        var pieces = SplitLayout.Pieces(File(3000), 1000);
        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(1000, p.Length));
    }

    [Fact]
    public void Part_size_must_be_positive() =>
        Assert.Throws<ArgumentException>(() => SplitLayout.Pieces(File(10), 0));

    [Theory]
    [InlineData(1, 3, ".001")]
    [InlineData(12, 999, ".012")]
    [InlineData(7, 1200, ".0007")]   // more than 999 parts: wider numbers so names still sort in order
    public void Suffix_is_zero_padded(int part, int parts, string expected) =>
        Assert.Equal(expected, SplitLayout.Suffix(part, parts));

    [Fact]
    public void Piece_name_carries_the_suffix()
    {
        var pieces = SplitLayout.Pieces(File(2500), 1000);
        Assert.Equal("S/big.bin.002", pieces[1].Name);
        Assert.Equal("S/big.bin", Piece.Whole(File(2500)).Name);
    }

    [Fact]
    public void Part_size_can_be_read_back_from_any_part_including_the_short_last_one()
    {
        foreach (var p in SplitLayout.Pieces(File(2500), 1000))
        {
            var copy = new BackupRef { Part = p.Part, Parts = p.Parts, Offset = p.Offset, Length = p.Length };
            Assert.Equal(1000, SplitLayout.PartSizeOf(copy));
        }
    }
}
