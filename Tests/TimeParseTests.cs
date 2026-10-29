using DoomGuy.Core;
using Xunit;

namespace DoomGuy.Tests;

public class TimeParseTests
{
    // A fixed "now" so the clock-time cases don't depend on when the tests run: Wednesday 14:00.
    private static readonly DateTime Now = new(2026, 10, 28, 14, 0, 0);

    [Theory]
    [InlineData("30m", 30 * 60)]
    [InlineData("1h 30m", 90 * 60)]
    [InlineData("an hour", 3600)]
    [InlineData("half an hour", 30 * 60)]
    [InlineData("2 days", 2 * 86400)]
    [InlineData("1.5h", 90 * 60)]
    public void Duration_reads_number_and_unit_pairs(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), TimeParse.Duration(text));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("30 parsecs")]
    [InlineData("30m and some")]
    public void Duration_rejects_anything_else(string text) => Assert.Null(TimeParse.Duration(text));

    [Fact]
    public void Reminder_with_in_puts_the_text_first()
    {
        Assert.True(TimeParse.Reminder("stand up in 30m", Now, out var text, out var due, out var every));
        Assert.Equal("stand up", text);
        Assert.Equal(Now.AddMinutes(30), due);
        Assert.Null(every);
    }

    [Fact]
    public void Reminder_at_a_clock_time_later_today()
    {
        Assert.True(TimeParse.Reminder("call mum at 18:00", Now, out var text, out var due, out _));
        Assert.Equal("call mum", text);
        Assert.Equal(Now.Date.AddHours(18), due);
    }

    [Fact]
    public void Reminder_at_a_time_already_gone_today_is_tomorrow()
    {
        Assert.True(TimeParse.Reminder("standup at 9am", Now, out _, out var due, out _));
        Assert.Equal(Now.Date.AddDays(1).AddHours(9), due);
    }

    [Fact]
    public void A_bare_hour_means_the_next_one_morning_or_evening()
    {
        // 14:00 now: "at 5" is 17:00 today, not 05:00 tomorrow.
        Assert.Equal(Now.Date.AddHours(17), TimeParse.At("5", Now));
    }

    [Fact]
    public void Reminder_every_repeats()
    {
        Assert.True(TimeParse.Reminder("water every 1h", Now, out var text, out var due, out var every));
        Assert.Equal("water", text);
        Assert.Equal(TimeSpan.FromHours(1), every);
        Assert.Equal(Now.AddHours(1), due);
    }

    [Fact]
    public void Reminder_tomorrow_with_a_time()
    {
        Assert.True(TimeParse.Reminder("rent tomorrow 9am", Now, out var text, out var due, out _));
        Assert.Equal("rent", text);
        Assert.Equal(Now.Date.AddDays(1).AddHours(9), due);
    }

    [Fact]
    public void Reminder_needs_a_when() => Assert.False(TimeParse.Reminder("just some words", Now, out _, out _, out _));

    [Theory]
    [InlineData(45, "45S")]
    [InlineData(5 * 60 + 7, "5M07S")]
    [InlineData(25 * 60, "25M")]
    [InlineData(2 * 3600, "2H")]
    [InlineData(2 * 3600 + 5 * 60, "2H05M")]
    [InlineData(26 * 3600, "1D2H")]
    public void Short_is_the_bar_s_compact_form(int seconds, string expected) =>
        Assert.Equal(expected, TimeParse.Short(TimeSpan.FromSeconds(seconds)));
}
