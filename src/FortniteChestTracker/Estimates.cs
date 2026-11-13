using System.Text.RegularExpressions;

namespace FortniteChestTracker;

/// <summary>
/// Matches of the current season that the replays do not cover (before replays were turned on, or deleted by
/// Fortnite): the season's total matches and time played, copied by the player from fortnite.gg (the site only shows
/// the current season, and refuses automated access). Saved in config.json.
/// </summary>
public sealed class EstimateSettings
{
    /// <summary>Major game version of the season the numbers cover ("43" for Release-43.x), set when they are saved.</summary>
    public string? Season { get; set; }

    /// <summary>Matches played this season, all modes (the counted ones included).</summary>
    public int Played { get; set; }

    /// <summary>
    /// Minutes played this season (the counted matches included). Only their average per match is used: Epic counts
    /// less time per match than the replays do (about 10 min against 12.5 on the same account, likely the time alive),
    /// so the counted matches keep their replay time.
    /// </summary>
    public int Minutes { get; set; }

    /// <summary>Epic name the numbers belong to.</summary>
    public string? Player { get; set; }

    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Add the estimate to the total shown (interface, overlay, friends' season), marked "≈".</summary>
    public bool Include { get; set; }

    /// <summary>Numbers saved before 1.3.2 (per mode, all time, fortnite-api.com): not of a season, dropped.</summary>
    public bool IsLegacy => Season == null && UpdatedUtc != null;
}

/// <param name="Played">Matches played in the season (fortnite.gg).</param>
/// <param name="Tracked">Matches of that season counted from the replays.</param>
/// <param name="Average">Chests per match used for the matches without replay.</param>
/// <param name="Low">Low end of a 95 % range (uncertainty on the average only).</param>
/// <param name="Matches">Matches in total: every counted one + the ones without replay.</param>
/// <param name="Minutes">Minutes played in total: every counted match + the ones without replay.</param>
public sealed record EstimateResult(string Season, int Played, int Tracked, int Untracked, double Average, int Estimated, int Low, int High,
    int Matches, int Minutes);

public static class Estimates
{
    /// <summary>
    /// Chests of the season's matches without replay = (matches played − matches of that season counted) × average
    /// chests per match (of that season's replays when there are at least 5, else of all of them). Null when nothing
    /// usable was entered.
    /// </summary>
    public static EstimateResult? Compute(EstimateSettings? settings, IReadOnlyList<MatchRecord> tracked)
    {
        if (settings is not { Season: { } season, Played: > 0, Minutes: > 0 } || tracked.Count == 0)
        {
            return null;
        }
        var ofSeason = tracked.Where(m => SeasonOf(m.GameVersion) == season).ToList();
        var basis = ofSeason.Count >= 5 ? ofSeason : tracked;
        var chests = basis.Select(m => (double)m.ChestsOpened).ToList();
        var mean = chests.Average();
        var sd = Math.Sqrt(chests.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, chests.Count - 1));

        var untracked = Math.Max(0, settings.Played - ofSeason.Count);
        var estimated = (int)Math.Round(untracked * mean);
        var margin = (int)Math.Round(1.96 * sd / Math.Sqrt(chests.Count) * untracked);

        // Time: the counted matches with their replay time, the ones without replay at fortnite.gg's minutes per match.
        var minutes = tracked.Sum(m => m.DurationSeconds ?? 0) / 60 + (int)Math.Round(untracked * (double)settings.Minutes / settings.Played);

        return new EstimateResult(season, settings.Played, ofSeason.Count, untracked, Math.Round(mean, 1), estimated,
            Math.Max(0, estimated - margin), estimated + margin, tracked.Count + untracked, minutes);
    }

    /// <summary>Season of a replay: the major game version ("++Fortnite+Release-43.10" gives "43"), null when unknown.</summary>
    public static string? SeasonOf(string? gameVersion)
    {
        var m = Regex.Match(gameVersion ?? "", @"Release-(\d+)\.");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Season of the latest replay (the current one, as fortnite.gg shows it), null when unknown.</summary>
    public static string? CurrentSeason(IEnumerable<MatchRecord> tracked) =>
        tracked.Where(m => SeasonOf(m.GameVersion) != null).OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc)
            .Select(m => SeasonOf(m.GameVersion)).FirstOrDefault();

    /// <summary>The player's page on fortnite.gg, all modes (opened in the browser: the site refuses automated access).</summary>
    public static string FortniteGgUrl(string player) => "https://fortnite.gg/stats?player=" + Uri.EscapeDataString(player);
}
