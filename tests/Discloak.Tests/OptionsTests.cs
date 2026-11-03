namespace Discloak.Tests;

public class OptionsTests
{
    [Fact]
    public void Command_arguments_values_and_flags()
    {
        var o = Options.Parse(["scan", @"D:\Photos", "--name", "Photos", "--full", "--threads", "4"]);

        Assert.Equal("scan", o.Command);
        Assert.Equal([@"D:\Photos"], o.Args);
        Assert.Equal("Photos", o.Get("name"));
        Assert.True(o.Has("full"));
        Assert.Equal(4, o.GetInt("threads", 1));
    }

    [Fact]
    public void A_flag_never_swallows_the_next_word()
    {
        var o = Options.Parse(["restore", "--flat", "dest", "*", "--split"]);
        Assert.Equal(["dest", "*"], o.Args);
        Assert.True(o.Has("flat"));
        Assert.True(o.Has("split"));
    }

    [Fact]
    public void Key_equals_value_and_a_value_option_at_the_end()
    {
        var o = Options.Parse(["plan", "--catalog=C:\\cat", "--reason"]);
        Assert.Equal("C:\\cat", o.Get("catalog"));
        Assert.Equal("true", o.Get("reason"));
    }

    [Fact]
    public void Option_names_are_case_insensitive()
    {
        var o = Options.Parse(["x", "--Max-Discs", "3"]);
        Assert.Equal(3, o.GetInt("max-discs", 1));
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void Short_help_switches(string sw) => Assert.True(Options.Parse(["scan", sw]).Has("help"));

    [Fact]
    public void Defaults_when_missing()
    {
        var o = Options.Parse(["status"]);
        Assert.Null(o.Get("x"));
        Assert.Equal("d", o.GetOr("x", "d"));
        Assert.Equal(7, o.GetInt("x", 7));
        Assert.Equal(5, o.GetSize("x", 5));
        Assert.Null(o.GetDate("x"));
        Assert.Null(o.GetMoney("x"));
    }

    [Fact]
    public void Bad_number_is_a_user_error() =>
        Assert.Throws<UserException>(() => Options.Parse(["x", "--threads", "many"]).GetInt("threads", 1));

    [Fact]
    public void A_date_is_that_calendar_day_in_utc()
    {
        var d = Options.Parse(["burned", "--date", "2026-10-01"]).GetDate("date");
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), d);
        Assert.Equal(DateTimeKind.Utc, d!.Value.Kind);
    }

    [Fact]
    public void Size_and_money_options()
    {
        var o = Options.Parse(["blanks", "add", "2", "--size", "90GiB", "--cost", "24,99"]);
        Assert.Equal(90L << 30, o.GetSize("size", 0));
        Assert.Equal(24.99m, o.GetMoney("cost"));
    }
}
