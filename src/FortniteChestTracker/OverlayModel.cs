using System.Globalization;
using FortniteChestTracker.Web;

namespace FortniteChestTracker;

/// <summary>Size and opacity choices of the overlay window (percent), offered in the settings.</summary>
public static class OverlayAppearance
{
    public static readonly int[] Sizes = { 70, 80, 90, 100, 115, 130, 150, 175 };

    /// <summary>Below 30 % the numbers can no longer be read.</summary>
    public static readonly int[] Opacities = { 100, 90, 80, 70, 60, 50, 40, 30 };

    /// <summary>Layouts of the overlay: the full HUD, or one compact line.</summary>
    public const string Full = "full", Compact = "compact";

    public static int ClampSize(int percent) => Math.Clamp(percent, Sizes[0], Sizes[^1]);
    public static int ClampOpacity(int percent) => Math.Clamp(percent, Opacities[^1], 100);

    /// <summary>Next value of the list, back to the first one after the last (or for a value edited by hand).</summary>
    public static int Next(int[] values, int current) => values[(Array.IndexOf(values, current) + 1) % values.Length];
}

/// <summary>What the hero counter of the overlay is about.</summary>
public enum HudMode
{
    /// <summary>Between two matches: the chests of the last match.</summary>
    Idle,

    /// <summary>A match is being recorded but its replay cannot be read yet.</summary>
    InProgress,

    /// <summary>The chests of the match in progress, read from its replay.</summary>
    Live,
}

/// <summary>Treasure aimed at (the one chosen on the home page): chance so far and how far the "dig" went.</summary>
/// <param name="Progress">Chests opened / average number of chests for one drop (0 … 1).</param>
public sealed record HudGoal(string Key, string Name, double Chance, double Progress, int Chests, int Odds);

/// <summary>What the overlays (game window, OBS page, preview of the page) show.</summary>
/// <param name="Chests">Hero number: chests of the match in progress (Live), of the last match (Idle); null = "—" / "…".</param>
/// <param name="Total">All the chests counted ("≈" when the estimate of older matches is included).</param>
public sealed record HudView(HudMode Mode, int? Chests, string Total, int Session, int Treasures, HudGoal? Goal)
{
    /// <summary>Website shown at the bottom (empty = none).</summary>
    public string Site { get; init; } = AppInfo.SiteName;

    /// <summary>Compact layout (one line) instead of the full HUD; chosen by the player.</summary>
    public bool Compact { get; init; }

    /// <summary>Matches counted so far (one more = a match was just counted: the HUD plays "+N").</summary>
    public int Matches { get; init; }

    /// <summary>Chests of the last counted match: still shown while another match is played (null = no match yet).</summary>
    public int? LastChests { get; init; }

    /// <summary>Length of the last counted match, "18:42" (null = unknown).</summary>
    public string? LastDuration { get; init; }

    /// <summary>Local time ("20:15") the count was last updated, that is when a match was last counted.</summary>
    public string? UpdatedAt { get; init; }

    public static HudView Empty { get; } = new(HudMode.Idle, null, "0", 0, 0, null);

    /// <summary>Short fingerprint of what is drawn (cache key of the picture served to the page).</summary>
    public string Key => string.Join("|", Mode, Chests, Total, Session, Treasures, Goal?.Key, Goal?.Chests, Site, Compact, LastChests, LastDuration, UpdatedAt, L.Code);
}

/// <summary>Builds the overlay view from the saved matches and the match in progress.</summary>
public static class HudModel
{
    public const string DefaultGoal = "odds_pirate";

    /// <summary>A goal the page can choose: one of the treasures, or "auto" (the next milestone).</summary>
    public static bool IsGoal(string? key) => key == "auto" || TreasureOdds.All.Any(o => o.Key == key);

    /// <summary>Start of the treasure hunt (UTC).</summary>
    public static DateTime HuntSince(Config config) => config.TokenSinceUtc.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(config.TokenSinceUtc, DateTimeKind.Utc)
        : config.TokenSinceUtc.ToUniversalTime();

    /// <param name="live">Figures of the match in progress, when the replay could be read (null otherwise).</param>
    /// <param name="inProgress">A match is being recorded (even if its figures are not readable yet).</param>
    /// <param name="estimated">Chests estimated for the matches without replay, added to the total ("≈") when the player chose it.</param>
    public static HudView Build(TrackerData d, Config config, MatchRecord? live, bool inProgress, int estimated = 0)
    {
        var liveChests = live?.ChestsOpened ?? 0;
        var total = d.Matches.Sum(m => m.ChestsOpened) + liveChests + estimated;
        var session = d.Matches.Where(ThisSession.Contains).Sum(m => m.ChestsOpened) + liveChests;
        var treasures = d.Matches.Sum(m => m.TreasureFinds.Count) + (live?.TreasureFinds.Count ?? 0);

        var since = HuntSince(config);
        var hunt = d.Matches.Where(m => Hunt.Counts(m, since)).ToList();
        // the match in progress has no start time yet: it counts unless it is Ranked
        if (live != null && (live.MatchStartUtc == null ? Season.Current.Ranked || !Hunt.IsRanked(live.Playlist) : Hunt.Counts(live, since)))
        {
            hunt.Add(live);
        }
        var huntChests = hunt.Sum(m => m.ChestsOpened);
        var goalKey = TreasureOdds.All.Any(o => o.Key == config.Goal) ? config.Goal! : config.Goal == "auto" ? Goal("auto", huntChests).Key : DefaultGoal;
        var weighted = Hunt.Weighted(hunt, goalKey);

        var last = d.Matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).FirstOrDefault();
        var (mode, chests) = live != null ? (HudMode.Live, (int?)live.ChestsOpened)
            : inProgress ? (HudMode.InProgress, (int?)null)
            : (HudMode.Idle, last?.ChestsOpened);

        return new HudView(mode, chests, estimated > 0 ? "≈" + L.N(total) : L.N(total), session, treasures, Goal(config.Goal, huntChests, weighted))
        {
            Compact = config.OverlayStyle == OverlayAppearance.Compact,
            Matches = d.Matches.Count,
            LastChests = last?.ChestsOpened,
            LastDuration = last?.DurationSeconds is { } s ? $"{s / 60}:{s % 60:00}" : null,
            UpdatedAt = last == null ? null : d.Matches.Max(m => AsUtc(m.AnalyzedAtUtc)).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
        };
    }

    private static DateTime AsUtc(DateTime t) => t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime();

    /// <summary>The chosen treasure, or with "auto" the first weekly treasure (most common first) not reached on average yet.</summary>
    /// <param name="weighted">Chests weighted by the Treasure Hours boosts (null = the chests as they are).</param>
    public static HudGoal Goal(string? key, int chests, double? weighted = null)
    {
        var entry = TreasureOdds.All.FirstOrDefault(o => o.Key == key);
        if (entry == null)
        {
            var list = TreasureOdds.All.Where(o => o.Key != "odds_gizmo").OrderBy(o => o.Odds).ToList();
            entry = key == "auto" ? list.FirstOrDefault(o => chests < o.Odds) ?? list[^1] : TreasureOdds.All.First(o => o.Key == DefaultGoal);
        }
        return new HudGoal(entry.Key, UiTexts.Get("short_" + entry.Key), TreasureOdds.Chance(weighted ?? chests, entry.Odds),
            Math.Min(1, (double)chests / entry.Odds), chests, entry.Odds);
    }

    /// <summary>"0,84 %" like the page: whole percents from 10 %, one decimal from 1 %, else two significant digits.</summary>
    public static string Percent(double p)
    {
        p = Math.Min(p, 0.99) * 100;
        string n;
        if (p >= 10) n = p.ToString("0", L.Culture);
        else if (p >= 1) n = p.ToString("0.#", L.Culture);
        else if (p <= 0) n = "0";
        else
        {
            var digits = Math.Max(0, 1 - (int)Math.Floor(Math.Log10(p)));
            n = Math.Round(p, digits).ToString("0." + new string('#', digits), L.Culture);
        }
        return L.Code == "en" ? n + "%" : n + " %";
    }
}
