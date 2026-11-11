namespace FortniteChestTracker;

/// <summary>
/// Treasure drop rates per chest opened in a normal match, as announced by Epic for the season (constant since launch,
/// the same at any time of day except during Treasure Hours). Apart from gizmos, the same treasure cannot drop twice;
/// the "weekly" treasures change every week.
/// </summary>
public static class TreasureOdds
{
    /// <param name="Key">Text key in the interface (odds_*).</param>
    /// <param name="Odds">1 chance in this many chests.</param>
    /// <param name="HourBoost">Rate multiplier during Treasure Hours (null = not boosted). Epic may raise it for some hours.</param>
    public sealed record Entry(string Key, int Odds, double? HourBoost);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new("odds_gizmo", 127, null),
        new("odds_loading", 272, null),
        new("odds_jam", 391, null),
        new("odds_pickaxe", 568, null),
        new("odds_vehicle", 7_813, 10),
        new("odds_pirate", 137_500, 1.5),
        new("odds_gold", 1_562_500, 7.5),
    ];

    /// <summary>Chance of at least one drop after <paramref name="chests"/> chests at 1 in <paramref name="odds"/>.</summary>
    public static double Chance(double chests, int odds) => 1 - Math.Pow(1 - 1.0 / odds, Math.Max(0, chests));

    /// <summary>
    /// Drops of one treasure expected from these matches of the hunt (see <see cref="Hunt"/>: Treasure Hours boosts
    /// included): gizmos repeat; weekly treasures at most once per week of the season; the pirate token once.
    /// </summary>
    public static double ExpectedFor(Entry o, IReadOnlyCollection<MatchRecord> matches, DateTime sinceUtc)
    {
        if (o.Key == "odds_gizmo")
        {
            return Hunt.Weighted(matches, o.Key) / o.Odds;
        }
        if (o.Key == "odds_pirate")
        {
            return Chance(Hunt.Weighted(matches, o.Key), o.Odds);
        }
        return matches.GroupBy(m => Season.WeekAt(Hunt.StartOf(m)) is var w and >= 0 ? w : 1000 + (int)Math.Floor((Hunt.StartOf(m) - sinceUtc).TotalDays / 7))
            .Sum(g => Chance(Hunt.Weighted(g, o.Key), o.Odds));
    }

    /// <summary>Weekly rewards (all but gizmos) expected from these matches of the hunt.</summary>
    public static double ExpectedRewardsFor(IReadOnlyCollection<MatchRecord> matches, DateTime sinceUtc) =>
        All.Where(o => o.Key != "odds_gizmo").Sum(o => ExpectedFor(o, matches, sinceUtc));

    /// <summary>
    /// Weekly rewards expected from these chests. Each weekly treasure drops at most once a week (and the pirate token
    /// once), so the expectation is the chance of at least one drop per treasure and per week, not chests / rate.
    /// Weeks are counted in 7-day blocks from the start of the hunt.
    /// </summary>
    public static double ExpectedRewards(IEnumerable<(DateTime StartUtc, int Chests)> matches, DateTime sinceUtc)
    {
        var list = matches.ToList();
        return All.Where(o => o.Key != "odds_gizmo").Sum(o => Expected(o, list, sinceUtc));
    }

    /// <summary>Drops of one treasure expected from these chests: gizmos repeat; weekly ones at most once a week; the
    /// pirate token once.</summary>
    public static double Expected(Entry o, IReadOnlyCollection<(DateTime StartUtc, int Chests)> matches, DateTime sinceUtc)
    {
        var total = matches.Sum(m => m.Chests);
        if (o.Key == "odds_gizmo")
        {
            return (double)total / o.Odds;
        }
        if (o.Key == "odds_pirate")
        {
            return Chance(total, o.Odds);
        }
        return matches.GroupBy(m => (int)Math.Floor((m.StartUtc - sinceUtc).TotalDays / 7)).Sum(g => Chance(g.Sum(m => m.Chests), o.Odds));
    }

    /// <summary>Below this many expected drops, a luck figure would only reflect a coin toss: none is shown.</summary>
    public const double MinExpectedForLuck = 0.3;

    /// <summary>
    /// Share of players with the same chests who found fewer treasures, ties counted half: P(N &lt; found) + P(N = found) / 2
    /// with N following a Poisson distribution of mean <paramref name="expected"/>. 0.5 = exactly as lucky as the average.
    /// </summary>
    public static double LuckFraction(int found, double expected)
    {
        if (expected <= 0)
        {
            return found > 0 ? 1 : 0.5;
        }
        // Terms computed in log space: exp(-λ) underflows past λ ≈ 745 (heavy players and gizmos).
        var logLambda = Math.Log(expected);
        var logTerm = -expected; // log P(N = 0)
        var below = 0.0;
        for (var i = 0; i < Math.Max(0, found); i++)
        {
            below += Math.Exp(logTerm);
            logTerm += logLambda - Math.Log(i + 1);
        }
        return Math.Min(1, below + Math.Exp(logTerm) / 2);
    }

    /// <summary>"Luckier than X % of players" (1 to 99), or null when too few drops were expected to tell.</summary>
    public static int? LuckPercent(int found, double expected) =>
        expected < MinExpectedForLuck ? null : Math.Clamp((int)Math.Round(LuckFraction(found, expected) * 100), 1, 99);
}
