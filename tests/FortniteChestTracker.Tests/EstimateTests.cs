namespace FortniteChestTracker.Tests;

public class EstimateTests
{
    private static List<MatchRecord> Tracked(string version, int count, int chests, int? seconds = null) =>
        Enumerable.Range(0, count).Select(i => new MatchRecord
        {
            SessionId = Guid.NewGuid().ToString("N"), GameVersion = "++Fortnite+Release-" + version, ChestsOpened = chests,
            DurationSeconds = seconds, MatchStartUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        }).ToList();

    private static EstimateSettings Season(string season, int played, int hours) =>
        new() { Season = season, Played = played, Minutes = hours * 60, UpdatedUtc = DateTime.UtcNow };

    [Theory]
    [InlineData("++Fortnite+Release-43.10", "43")]
    [InlineData("++Fortnite+Release-9.41", "9")]
    [InlineData("whatever", null)]
    [InlineData(null, null)]
    public void Season_is_the_major_game_version(string? version, string? season)
    {
        Assert.Equal(season, Estimates.SeasonOf(version));
    }

    [Fact]
    public void The_current_season_is_the_one_of_the_latest_replay()
    {
        var old = Tracked("42.30", 3, 10);
        var now = Tracked("43.10", 2, 10);
        now.ForEach(m => m.MatchStartUtc = m.MatchStartUtc!.Value.AddDays(10));
        Assert.Equal("43", Estimates.CurrentSeason(old.Concat(now)));
        Assert.Null(Estimates.CurrentSeason(new[] { new MatchRecord() }));
    }

    [Fact]
    public void The_season_is_compared_with_the_replays_of_that_season_only()
    {
        var tracked = Tracked("42.30", 6, 10).Concat(Tracked("43.00", 3, 30)).Concat(Tracked("43.10", 2, 30)).ToList();
        var r = Estimates.Compute(Season("43", 20, 3), tracked)!;
        Assert.Equal(20, r.Played);
        Assert.Equal(5, r.Tracked);           // 43.00 and 43.10: the same season
        Assert.Equal(15, r.Untracked);
        Assert.Equal(30, r.Average);          // this season's average (5 matches)
        Assert.Equal(15 * 30, r.Estimated);
        Assert.Equal(11 + 15, r.Matches);     // every replay + the season's matches without one
    }

    [Fact]
    public void Few_replays_this_season_use_the_average_of_all_of_them()
    {
        var tracked = Tracked("42.30", 6, 10).Concat(Tracked("43.10", 2, 30)).ToList(); // 120 / 8 = 15
        var r = Estimates.Compute(Season("43", 12, 2), tracked)!;
        Assert.Equal(10, r.Untracked);
        Assert.Equal(15, r.Average);
        Assert.Equal(150, r.Estimated);
        Assert.True(r.Low < 150 && r.High > 150);
    }

    [Fact]
    public void Time_played_is_fortnite_gg_s_for_the_season_and_the_replays_for_the_others()
    {
        var tracked = Tracked("42.30", 3, 10, seconds: 1200).Concat(Tracked("43.10", 2, 10, seconds: 1500)).ToList(); // 60 + 50 min
        var r = Estimates.Compute(Season("43", 12, 4), tracked)!;
        Assert.Equal(60 + 240, r.Minutes); // the season: fortnite.gg's 4 h, not its replays' 50 min + an average
    }

    [Fact]
    public void More_counted_than_played_never_goes_negative()
    {
        var r = Estimates.Compute(Season("43", 1, 1), Tracked("43.10", 3, 10))!;
        Assert.Equal(0, r.Untracked);
        Assert.Equal(0, r.Estimated);
        Assert.Equal(3, r.Matches);
    }

    [Fact]
    public void Nothing_to_estimate()
    {
        var tracked = Tracked("43.10", 3, 10);
        Assert.Null(Estimates.Compute(null, tracked));
        Assert.Null(Estimates.Compute(new EstimateSettings(), tracked));
        Assert.Null(Estimates.Compute(Season("43", 10, 0), tracked));               // hours are needed
        Assert.Null(Estimates.Compute(new EstimateSettings { Played = 10, Minutes = 60 }, tracked)); // no season
        Assert.Null(Estimates.Compute(Season("43", 10, 1), new List<MatchRecord>()));
    }

    [Fact]
    public void Numbers_from_before_seasons_are_dropped()
    {
        Assert.True(new EstimateSettings { UpdatedUtc = DateTime.UtcNow }.IsLegacy);       // per mode, all time, fortnite-api.com
        Assert.False(new EstimateSettings { Player = "x" }.IsLegacy);                       // only the name typed
        Assert.False(Season("43", 10, 1).IsLegacy);
    }

    [Fact]
    public void An_old_estimate_in_config_json_still_loads()
    {
        var json = """{"Language":"fr","Estimate":{"Source":"api","Matches":{"solo":300,"total":500},"Minutes":900,"Player":"x","Include":true,"ApiKey":"k","UpdatedUtc":"2026-11-12T10:00:00Z"}}""";
        var config = System.Text.Json.JsonSerializer.Deserialize<Config>(json)!;
        Assert.True(config.Estimate!.IsLegacy);
        Assert.Equal("x", config.Estimate.Player);
    }

    [Fact]
    public void The_hunt_counts_the_estimate_of_the_current_season_only_when_added_to_the_total()
    {
        var tracked = Tracked("43.10", 5, 30);
        var included = Season("43", 15, 3);
        included.Include = true;
        Assert.Equal(10 * 30, Estimates.ForHunt(included, tracked));

        var notIncluded = Season("43", 15, 3);
        Assert.Equal(0, Estimates.ForHunt(notIncluded, tracked));

        var newSeason = tracked.Concat(Tracked("44.00", 1, 30).Select(m => { m.MatchStartUtc = m.MatchStartUtc!.Value.AddDays(30); return m; })).ToList();
        Assert.Equal(0, Estimates.ForHunt(included, newSeason)); // numbers of the previous season: not in the new hunt
    }
}
