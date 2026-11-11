namespace FortniteChestTracker.Tests;

public class HudTests
{
    private static MatchRecord Match(int chests, DateTime start, int treasures = 0)
    {
        var m = new MatchRecord { ChestsOpened = chests, MatchStartUtc = start, AnalyzedAtUtc = start };
        for (var i = 0; i < treasures; i++)
        {
            m.TreasureFinds.Add(new TreasureFind { Item = "x" });
        }
        return m;
    }

    [Fact]
    public void Goal_is_the_chosen_treasure_or_the_next_milestone()
    {
        Assert.Equal("odds_vehicle", HudModel.Goal("odds_vehicle", 100).Key);
        Assert.Equal("odds_pirate", HudModel.Goal("nonsense", 100).Key); // unknown: the default goal
        Assert.Equal("odds_loading", HudModel.Goal("auto", 100).Key);    // gizmos never are a milestone
        Assert.Equal("odds_jam", HudModel.Goal("auto", 300).Key);
        Assert.Equal("odds_gold", HudModel.Goal("auto", 5_000_000).Key); // past everything: the rarest

        var g = HudModel.Goal("odds_pirate", 13_750);
        Assert.Equal(0.1, g.Progress, 6);
        Assert.Equal(TreasureOdds.Chance(13_750, 137_500), g.Chance, 9);
        Assert.Equal(1, HudModel.Goal("odds_loading", 10_000).Progress);
    }

    [Fact]
    public void Only_known_goals_are_accepted()
    {
        Assert.True(HudModel.IsGoal("auto"));
        Assert.True(HudModel.IsGoal("odds_gold"));
        Assert.False(HudModel.IsGoal("odds_nope"));
        Assert.False(HudModel.IsGoal(null));
    }

    [Fact]
    public void View_keeps_the_last_match_and_the_time_of_the_last_update()
    {
        var config = new Config();
        Assert.Null(HudModel.Build(new TrackerData(), config, null, false).LastChests);

        var counted = new DateTime(2026, 11, 4, 18, 5, 0, DateTimeKind.Utc);
        var data = new TrackerData();
        data.Matches.Add(new MatchRecord { ChestsOpened = 4, MatchStartUtc = counted.AddHours(-3), AnalyzedAtUtc = counted.AddHours(-2), DurationSeconds = 100 });
        data.Matches.Add(new MatchRecord { ChestsOpened = 23, MatchStartUtc = counted.AddMinutes(-20), AnalyzedAtUtc = counted, DurationSeconds = 1122 });

        var view = HudModel.Build(data, config, null, true);
        Assert.Equal(23, view.LastChests);
        Assert.Equal("18:42", view.LastDuration);
        Assert.Equal(counted.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), view.UpdatedAt);

        data.Matches[1].DurationSeconds = null;
        Assert.Null(HudModel.Build(data, config, null, true).LastDuration);
    }

    [Fact]
    public void View_counts_the_match_in_progress_and_the_hunt_since_its_start()
    {
        var config = new Config { TokenSinceUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), Goal = "odds_pirate" };
        var data = new TrackerData();
        data.Matches.Add(Match(30, new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), treasures: 1)); // before the hunt
        data.Matches.Add(Match(10, new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc)));
        data.Matches.Add(Match(7, new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc), treasures: 2));

        var idle = HudModel.Build(data, config, null, false);
        Assert.Equal(HudMode.Idle, idle.Mode);
        Assert.Equal(7, idle.Chests); // last match
        Assert.Equal(3, idle.Treasures);
        Assert.Equal(17, idle.Goal!.Chests);

        var waiting = HudModel.Build(data, config, null, true);
        Assert.Equal(HudMode.InProgress, waiting.Mode);
        Assert.Null(waiting.Chests);
        Assert.Equal(7, waiting.LastChests); // the last match stays readable during the next one

        var live = HudModel.Build(data, config, new MatchRecord { ChestsOpened = 5 }, true, estimated: 100);
        Assert.Equal(HudMode.Live, live.Mode);
        Assert.Equal(5, live.Chests);
        Assert.Equal(22, live.Goal!.Chests);
        Assert.StartsWith("≈", live.Total);
        Assert.NotEqual(idle.Key, live.Key);
    }

    [Fact]
    public void Percent_like_the_page()
    {
        L.Set("en");
        Assert.Equal("0.29%", HudModel.Percent(0.002869));
        Assert.Equal("4.9%", HudModel.Percent(0.0491));
        Assert.Equal("64%", HudModel.Percent(0.6412));
        Assert.Equal("99%", HudModel.Percent(0.9999));
        Assert.Equal("0%", HudModel.Percent(0));
    }

    [Fact]
    public void Png_round_trip()
    {
        var bgra = new byte[3 * 2 * 4];
        for (var i = 0; i < bgra.Length; i++) bgra[i] = (byte)(i * 10);
        var png = Png.Encode(3, 2, bgra, alpha: true);
        var back = Png.Decode(png);
        Assert.NotNull(back);
        Assert.Equal(3, back!.Value.Width);
        Assert.Equal(2, back.Value.Height);
        for (var p = 0; p < 6; p++)
        {
            Assert.Equal(bgra[p * 4 + 2], back.Value.Rgba[p * 4]);     // R
            Assert.Equal(bgra[p * 4 + 1], back.Value.Rgba[p * 4 + 1]); // G
            Assert.Equal(bgra[p * 4], back.Value.Rgba[p * 4 + 2]);     // B
            Assert.Equal(bgra[p * 4 + 3], back.Value.Rgba[p * 4 + 3]); // A
        }
    }

    [Fact]
    public void Hud_is_drawn_with_a_transparent_background()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var r = new HudRenderer();
        var view = new HudView(HudMode.Live, 12, "2 371", 55, 17, HudModel.Goal("odds_pirate", 400));
        var frame = r.Render(view, 1f);
        Assert.InRange(frame.Width, 300, 600);
        Assert.True(frame.Pixels[3] < 0.01f); // top-left corner: (almost) nothing
        Assert.Contains(frame.Pixels.Where((_, i) => i % 4 == 3), a => a > 0.99f); // opaque text and pictures

        var edit = r.Render(view, 1f, edit: true);
        Assert.True(edit.LockButton.W > 0);
        Assert.True(r.Render(view, 1f, minWidth: frame.Width + 40).Width == frame.Width + 40); // never narrower

        var png = Png.Decode(frame.ToPng());
        Assert.Equal(frame.Width, png!.Value.Width);

        // Compact: one line, much smaller, same padlock in edit mode
        var compact = r.Render(view with { Compact = true }, 1f);
        Assert.True(compact.Width < frame.Width && compact.Height < frame.Height / 2);
        Assert.True(r.Render(view with { Compact = true }, 1f, edit: true).LockButton.W > 0);
        Assert.NotEqual(view.Key, (view with { Compact = true }).Key);
    }
}
