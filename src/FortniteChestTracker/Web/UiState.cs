namespace FortniteChestTracker.Web;

// What the web page shows, sent as JSON (camelCase). Built on the main thread only (it reads the history);
// display strings that depend on stored data (modes, chest kinds, treasure names) are already translated here.

public sealed record UiState(
    UiApp App,
    string Status,
    UiProgress? Progress,
    UiSetup? Setup,
    IReadOnlyList<UiProfile> Profiles,
    bool ProfileChoiceNeeded,
    UiStats Stats,
    UiLive Live,
    IReadOnlyList<UiDay> PerDay,
    IReadOnlyList<UiCount> ByKind,
    int Destroyed,
    IReadOnlyList<UiCount> OtherInteractions,
    IReadOnlyList<UiMode> ByMode,
    IReadOnlyList<UiTreasure> Treasures,
    IReadOnlyList<UiMatch> Matches,
    IReadOnlyList<UiPrize> Prizes,
    UiOverlay Overlay,
    UiUpdate Update,
    UiSettings Settings,
    IReadOnlyList<UiNotice> Notices,
    IReadOnlyList<UiActivity> Activity,
    UiEstimate Estimate,
    IReadOnlyList<UiWeekday> ByWeekday,
    UiHunt Hunt,
    IReadOnlyList<UiLootBucket> ChestsVsPlacement,
    IReadOnlyList<UiMilestone> Milestones)
{
    /// <summary>Calendar of the treasure hunt.</summary>
    public UiSeason? Season { get; init; }

    /// <summary>Friends and squad (Friends tab).</summary>
    public UiFriends? Friends { get; init; }
}

/// <summary>
/// Friends tab: the server's state for the player (friends with their summaries, requests, suggestions, squad, squad
/// invites, as sent by worker/friends.js) and what the app adds (the player's own summary, the squad suggestion
/// after a match, the squad overlay).
/// </summary>
/// <param name="Status">"off" (sharing off), "noprofile" (no Epic account seen yet), "connecting", "ok", "offline", "taken".</param>
/// <param name="Key">Secret key of this PC, to link another PC to the same Epic account.</param>
public sealed record UiFriends(bool Sharing, string Status, string? MeId, string? MeName, string? MeSkin, System.Text.Json.JsonElement? Data,
    object? MeSummary, UiSquadSuggestion? Suggestion, UiSquadOverlay Overlay, int SkinsVersion, string? Key, string Server)
{
    /// <summary>Epic name from an invite link (fctracker://), shown with an "Add" button until sent or dismissed.</summary>
    public string? Invite { get; init; }
}

/// <summary>Friends played with in the last match who are not in the squad yet.</summary>
public sealed record UiSquadSuggestion(string Sid, IReadOnlyList<string> Names);

/// <param name="Hud">Hash of what the squad overlay draws (the page reloads /squad.png when it changes).</param>
/// <param name="Demo">Hash of the example squad shown on the home page while the player has no squad (/squad.png?demo=1).</param>
public sealed record UiSquadOverlay(bool Enabled, bool Running, bool Visible, bool Locked, string Period, string Hud, string Demo = "")
{
    /// <summary>Size and opacity of the squad overlay (percent), its own, apart from the main overlay's.</summary>
    public int Size { get; init; } = 100;
    public int Opacity { get; init; } = 100;

    /// <summary>overlay-friends.html, for an OBS browser source.</summary>
    public string ObsPath { get; init; } = "";
}

/// <summary>
/// Treasure hunt of the season: chests opened since it started, treasures found since then (gizmos apart from the
/// other rewards), and the drop rates per chest announced by Epic.
/// </summary>
/// <param name="GizmoLuck">"Luckier than X % of players" for gizmos (1–99), null when too few were expected.</param>
/// <param name="RewardLuck">Same for the weekly rewards.</param>
public sealed record UiHunt(DateTime Since, int Chests, int Matches, int Gizmos, int Rewards, IReadOnlyList<UiOdds> Odds,
    double GizmosExpected = 0, double RewardsExpected = 0, int? GizmoLuck = null, int? RewardLuck = null, bool Combined = false)
{
    /// <summary>Chests of Ranked matches since the start of the hunt: no treasure-hunt rewards there, left out of the chances.</summary>
    public int RankedChests { get; init; }

    /// <summary>Chests opened during Treasure Hours (they count more for the boosted treasures).</summary>
    public int BoostedChests { get; init; }

    /// <summary>Chests of teammates seen in the replays (opened near the player): they count for the squad chance.</summary>
    public int SquadChests { get; init; }
}

/// <summary>One prize of a week of the hunt; Found = the player got it (its prize id came out of one of their chests).</summary>
public sealed record UiSeasonPrize(string Key, string Name, string? Img, string? Odds, bool Found);

/// <summary>A week of the hunt: its prizes, the player's chests that week (Ranked apart) and the prizes they found.</summary>
public sealed record UiSeasonWeek(DateTime Start, DateTime End, string Title, string? Art, IReadOnlyList<UiSeasonPrize> Prizes, int Chests, int Found);

/// <summary>Treasure Hours window: rate multiplier per treasure (odds_* key).</summary>
public sealed record UiBoostWindow(DateTime Start, DateTime End, IReadOnlyDictionary<string, double> Boosts, bool Confirmed);

/// <summary>Calendar of the treasure hunt (season.json, updated from the website) and the player's token timer.</summary>
/// <param name="Week">Index of the week going on (-1 = outside the season).</param>
/// <param name="TokenActivated">When the player activated their Pirate Shop Access (14 days from then), null = not done.</param>
public sealed record UiSeason(string Name, DateTime Start, DateTime End, int Week, IReadOnlyList<UiSeasonWeek> Weeks,
    IReadOnlyList<UiBoostWindow> Hours, DateTime? TokenActivated, bool Notify);

/// <param name="Key">Text key of the treasure (odds_*).</param>
/// <param name="Odds">1 chance in this many chests, outside Treasure Hours.</param>
/// <param name="HourBoost">Multiplier during Treasure Hours (null = not boosted).</param>
/// <param name="Expected">Drops expected from the chests of the season (weekly treasures: at most one a week).</param>
public sealed record UiOdds(string Key, int Odds, double? HourBoost, double Expected = 0)
{
    /// <summary>Chests of the hunt for this treasure, each weighted by the Treasure Hours boost when it was opened.</summary>
    public double Weighted { get; init; }

    /// <summary>Same plus the teammates' chests seen in the replays: treasures are shared with the team.</summary>
    public double SquadWeighted { get; init; }
}

/// <summary>A milestone track (see <see cref="FortniteChestTracker.Milestones"/>): current value, last step reached, next step.</summary>
public sealed record UiMilestone(string Key, int Value, int? Reached, int? Next);

/// <summary>Matches that opened From…To chests (To null = "From+") and how they ended.</summary>
/// <param name="AverageTop">Average placement as a share of the teams of the lobby (0.18 = top 18 %), so modes compare.</param>
/// <param name="WinRate">Share of Victory Royales.</param>
/// <param name="AverageKills">Average eliminations, null when no match of the group knows them.</param>
public sealed record UiLootBucket(int From, int? To, int Matches, double AverageTop, double WinRate, double? AverageKills);

/// <param name="Day">0 = Monday … 6 = Sunday.</param>
public sealed record UiWeekday(int Day, int Matches, int Chests, double Average);

/// <summary>Chests estimated for the matches the replays do not cover (see <see cref="Estimates"/>).</summary>
public sealed record UiEstimate(bool Configured, string Source, string? Player, DateTime? UpdatedUtc, bool Include, bool HasApiKey, bool Fetching,
    string? Error, int? Played, int Tracked, int? Untracked, int? Estimated, int? Low, int? High, int? TotalWithEstimate,
    IReadOnlyList<UiEstimatePart> Parts, IReadOnlyDictionary<string, int> Matches, string? FortniteGgUrl)
{
    /// <summary>Matches in total with the ones without replay (shown "≈" when the estimate is included).</summary>
    public int? MatchesWithEstimate { get; init; }

    /// <summary>Minutes played in total with the matches without replay.</summary>
    public int? MinutesWithEstimate { get; init; }

    /// <summary>The time played comes from Epic's stats, not from the average length of the counted matches.</summary>
    public bool MinutesFromEpic { get; init; }

    /// <summary>Hours played typed in the form (manual source), to fill the field again.</summary>
    public int? Hours { get; init; }
}

public sealed record UiEstimatePart(string Bucket, string Mode, int Played, int Tracked, int Untracked, double Average, int Estimated);

public sealed record UiApp(string Version, string Lang, string DataFolder, string DemosFolder, bool DemosExists, string RepoUrl, string IssuesUrl,
    bool AutoMode, int? AutoCloseIn, bool Windows, string Site = "");

public sealed record UiProgress(int Done, int Total);

/// <param name="CanInstall">Run from the published exe, not the installed copy: the last step offers to install it.</param>
public sealed record UiSetup(string DemosFolder, bool DemosExists, int ExistingReplays, string DefaultDemos, bool CanStartWithFortnite, bool CanInstall = false);

public sealed record UiProfile(string Key, string Name, int Matches, int Chests, bool Current);

public sealed record UiStats(int Total, int Matches, double Average, int Best, int Session, int SessionMatches, int Treasures,
    int? LastChests, DateTime? LastWhen, int BestDay, DateTime? BestDayDate, string? Player,
    int Wins = 0, int Eliminations = 0, int Destroyed = 0, int MinutesPlayed = 0);

public sealed record UiLive(bool InProgress, int? Chests, int? Treasures);

public sealed record UiDay(DateTime Date, int Chests, int Matches);

/// <param name="Key">Stored (French) name of a chest kind, used by the page for its rarity colour.</param>
public sealed record UiCount(string Name, int Count, string? Key = null);

public sealed record UiMode(string Mode, int Matches, int Chests, double Average, int Best);

public sealed record UiTreasure(DateTime When, string Name, bool Golden, string? Id, string? ObtainedAt, string Source, bool Likely, bool Unusual,
    string? Blueprint, IReadOnlyList<string> AlsoPossible);

/// <param name="ChestTimes">When each chest was opened (seconds on the match clock, in order), null when the record has no times.</param>
public sealed record UiMatch(string Id, DateTime When, string Mode, string Placement, int? Rank, int? Players, int? Kills, int Chests, int Destroyed,
    int AmmoBoxes, int? DurationSeconds, IReadOnlyList<UiMatchTreasure> Treasures, IReadOnlyList<UiCount> Kinds, IReadOnlyList<int>? ChestTimes = null,
    IReadOnlyList<UiRare>? Rare = null);

/// <summary>A rare item another player got near the player (<see cref="RareSighting"/>).</summary>
/// <param name="Golden">From a golden treasure chest they opened (certain); false = seen holding it.</param>
public sealed record UiRare(string? Player, string Item, bool Golden, bool Mate, int At);

public sealed record UiMatchTreasure(string Name, bool Golden);

/// <param name="Unnamed">Still has no name in treasures.json (the player can name it from the page).</param>
public sealed record UiPrize(string Id, string Name, bool Unnamed);

public sealed record UiOverlay(bool Available, bool Running, bool Visible, bool Locked, int Size, int Opacity,
    bool ToggleHotkey, bool LockHotkey, string ObsPath, string Hud = "", string Goal = HudModel.DefaultGoal, string Style = OverlayAppearance.Full);

public sealed record UiUpdate(bool Available, string Mode, string? FoundVersion, string? FoundUrl, bool Checking, string? LastCheck, bool Installing);

/// <param name="Installed">Running the copy installed on this PC (Start menu, desktop, "Installed apps").</param>
/// <param name="CanInstall">Running from the published exe (not a build run through its dll).</param>
/// <param name="InstalledElsewhere">Not running the installed copy, but there is one.</param>
public sealed record UiSettings(string Language, bool StartWithFortnite, bool CanStartWithFortnite, string UpdateMode, bool ShowOverlay,
    int OverlaySize, int OverlayOpacity, bool OpenUiOnStart, string DemosFolder, IReadOnlyList<int> Sizes, IReadOnlyList<int> Opacities,
    IReadOnlyList<UiLanguage> Languages, string ArchiveFolder = "", int ArchiveCount = 0, bool Installed = false, bool CanInstall = false,
    bool InstalledElsewhere = false)
{
    /// <summary>Overlay hidden while a match is played.</summary>
    public bool HideInMatch { get; init; }

    /// <summary>Animated 3D island on the home page.</summary>
    public bool Scene3D { get; init; } = true;

    /// <summary>Blocks of the overlay shown (goal, stats, last, site).</summary>
    public IReadOnlyDictionary<string, bool>? Parts { get; init; }
}

public sealed record UiLanguage(string Code, string Name);

/// <param name="Action">Command run by the notice's button (with <paramref name="ActionLabel"/>), or null.</param>
public sealed record UiNotice(string Id, string Level, string Text, string? ActionLabel = null, string? Action = null);

public sealed record UiActivity(long Id, DateTime Time, string Level, string Text);

public static class UiStateBuilder
{
    private static DateTime When(MatchRecord m) => (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime();

    public static UiStats Stats(TrackerData view)
    {
        var matches = view.Matches;
        var total = matches.Sum(m => m.ChestsOpened);
        var session = matches.Where(ThisSession.Contains).ToList();
        var last = matches.OrderByDescending(When).FirstOrDefault();
        var bestDay = matches.GroupBy(m => When(m).Date).Select(g => (Date: g.Key, Chests: g.Sum(m => m.ChestsOpened)))
            .OrderByDescending(x => x.Chests).FirstOrDefault();
        return new UiStats(
            total,
            matches.Count,
            matches.Count > 0 ? Math.Round((double)total / matches.Count, 1) : 0,
            matches.Count > 0 ? matches.Max(m => m.ChestsOpened) : 0,
            session.Sum(m => m.ChestsOpened),
            session.Count,
            matches.Sum(m => m.TreasureFinds.Count),
            last?.ChestsOpened,
            last != null ? When(last) : null,
            bestDay.Chests,
            bestDay.Chests > 0 ? bestDay.Date : null,
            matches.OrderByDescending(When).Select(m => m.PlayerName).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
            matches.Count(m => m.Placement == 1),
            matches.Sum(m => m.Eliminations ?? 0),
            matches.Sum(m => m.Chests.Count(c => c.Destroyed)),
            matches.Sum(m => m.DurationSeconds ?? 0) / 60);
    }

    public static IReadOnlyList<UiDay> PerDay(TrackerData view, int days = 30)
    {
        var today = DateTime.Now.Date;
        var byDay = view.Matches.GroupBy(m => When(m).Date).ToDictionary(g => g.Key, g => (Chests: g.Sum(m => m.ChestsOpened), Matches: g.Count()));
        return Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1))
            .Select(d => byDay.TryGetValue(d, out var v) ? new UiDay(d, v.Chests, v.Matches) : new UiDay(d, 0, 0))
            .ToList();
    }

    /// <summary>Average chests per match for each day of the week (Monday first), over the whole history.</summary>
    public static IReadOnlyList<UiWeekday> ByWeekday(TrackerData view)
    {
        var byDay = view.Matches.GroupBy(m => ((int)When(m).DayOfWeek + 6) % 7).ToDictionary(g => g.Key, g => g.ToList());
        return Enumerable.Range(0, 7).Select(d => byDay.TryGetValue(d, out var list)
                ? new UiWeekday(d, list.Count, list.Sum(m => m.ChestsOpened), Math.Round(list.Average(m => m.ChestsOpened), 1))
                : new UiWeekday(d, 0, 0, 0))
            .ToList();
    }

    /// <summary>Players per team: placements are given per team (4th in squads = 4th squad).</summary>
    public static int TeamSize(string? playlist)
    {
        var p = (playlist ?? "").ToLowerInvariant();
        return p.Contains("solo") ? 1 : p.Contains("duo") ? 2 : p.Contains("trio") ? 3 : p.Contains("squad") ? 4 : 1;
    }

    /// <summary>Placement as a share of the teams of the lobby (2nd of 25 trios = 0.08), null when unknown.</summary>
    public static double? TopShare(MatchRecord m)
    {
        if (m.Placement is not int place || place <= 0 || m.TotalPlayers is not int players || players <= 0)
        {
            return null;
        }
        var teams = Math.Max(1, (int)Math.Ceiling(players / (double)TeamSize(m.Playlist)));
        return Math.Min(1, (double)place / teams);
    }

    /// <summary>
    /// Matches grouped by chests opened (0–9, 10–19, 20–29, 30+; steps of 5 for players who open few chests), at most
    /// four groups, the last one open-ended so that it still holds <paramref name="minMatches"/> matches. Groups with fewer
    /// matches are left out: an average over one or two matches says nothing.
    /// </summary>
    public static IReadOnlyList<UiLootBucket> ChestsVsPlacement(TrackerData view, int minMatches = 3)
    {
        var rated = view.Matches.Select(m => (Match: m, Top: TopShare(m))).Where(x => x.Top != null).ToList();
        if (rated.Count < minMatches * 2)
        {
            return [];
        }
        var chests = rated.Select(x => x.Match.ChestsOpened).OrderBy(c => c).ToList();
        var width = chests[(int)(0.75 * (chests.Count - 1))] >= 16 ? 10 : 5;
        var open = 0;
        for (var from = width; from <= 3 * width; from += width)
        {
            if (chests.Count(c => c >= from) >= minMatches)
            {
                open = from;
            }
        }

        var buckets = new List<UiLootBucket>();
        for (var from = 0; from <= open; from += width)
        {
            int? to = from == open ? null : from + width - 1;
            var group = rated.Where(x => x.Match.ChestsOpened >= from && (to == null || x.Match.ChestsOpened <= to)).ToList();
            if (group.Count < minMatches)
            {
                continue;
            }
            var kills = group.Where(x => x.Match.Eliminations != null).Select(x => (double)x.Match.Eliminations!.Value).ToList();
            buckets.Add(new UiLootBucket(from, to, group.Count,
                Math.Round(group.Average(x => x.Top!.Value), 3),
                Math.Round(group.Count(x => x.Match.Placement == 1) / (double)group.Count, 3),
                kills.Count > 0 ? Math.Round(kills.Average(), 1) : null));
        }
        return buckets;
    }

    public static IReadOnlyList<UiCount> ByKind(TrackerData view) => view.Matches
        .SelectMany(m => m.Chests)
        .GroupBy(c => c.Kind)
        .Select(g => new UiCount(L.Name(g.Key), g.Count(), g.Key))
        .OrderByDescending(c => c.Count)
        .ToList();

    public static IReadOnlyList<UiCount> OtherInteractions(TrackerData view) => view.Matches
        .SelectMany(m => m.OtherInteractions.Count > 0
            ? m.OtherInteractions
            : new Dictionary<string, int> { [ChestKinds.AmmoBox] = m.AmmoBoxesOpened, [ChestKinds.OtherContainer] = m.OtherContainersOpened })
        .GroupBy(kv => kv.Key)
        .Select(g => new UiCount(L.Name(g.Key), g.Sum(kv => kv.Value)))
        .Where(c => c.Count > 0)
        .OrderByDescending(c => c.Count)
        .ToList();

    public static IReadOnlyList<UiMode> ByMode(TrackerData view) => view.Matches
        .GroupBy(m => Pretty.Playlist(m.Playlist))
        .Select(g => new UiMode(g.Key, g.Count(), g.Sum(m => m.ChestsOpened), Math.Round(g.Average(m => m.ChestsOpened), 1), g.Max(m => m.ChestsOpened)))
        .OrderByDescending(m => m.Matches)
        .ToList();

    public static IReadOnlyList<UiTreasure> Treasures(TrackerData view) => view.Matches
        .OrderByDescending(When)
        .SelectMany(m => m.TreasureFinds.Select(f => new UiTreasure(
            When(m),
            f.DisplayName,
            f.FromGoldenChest,
            f.PrizeId ?? (f.RawItem.StartsWith("WID_", StringComparison.Ordinal) ? f.RawItem : null),
            f.ObtainedAtSeconds is float t ? (f.TimeExact ? Pretty.Clock(t) : $"{L.T("vers", "around", "hacia los")} {Pretty.Clock(t)}") : null,
            f.SourceLabel,
            !f.SourceCertain,
            !f.SourceIsChest,
            !f.SourceIsChest && !string.IsNullOrEmpty(f.SourceBlueprint) ? f.SourceBlueprint : null,
            f.AlsoPossible.Select(L.Name).ToList())))
        .ToList();

    public static IReadOnlyList<UiMatch> Matches(TrackerData view, int max = 300) => view.Matches
        .OrderByDescending(When)
        .Take(max)
        .Select(m => new UiMatch(
            m.SessionId,
            When(m),
            Pretty.Playlist(m.Playlist),
            Pretty.Placement(m),
            m.Placement is > 0 ? m.Placement : null,
            m.TotalPlayers is > 0 ? m.TotalPlayers : null,
            m.Eliminations,
            m.ChestsOpened,
            m.Chests.Count(c => c.Destroyed),
            m.AmmoBoxesOpened,
            m.DurationSeconds,
            m.TreasureFinds.Select(f => new UiMatchTreasure(f.DisplayName, f.FromGoldenChest)).ToList(),
            m.Chests.GroupBy(c => c.Kind).Select(g => new UiCount(L.Name(g.Key), g.Count(), g.Key)).OrderByDescending(c => c.Count).ToList(),
            ChestTimes(m),
            m.RareNearby?.Select(r => new UiRare(r.PlayerName, r.DisplayName, r.Golden, r.IsMate, (int)Math.Round(r.AtSeconds))).ToList()))
        .ToList();

    /// <summary>Seconds (match clock) at which the chests were opened; null when none has a time (very old records).</summary>
    public static IReadOnlyList<int>? ChestTimes(MatchRecord m) => m.Chests.Any(c => c.TimeSeconds > 0)
        ? m.Chests.Select(c => (int)Math.Round(c.TimeSeconds)).Order().ToList()
        : null;

    /// <summary>Prize ids of treasures.json: real golden chest prizes only (unknown gizmos cannot be told apart).</summary>
    public static IReadOnlyList<UiPrize> Prizes() => TreasureNames.PrizeNames
        .Where(kv => !kv.Key.StartsWith(TreasureNames.UnknownPrefix, StringComparison.OrdinalIgnoreCase)
                     && !kv.Key.StartsWith("Inconnu:", StringComparison.OrdinalIgnoreCase))
        .Select(kv => new UiPrize(kv.Key, kv.Value, string.IsNullOrWhiteSpace(kv.Value)))
        .OrderByDescending(p => p.Unnamed)
        .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static IReadOnlyList<UiActivity> Activity(int max = 120) => FortniteChestTracker.Activity.Snapshot()
        .TakeLast(max)
        .Select(e => new UiActivity(e.Id, e.Time, e.Level.ToString().ToLowerInvariant(), e.Text))
        .ToList();
}
