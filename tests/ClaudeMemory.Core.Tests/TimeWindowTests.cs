using System.Globalization;
using ClaudeMemory.Core.Text;

namespace ClaudeMemory.Core.Tests;

/// <summary>Vectors copied from the Python reference (claude_memory.py:355-387).</summary>
public sealed class TimeWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void ParseTimestamp_TreatsZuluAndNaiveValuesAsUtc()
    {
        Assert.Equal(Utc(2026, 10, 5, 9), TimeWindow.ParseTimestamp("2026-10-05T09:00:00.000Z"));
        Assert.Equal(Utc(2026, 10, 5, 9), TimeWindow.ParseTimestamp("2026-10-05T09:00:00Z"));
        Assert.Equal(Utc(2026, 10, 5, 9), TimeWindow.ParseTimestamp("2026-10-05T09:00:00"));
        Assert.Equal(Utc(2026, 10, 5), TimeWindow.ParseTimestamp("2026-10-05"));
        Assert.Equal(Utc(2026, 10, 5, 9), TimeWindow.ParseTimestamp("2026-10-05 09:00:00"));
    }

    [Fact]
    public void ParseTimestamp_KeepsTheOriginalOffset()
    {
        var parsed = TimeWindow.ParseTimestamp("2026-10-05T09:00:00+02:00");
        Assert.NotNull(parsed);
        Assert.Equal(TimeSpan.FromHours(2), parsed.Value.Offset);
        Assert.Equal(9, parsed.Value.Hour);

        var fractional = TimeWindow.ParseTimestamp("2026-10-05T09:00:00.500+02:00");
        Assert.NotNull(fractional);
        Assert.Equal(500, fractional.Value.Millisecond);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("2026-13-45")]
    public void ParseTimestamp_ReturnsNullForUnusableValues(string value)
        => Assert.Null(TimeWindow.ParseTimestamp(value));

    [Fact]
    public void ParseTimestamp_ReturnsNullForMissingValue()
        => Assert.Null(TimeWindow.ParseTimestamp(null));

    [Theory]
    [InlineData("30m", "2026-10-10T11:30:00")]
    [InlineData("2H", "2026-10-10T10:00:00")]
    [InlineData("7d", "2026-10-03T12:00:00")]
    [InlineData("1w", "2026-10-03T12:00:00")]
    [InlineData("2026-10-05", "2026-10-05T00:00:00")]
    [InlineData("2026-10-05 09:00", "2026-10-05T09:00:00")]
    [InlineData("10/05/2026", "2026-10-05T00:00:00")]
    [InlineData("05/10/2026", "2026-05-10T00:00:00")]
    [InlineData("20260101", "2026-01-01T00:00:00")]
    public void ParseSince_MatchesReference(string value, string expectedUtc)
    {
        var expected = new DateTimeOffset(
            DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None),
            TimeSpan.Zero);

        Assert.Equal(expected, TimeWindow.ParseSince(value, Now));
    }

    [Fact]
    public void ParseSince_RelativeValuesKeepTheReferenceOffset()
    {
        var offsetNow = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(3));
        var later = TimeWindow.ParseSince("30m", offsetNow);
        Assert.Equal(TimeSpan.FromHours(3), later.Offset);
        Assert.Equal(offsetNow.AddMinutes(-30), later);
    }

    [Fact]
    public void ParseSince_ThrowsTheReferenceMessage()
    {
        var error = Assert.Throws<FormatException>(() => TimeWindow.ParseSince("nope", Now));
        Assert.Equal("cannot parse --since value: 'nope'", error.Message);
    }
}