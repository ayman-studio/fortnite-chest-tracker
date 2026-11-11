namespace FortniteChestTracker;

/// <summary>
/// The chests that count for the treasure hunt, and what they are worth for each treasure: Ranked matches give no
/// treasure-hunt rewards (Epic), and a chest opened during Treasure Hours counts as many times as its boost (×1.5 for
/// the pirate token = one and a half chest). The chance of a treasure is then 1 − (1 − 1/rate)^(weighted chests).
/// </summary>
public static class Hunt
{
    /// <summary>Ranked playlists ("Habanero" in the replays).</summary>
    public static bool IsRanked(string? playlist)
    {
        var p = playlist ?? "";
        return p.Contains("Habanero", StringComparison.OrdinalIgnoreCase) || p.Contains("Ranked", StringComparison.OrdinalIgnoreCase);
    }

    public static DateTime StartOf(MatchRecord m)
    {
        var t = m.MatchStartUtc ?? m.AnalyzedAtUtc;
        return t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();
    }

    /// <summary>The match counts for the hunt: played since it started, Battle Royale, not Ranked (unless the season says so).</summary>
    public static bool Counts(MatchRecord m, DateTime sinceUtc) =>
        StartOf(m) >= sinceUtc && m.IsBattleRoyale && (Season.Current.Ranked || !IsRanked(m.Playlist));

    /// <summary>Chests of this match for one treasure, each one weighted by the Treasure Hours boost when it was opened.</summary>
    public static double Weighted(MatchRecord m, string key)
    {
        var start = StartOf(m);
        if (m.Chests.Count == 0 || m.Chests.Count != m.ChestsOpened)
        {
            // older records without the chest list: the time of the match
            return m.ChestsOpened * Season.Boost(key, start);
        }
        return m.Chests.Sum(c => Season.Boost(key, start.AddSeconds(c.TimeSeconds)));
    }

    /// <summary>Weighted chests of these matches for one treasure.</summary>
    public static double Weighted(IEnumerable<MatchRecord> matches, string key) => matches.Sum(m => Weighted(m, key));

    /// <summary>Chests opened during Treasure Hours in these matches.</summary>
    public static int DuringTreasureHours(IEnumerable<MatchRecord> matches) => matches.Sum(m =>
    {
        var start = StartOf(m);
        return m.Chests.Count > 0 ? m.Chests.Count(c => Season.ActiveAt(start.AddSeconds(c.TimeSeconds)) != null)
            : Season.ActiveAt(start) != null ? m.ChestsOpened : 0;
    });
}
