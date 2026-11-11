using FortniteChestTracker;
using Xunit;

namespace FortniteChestTracker.Tests;

/// <summary>The chests that count for the treasure hunt, with the calendar embedded in the exe (season.json).</summary>
public class HuntTests
{
    private static readonly DateTime Since = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MatchRecord Match(DateTime startUtc, int chests, string playlist = "Playlist_NoBuildBR_Duo", float firstChestAt = 60)
    {
        var m = new MatchRecord { MatchStartUtc = startUtc, AnalyzedAtUtc = startUtc.AddMinutes(25), ChestsOpened = chests, Playlist = playlist };
        for (var i = 0; i < chests; i++)
        {
            m.Chests.Add(new ChestRecord { TimeSeconds = firstChestAt + i * 30, Kind = "Coffre" });
        }
        return m;
    }

    [Fact]
    public void Ranked_matches_do_not_count_for_the_hunt()
    {
        Assert.True(Hunt.IsRanked("Playlist_HabaneroSolo"));
        Assert.False(Hunt.IsRanked("Playlist_NoBuildBR_Squad"));
        Assert.False(Hunt.Counts(Match(Since.AddDays(3), 10, "Playlist_HabaneroDuo"), Since));
        Assert.True(Hunt.Counts(Match(Since.AddDays(3), 10), Since));
        Assert.False(Hunt.Counts(Match(Since.AddDays(-3), 10), Since)); // before the hunt
        Assert.False(Hunt.Counts(new MatchRecord { MatchStartUtc = Since.AddDays(3), IsBattleRoyale = false, ChestsOpened = 4 }, Since));
    }

    [Fact]
    public void Chests_opened_during_Treasure_Hours_count_with_their_boost()
    {
        Assert.NotEmpty(Season.Current.TreasureHours);
        var hour = Season.Current.TreasureHours.First(h => h.BoostOf("odds_pirate") > 1);
        var boost = hour.BoostOf("odds_pirate");

        var during = Match(hour.Start.AddMinutes(10), 10);
        Assert.Equal(10 * boost, Hunt.Weighted(during, "odds_pirate"), 6);
        Assert.Equal(10, Hunt.Weighted(during, "odds_jam"), 6); // jam tracks are never boosted

        var outside = Match(hour.Start.AddHours(-6), 10);
        Assert.Equal(10, Hunt.Weighted(outside, "odds_pirate"), 6);

        // a match that starts just before the window: only its chests opened inside count more
        var across = Match(hour.Start.AddSeconds(-90), 4, firstChestAt: 30); // chests at -60 s, -30 s, 0 s, +30 s
        Assert.Equal(2 + 2 * boost, Hunt.Weighted(across, "odds_pirate"), 6);
        Assert.Equal(2, Hunt.DuringTreasureHours(new[] { across }));
    }

    [Fact]
    public void Weeks_follow_the_calendar()
    {
        var s = Season.Current;
        Assert.True(s.Weeks.Count >= 2);
        Assert.Equal(0, Season.WeekAt(s.Weeks[0].Start.AddHours(1)));
        Assert.Equal(1, Season.WeekAt(s.Weeks[1].Start.AddHours(1)));
        Assert.Equal(-1, Season.WeekAt(s.End.AddHours(1)));
        Assert.Equal(s.Weeks[1].Start, Season.WeekBounds(0).End);
        Assert.Equal(s.End, Season.WeekBounds(s.Weeks.Count - 1).End);
    }

    [Fact]
    public void Next_and_current_Treasure_Hours()
    {
        var first = Season.Current.TreasureHours[0];
        Assert.Same(first, Season.NextAfter(first.Start.AddHours(-1)));
        Assert.Same(first, Season.ActiveAt(first.Start.AddMinutes(5)));
        Assert.Null(Season.ActiveAt(first.End));
        Assert.Equal(first.BoostOf("odds_gold"), Season.Boost("odds_gold", first.Start.AddMinutes(1)));
        Assert.Equal(1, Season.Boost("odds_gold", first.End.AddMinutes(1)));
    }

    [Fact]
    public void A_broken_calendar_is_refused()
    {
        Assert.Null(Season.Parse("{ not json"));
        Assert.Null(Season.Parse("{\"version\": 9, \"start\": \"2026-12-05T00:00:00Z\", \"end\": \"2026-11-01T00:00:00Z\"}"));
        Assert.False(Season.Use(null));
    }

    [Fact]
    public void Expected_rewards_are_counted_per_week_of_the_season()
    {
        var w0 = Season.Current.Weeks[0].Start.AddDays(1);
        var oneWeek = new[] { Match(w0, 20), Match(w0.AddHours(2), 20) };
        var twoWeeks = new[] { Match(w0, 20), Match(Season.Current.Weeks[1].Start.AddDays(1), 20) };
        var vehicle = TreasureOdds.All.First(o => o.Key == "odds_vehicle");
        // the same chests split over two weeks give two chances at the weekly vehicle instead of one
        Assert.True(TreasureOdds.ExpectedFor(vehicle, twoWeeks, Since) > TreasureOdds.ExpectedFor(vehicle, oneWeek, Since));
    }
}

/// <summary>History file (export / import between PCs).</summary>
public class HistoryFileTests
{
    [Fact]
    public void A_history_file_is_read_back_and_anything_else_is_refused()
    {
        var json = "{\"Matches\":[{\"SessionId\":\"abc\",\"ChestsOpened\":12,\"MatchStartUtc\":\"2026-11-03T12:00:00\",\"AnalyzedAtUtc\":\"2026-11-03T12:30:00\"}],\"ProcessedFiles\":{\"a.replay\":\"abc\"}}";
        var data = Store.ParseData(json);
        Assert.NotNull(data);
        Assert.Single(data!.Matches);
        Assert.Equal(12, data.Matches[0].ChestsOpened);
        Assert.Equal(DateTimeKind.Utc, data.Matches[0].MatchStartUtc!.Value.Kind);
        Assert.Null(Store.ParseData("{ not json"));
        Assert.Null(Store.ParseData("[1, 2]"));
    }
}
