namespace FortniteChestTracker.Tests;

public class TreasureOddsTests
{
    [Fact]
    public void Rates_announced_by_Epic()
    {
        var odds = TreasureOdds.All.ToDictionary(o => o.Key, o => o.Odds);
        Assert.Equal(127, odds["odds_gizmo"]);
        Assert.Equal(272, odds["odds_loading"]);
        Assert.Equal(391, odds["odds_jam"]);
        Assert.Equal(568, odds["odds_pickaxe"]);
        Assert.Equal(7_813, odds["odds_vehicle"]);
        Assert.Equal(137_500, odds["odds_pirate"]);
        Assert.Equal(1_562_500, odds["odds_gold"]);
    }

    [Fact]
    public void Chance_of_at_least_one_drop()
    {
        Assert.Equal(0, TreasureOdds.Chance(0, 568));
        // About 63 % (1 − 1/e) after as many chests as the rate, 50 % after rate × ln 2.
        Assert.InRange(TreasureOdds.Chance(568, 568), 0.631, 0.633);
        Assert.InRange(TreasureOdds.Chance(394, 568), 0.499, 0.501);
        Assert.InRange(TreasureOdds.Chance(395, 137_500), 0.00286, 0.00288);
    }

    [Fact]
    public void Luck_is_the_poisson_rank_of_the_count_found()
    {
        // λ = 1: P(0) = P(1) = e^-1 ≈ 0.368 -> nothing found = 0.184, one found = 0.368 + 0.184 = 0.552
        Assert.InRange(TreasureOdds.LuckFraction(0, 1), 0.1839, 0.1840);
        Assert.InRange(TreasureOdds.LuckFraction(1, 1), 0.5518, 0.5519);
        Assert.Equal(18, TreasureOdds.LuckPercent(0, 1));
        Assert.Equal(55, TreasureOdds.LuckPercent(1, 1));
        // λ = 3.1, 2 found: P(0) + P(1) + P(2)/2 = e^-3.1 (1 + 3.1 + 4.805/2) ≈ 0.293
        Assert.Equal(29, TreasureOdds.LuckPercent(2, 3.1));
        // Finding about the mean is average luck, even for very large means (no underflow of e^-λ)
        Assert.InRange(TreasureOdds.LuckFraction(800, 800), 0.49, 0.51);
        Assert.InRange(TreasureOdds.LuckFraction(1000, 800), 0.999, 1.0);
    }

    [Fact]
    public void Luck_is_clamped_and_hidden_when_too_few_drops_were_expected()
    {
        Assert.Null(TreasureOdds.LuckPercent(0, 0.29));
        Assert.Null(TreasureOdds.LuckPercent(1, 0));
        Assert.NotNull(TreasureOdds.LuckPercent(0, 0.3));
        Assert.Equal(1, TreasureOdds.LuckPercent(0, 12));   // e^-12 / 2 ≈ 0.000003
        Assert.Equal(99, TreasureOdds.LuckPercent(9, 0.5)); // far above the mean
        Assert.Equal(37, TreasureOdds.LuckPercent(0, 0.3)); // e^-0.3 / 2
    }

    [Fact]
    public void Every_rate_has_its_name_in_the_interface()
    {
        var keys = Web.UiTexts.Keys.ToHashSet();
        Assert.All(TreasureOdds.All, o => Assert.Contains(o.Key, keys));
    }

    [Fact]
    public void Weekly_rewards_expected_drop_at_most_once_a_week()
    {
        var since = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        // 2,000 chests in one week: the loading screen, jam track and pickaxe are almost certain, but count once each.
        var oneWeek = TreasureOdds.ExpectedRewards(new[] { (since.AddDays(2), 2000) }, since);
        Assert.InRange(oneWeek, 3.0, 3.4);
        // The same chests spread over two weeks: up to two of each weekly treasure.
        var twoWeeks = TreasureOdds.ExpectedRewards(new[] { (since.AddDays(2), 1000), (since.AddDays(9), 1000) }, since);
        Assert.True(twoWeeks > oneWeek);
        Assert.Equal(0, TreasureOdds.ExpectedRewards(Array.Empty<(DateTime, int)>(), since));
    }
}
