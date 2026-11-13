using System.Net;

namespace FortniteChestTracker.Tests;

public class EstimateTests
{
    private static List<MatchRecord> Tracked(params (string Playlist, int Chests)[] matches) =>
        matches.Select(m => new MatchRecord { SessionId = Guid.NewGuid().ToString("N"), Playlist = m.Playlist, ChestsOpened = m.Chests }).ToList();

    [Theory]
    [InlineData("Playlist_NoBuildBR_Solo", "solo")]
    [InlineData("Playlist_NoBuildBR_Duo", "duo")]
    [InlineData("Playlist_DefaultDuo", "duo")]
    [InlineData("Playlist_NoBuildBR_Trio", "trio")]
    [InlineData("Playlist_NoBuildBR_Squad", "squad")]
    [InlineData("Playlist_Habanero_Quad", "squad")]
    [InlineData("Playlist_Something", "other")]
    [InlineData(null, "other")]
    public void Playlists_map_to_Epic_stats_modes(string? playlist, string bucket)
    {
        Assert.Equal(bucket, Estimates.Bucket(playlist));
    }

    [Fact]
    public void Total_only_uses_the_overall_average()
    {
        var tracked = Tracked(("duo", 10), ("duo", 20), ("solo", 30)); // average 20
        var r = Estimates.Compute(new EstimateSettings { Matches = { ["total"] = 103 } }, tracked)!;
        Assert.Equal(103, r.Played);
        Assert.Equal(3, r.Tracked);
        Assert.Equal(100, r.Untracked);
        Assert.Equal(2000, r.Estimated);
        Assert.True(r.Low < 2000 && r.High > 2000);
    }

    [Fact]
    public void Per_mode_uses_the_mode_average_when_there_are_enough_matches()
    {
        var tracked = Tracked(Enumerable.Repeat(("Playlist_Solo", 30), 5).Concat(Enumerable.Repeat(("Playlist_Duo", 10), 2)).ToArray());
        var r = Estimates.Compute(new EstimateSettings { Matches = { ["solo"] = 15, ["duo"] = 12 } }, tracked)!;

        var solo = r.Parts.Single(p => p.Bucket == "solo");
        Assert.Equal(10, solo.Untracked);
        Assert.Equal(30, solo.Average);   // 5 solo matches: their own average
        Assert.Equal(300, solo.Estimated);

        var duo = r.Parts.Single(p => p.Bucket == "duo");
        Assert.Equal(10, duo.Untracked);
        Assert.Equal(24.3, duo.Average);  // only 2 duo matches: overall average (170 / 7)
        Assert.Equal(243, duo.Estimated);
        Assert.Equal(543, r.Estimated);
    }

    [Fact]
    public void More_counted_than_played_never_goes_negative()
    {
        var r = Estimates.Compute(new EstimateSettings { Matches = { ["total"] = 1 } }, Tracked(("duo", 10), ("duo", 20)))!;
        Assert.Equal(0, r.Untracked);
        Assert.Equal(0, r.Estimated);
    }

    [Fact]
    public void Nothing_to_estimate()
    {
        Assert.Null(Estimates.Compute(null, Tracked(("duo", 10))));
        Assert.Null(Estimates.Compute(new EstimateSettings(), Tracked(("duo", 10))));
        Assert.Null(Estimates.Compute(new EstimateSettings { Matches = { ["total"] = 10 } }, new List<MatchRecord>()));
    }

    private static List<MatchRecord> Timed(params int[] seconds) =>
        seconds.Select(s => new MatchRecord { SessionId = Guid.NewGuid().ToString("N"), Playlist = "duo", ChestsOpened = 10, DurationSeconds = s }).ToList();

    [Fact]
    public void Matches_and_time_played_include_the_matches_without_replay()
    {
        var tracked = Timed(1200, 1800); // 2 matches, 50 min, 25 min on average
        var r = Estimates.Compute(new EstimateSettings { Matches = { ["total"] = 12 } }, tracked)!;
        Assert.Equal(12, r.Matches);
        Assert.Equal(50 + 10 * 25, r.Minutes);
        Assert.False(r.MinutesFromEpic);

        // Epic's minutes: only their average per match (240 / 12 = 20) for the 10 matches without replay
        r = Estimates.Compute(new EstimateSettings { Matches = { ["total"] = 12 }, Minutes = 240 }, tracked)!;
        Assert.Equal(50 + 10 * 20, r.Minutes);
        Assert.True(r.MinutesFromEpic);

        // per mode: the matches of all the modes entered
        r = Estimates.Compute(new EstimateSettings { Matches = { ["duo"] = 8, ["solo"] = 4 }, Minutes = 240 }, tracked)!;
        Assert.Equal(50 + 10 * 20, r.Minutes);

        // no match length known: only the counted time
        r = Estimates.Compute(new EstimateSettings { Matches = { ["total"] = 12 } }, Tracked(("duo", 10)))!;
        Assert.Equal(12, r.Matches);
        Assert.Equal(0, r.Minutes);
    }

    [Fact]
    public void Per_mode_match_total_keeps_the_counted_matches_of_other_modes()
    {
        var tracked = Tracked(("Playlist_Solo", 10), ("Playlist_Duo", 10), ("Playlist_Duo", 10));
        var r = Estimates.Compute(new EstimateSettings { Matches = { ["solo"] = 20 } }, tracked)!;
        Assert.Equal(19, r.Untracked);
        Assert.Equal(3 + 19, r.Matches);
    }

    [Fact]
    public void Minutes_played_are_read_from_the_overall_stats()
    {
        Assert.Equal(20000, Estimates.ParseMinutes(ApiAnswer));
        Assert.Null(Estimates.ParseMinutes("""{"status":200,"data":{"stats":{"all":{"overall":{"matches":3}}}}}"""));
        Assert.Null(Estimates.ParseMinutes("""{"status":200,"data":null}"""));
    }

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
    public void Season_numbers_are_compared_with_the_replays_of_that_season_only()
    {
        var old = Tracked(Enumerable.Repeat(("duo", 10), 6).ToArray());
        old.ForEach(m => m.GameVersion = "++Fortnite+Release-42.30");
        var now = Tracked(Enumerable.Repeat(("duo", 30), 5).ToArray());
        now.ForEach(m => { m.GameVersion = "++Fortnite+Release-43.10"; m.MatchStartUtc = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc); });
        var tracked = old.Concat(now).ToList();
        Assert.Equal("43", Estimates.CurrentSeason(tracked));

        var r = Estimates.Compute(new EstimateSettings { Season = "43", Matches = { ["total"] = 20 } }, tracked)!;
        Assert.Equal(5, r.Tracked);          // this season's replays only
        Assert.Equal(15, r.Untracked);
        Assert.Equal(15 * 30, r.Estimated);  // at this season's average
        Assert.Equal(11 + 15, r.Matches);    // every replay + the matches without one
    }

    private const string ApiAnswer = """
        {"status":200,"data":{"account":{"id":"x","name":"Ssshamble"},"battlePass":{"level":50,"progress":10},"image":null,
         "stats":{"all":{
           "overall":{"score":1,"wins":30,"kills":900,"matches":1500,"minutesPlayed":20000},
           "solo":{"wins":5,"kills":200,"matches":300},
           "duo":{"wins":10,"kills":300,"matches":500},
           "trio":null,
           "squad":{"wins":15,"kills":400,"matches":600},
           "ltm":{"wins":0,"kills":0,"matches":40}},
          "keyboardMouse":null,"gamepad":null,"touch":null}}}
        """;

    [Fact]
    public void Fortnite_api_answer_is_read_per_mode()
    {
        var m = Estimates.Parse(ApiAnswer);
        Assert.Equal(300, m["solo"]);
        Assert.Equal(500, m["duo"]);
        Assert.Equal(600, m["squad"]);
        Assert.False(m.ContainsKey("trio"));     // sent as null
        Assert.Equal(40 + 60, m["other"]);       // ltm + what overall has on top of the listed modes (trios)
        Assert.Equal(1500, m.Values.Sum());
    }

    [Fact]
    public void Unexpected_answer_gives_nothing()
    {
        Assert.Empty(Estimates.Parse("""{"status":200,"data":{}}"""));
        Assert.Empty(Estimates.Parse("""{"status":200}"""));
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Fetch_sends_the_key_and_the_name()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ApiAnswer);
        var (matches, minutes, error) = await Estimates.FetchAsync("Ss Shamble", "01234567-89ab-cdef-0123-456789abcdef", handler);
        Assert.Null(error);
        Assert.Equal(1500, matches!.Values.Sum());
        Assert.Equal(20000, minutes);
        Assert.Equal("01234567-89ab-cdef-0123-456789abcdef", handler.Request!.Headers.GetValues("Authorization").Single());
        Assert.Equal("https://fortnite-api.com/v2/stats/br/v2?timeWindow=lifetime&name=Ss%20Shamble", handler.Request.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused")]
    [InlineData(HttpStatusCode.Forbidden, "private")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    public async Task Fetch_errors_are_explained(HttpStatusCode status, string expected)
    {
        L.Set("en");
        var (matches, _, error) = await Estimates.FetchAsync("Nobody", "01234567-89ab-cdef-0123-456789abcdef",
            new FakeHandler(status, """{"status":403,"error":"the requested account's stats are not public"}"""));
        Assert.Null(matches);
        Assert.Contains(expected, error);
    }
}
