using System.Text.RegularExpressions;
using Searchlight.ViewModels;
using Xunit;

namespace Searchlight.Core.Tests;

/// <summary>
/// Verifies the recency-bucket ladder in <see cref="MainViewModel.GroupKeyFor"/>:
/// doubling windows (2/4/8/16/32/64h, strict upper bounds) then an absolute calendar-day
/// header for anything older. A fixed <c>now</c> makes every boundary deterministic.
/// </summary>
public sealed class GroupKeyForTests
{
    // Arbitrary fixed anchor so the tests never depend on the wall clock.
    private static readonly DateTimeOffset Now =
        new(2026, 7, 2, 12, 0, 0, TimeSpan.FromHours(-7));

    private static string KeyFor(TimeSpan age) => MainViewModel.GroupKeyFor(Now - age, Now);

    [Fact]
    public void JustNow_IsLast2Hours() =>
        Assert.Equal("Last 2 hours", KeyFor(TimeSpan.FromMinutes(1)));

    [Fact]
    public void OneMinuteUnderTwoHours_IsLast2Hours() =>
        Assert.Equal("Last 2 hours", KeyFor(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1)));

    [Fact]
    public void ExactlyTwoHours_RollsIntoLast4Hours() =>
        // Boundary is strict '<', so age == 2h is NOT in the 2h bucket.
        Assert.Equal("Last 4 hours", KeyFor(TimeSpan.FromHours(2)));

    [Fact]
    public void JustUnderFourHours_IsLast4Hours() =>
        Assert.Equal("Last 4 hours", KeyFor(TimeSpan.FromHours(4) - TimeSpan.FromMinutes(1)));

    [Fact]
    public void ExactlyFourHours_RollsIntoLast8Hours() =>
        Assert.Equal("Last 8 hours", KeyFor(TimeSpan.FromHours(4)));

    [Fact]
    public void JustUnderEightHours_IsLast8Hours() =>
        Assert.Equal("Last 8 hours", KeyFor(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(1)));

    [Fact]
    public void ExactlyEightHours_RollsIntoLast16Hours() =>
        Assert.Equal("Last 16 hours", KeyFor(TimeSpan.FromHours(8)));

    [Fact]
    public void JustUnderSixteenHours_IsLast16Hours() =>
        Assert.Equal("Last 16 hours", KeyFor(TimeSpan.FromHours(16) - TimeSpan.FromMinutes(1)));

    [Fact]
    public void ExactlySixteenHours_RollsIntoLast32Hours() =>
        Assert.Equal("Last 32 hours", KeyFor(TimeSpan.FromHours(16)));

    [Fact]
    public void JustUnderThirtyTwoHours_IsLast32Hours() =>
        Assert.Equal("Last 32 hours", KeyFor(TimeSpan.FromHours(32) - TimeSpan.FromMinutes(1)));

    [Fact]
    public void ExactlyThirtyTwoHours_RollsIntoLast64Hours() =>
        Assert.Equal("Last 64 hours", KeyFor(TimeSpan.FromHours(32)));

    [Fact]
    public void JustUnderSixtyFourHours_IsLast64Hours() =>
        Assert.Equal("Last 64 hours", KeyFor(TimeSpan.FromHours(64) - TimeSpan.FromMinutes(1)));

    [Fact]
    public void ExactlySixtyFourHours_FallsBackToAbsoluteDate()
    {
        string key = KeyFor(TimeSpan.FromHours(64));

        Assert.DoesNotContain("Last", key);
        // Absolute-date fallback formats as "dddd, MMMM d, yyyy" — assert that shape,
        // locale-independently, rather than a hard-coded (culture-specific) string.
        Assert.Matches(new Regex(@"^\w+, \w+ \d{1,2}, \d{4}$"), key);
    }

    [Fact]
    public void MuchOlder_UsesTheSessionsOwnCalendarDay()
    {
        // A session updated 72h before the anchor should carry the anchor-minus-72h date,
        // not the "now" date — proving the header reflects the session, not the clock.
        DateTimeOffset updated = Now - TimeSpan.FromHours(72);
        string expected = updated.ToLocalTime().ToString("dddd, MMMM d, yyyy");

        Assert.Equal(expected, MainViewModel.GroupKeyFor(updated, Now));
    }

    // --- Coarser tiers for older sessions: day (32h–14d), week (14d–30d), month (≥30d) ---

    private static string ShortFor(TimeSpan age) => MainViewModel.ShortKeyFor(Now - age, Now);

    [Fact]
    public void SeventyTwoHours_IsCalendarDay()
    {
        string key = KeyFor(TimeSpan.FromHours(72));

        Assert.DoesNotContain("Last", key);
        Assert.DoesNotContain("Week", key);
        Assert.Matches(new Regex(@"^\w+, \w+ \d{1,2}, \d{4}$"), key);
    }

    [Fact]
    public void JustUnderFourteenDays_IsCalendarDay() =>
        Assert.Matches(
            new Regex(@"^\w+, \w+ \d{1,2}, \d{4}$"),
            KeyFor(TimeSpan.FromDays(14) - TimeSpan.FromHours(1)));

    [Fact]
    public void ExactlyFourteenDays_RollsIntoWeek() =>
        // Boundary is strict '<' on the day tier, so age == 14d is a week header.
        Assert.StartsWith("Week of ", KeyFor(TimeSpan.FromDays(14)));

    [Fact]
    public void TwentyDays_IsWeek()
    {
        string key = KeyFor(TimeSpan.FromDays(20));

        Assert.StartsWith("Week of ", key);
        Assert.Matches(new Regex(@"^Week of \w+ \d{1,2}, \d{4}$"), key);
    }

    [Fact]
    public void ExactlyThirtyDays_RollsIntoMonth()
    {
        string key = KeyFor(TimeSpan.FromDays(30));

        Assert.DoesNotContain("Week", key);
        Assert.DoesNotContain("Last", key);
        Assert.Matches(new Regex(@"^\w+ \d{4}$"), key);
    }

    [Fact]
    public void FortyFiveDays_IsMonth()
    {
        string key = KeyFor(TimeSpan.FromDays(45));

        Assert.DoesNotContain("Week", key);
        Assert.DoesNotContain("Last", key);
        Assert.Matches(new Regex(@"^\w+ \d{4}$"), key);
    }

    // --- ShortKey (tick-rail labels) mirror the tier ladder ---

    [Theory]
    [InlineData(1, "2h")]      // minutes → 2h bucket
    [InlineData(2, "4h")]      // hours (boundary rolls up)
    [InlineData(4, "8h")]
    [InlineData(8, "16h")]
    [InlineData(16, "32h")]
    [InlineData(32, "64h")]    // 32h boundary rolls into the 64h bucket
    public void ShortKey_RecencyBuckets(int ageHours, string expectedPrefix) =>
        // Hour ticks now append the session's weekday letter, e.g. "2h (T)".
        Assert.Matches(new Regex("^" + Regex.Escape(expectedPrefix) + @" \([MTWFS]\)$"),
            ShortFor(TimeSpan.FromHours(ageHours)));

    [Fact]
    public void ShortKey_Day_IsMonthDayAndWeekdayLetter() =>
        // Day-tier tick shows abbreviated month + day# (no parens) and appends the single-letter
        // weekday, e.g. "Jul 3 (W)".
        Assert.Matches(new Regex(@"^[A-Za-z]{3} \d{1,2} \([MTWFS]\)$"), ShortFor(TimeSpan.FromHours(72)));

    [Fact]
    public void ShortKey_Week_IsPrefixedWk() =>
        Assert.Matches(new Regex(@"^Wk \w+ \d{1,2}$"), ShortFor(TimeSpan.FromDays(20)));

    [Fact]
    public void ShortKey_Month_IsMonthYear() =>
        Assert.Matches(new Regex(@"^\w+ \d{4}$"), ShortFor(TimeSpan.FromDays(45)));
}
