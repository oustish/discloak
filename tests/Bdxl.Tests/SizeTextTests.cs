namespace Bdxl.Tests;

public class SizeTextTests
{
    [Theory]
    [InlineData("96882130944", 96882130944L)]
    [InlineData("25GB", 25_000_000_000L)]
    [InlineData("90GiB", 90L * 1024 * 1024 * 1024)]
    [InlineData("1.5TB", 1_500_000_000_000L)]
    [InlineData("500 MB", 500_000_000L)]
    [InlineData("200kb", 200_000L)]
    [InlineData("1,000", 1000L)]
    [InlineData("2M", 2L * 1024 * 1024)]
    public void Parse_sizes(string text, long expected) => Assert.Equal(expected, SizeText.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("GB")]
    [InlineData("5XB")]
    public void Parse_rejects_nonsense(string text) => Assert.Throws<UserException>(() => SizeText.Parse(text));

    [Theory]
    [InlineData("24.99", "24.99")]
    [InlineData("24,99", "24.99")]
    [InlineData("0", "0")]
    public void Parse_money(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), SizeText.ParseMoney(text));

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Parse_money_rejects_negative_and_nonsense(string text) =>
        Assert.Throws<UserException>(() => SizeText.ParseMoney(text));

    [Fact]
    public void Formatting_is_culture_independent()
    {
        Assert.Equal("1,234,567", SizeText.Bytes(1234567));
        Assert.Equal("1,234.50 PLN", SizeText.Money(1234.5m, "PLN"));
        Assert.Equal("3.00", SizeText.Money(3, ""));
    }

    [Theory]
    [InlineData("Photos/2019/IMG_0042.CR2", 30, "Photos/2019/IMG_0042.CR2")]   // fits: unchanged
    [InlineData("Photos/2019/Summer/Holiday/IMG_0042.CR2", 25, "Photos/2…day/IMG_0042.CR2")]
    [InlineData("abcdefghij", 4, "ghij")]                                      // too narrow for an ellipsis
    public void ShortenMiddle_keeps_the_start_and_more_of_the_end(string text, int max, string expected)
    {
        string got = SizeText.ShortenMiddle(text, max);
        Assert.Equal(expected, got);
        Assert.True(got.Length <= max);
    }
}
