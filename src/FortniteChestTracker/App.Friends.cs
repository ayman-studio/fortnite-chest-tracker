using System.Text.Json;
using FortniteChestTracker.Web;

namespace FortniteChestTracker;

// Friends and squads (Friends tab, squad overlay). The server keeps each player's summary and recent matches; the app
// syncs every minute (every 10 s in a squad) and right after a match or when a match starts / ends. Everything here
// runs on the main loop, except the HTTP calls themselves.
public sealed partial class App
{
    private JsonElement? _friends;            // last state from the server
    private string _friendsStatus = "connecting";
    private bool _friendsBusy;
    private DateTime _nextFriendsSync = DateTime.UtcNow.AddSeconds(3);
    private bool _lastPlaying;
    private readonly HashSet<string> _sentMatches = new();
    private string? _sentSummary;
    private string? _syncedFor;               // Epic id the sent matches belong to
    private SquadSuggestion? _squadSuggestion;
    private readonly HashSet<string> _dismissedSuggestions = new();
    private string? _squadOverlayFor;         // squad id the squad overlay was shown for
    private int _friendsFailures;
    private FriendsLive? _live;               // doorbell of the server (sync as soon as something changed)
    private volatile SquadView _squadDemo = SquadView.Empty; // example shown on the home page without a squad

    // Outfits of the example when the player has no friends yet (Epic's default outfits).
    private static readonly string[] DemoSkins = { "CID_A_273_Athena_Commando_F_Prime_B", "CID_A_275_Athena_Commando_F_Prime_D", "CID_A_274_Athena_Commando_F_Prime_C" };

    /// <summary>An example squad: the player and up to two friends (or example names), with example chests.</summary>
    private SquadView SquadDemo(FriendsClient.Identity? me)
    {
        var friends = Items(_friends, "friends").Take(2).Select(f => (Name: Str(f, "name") ?? "?", Skin: Str(f, "skin"))).ToList();
        for (var i = friends.Count; i < 2; i++)
        {
            friends.Add((L.T($"Ami {i + 1}", $"Friend {i + 1}", $"Amigo {i + 1}"), DemoSkins[i]));
        }
        // Same numbers as the chosen period: a session adds up several matches (the one still playing keeps its total).
        var session = _config.SquadOverlayPeriod == "session";
        var members = new List<SquadMember>
        {
            new(friends[0].Name, friends[0].Skin, session ? 168 : 42, false, false),
            new(me?.Name ?? L.T("Toi", "You", "Tú"), me?.Skin ?? DemoSkins[2], session ? 131 : 35, false, true),
            new(friends[1].Name, friends[1].Skin, session ? 97 : null, true, false),
        };
        foreach (var m in members)
        {
            SkinCache.Want(m.Skin);
        }
        return new SquadView(session ? "session" : "last", members);
    }

    private sealed record SquadSuggestion(string Sid, long Since, DateTime Until, List<(string Id, string Name)> Friends);

    // ---- Identity and what is sent --------------------------------------------------------------------------

    /// <summary>The player as the friends server knows them (null: sharing off, or no Epic account seen in a replay yet).</summary>
    private FriendsClient.Identity? FriendIdentity()
    {
        if (!_config.FriendsSharing || _setupPending)
        {
            return null;
        }
        var profile = Profiles.Current(_data, _config);
        if (profile == null || !FriendsClient.IsEpicId(profile.Key))
        {
            return null;
        }
        var id = profile.Key.ToLowerInvariant();
        if (!_config.FriendKeys.TryGetValue(id, out var key) || !FriendsClient.IsKey(key))
        {
            key = FriendsClient.NewKey();
            _config.FriendKeys[id] = key;
            SaveConfig();
        }
        var skin = Profiles.View(_data, _config).Matches.OrderByDescending(EndOf).Select(m => m.Skin).FirstOrDefault(s => s != null);
        return new FriendsClient.Identity(id, key, profile.Name, skin);
    }

    private static DateTime EndOf(MatchRecord m) => Hunt.StartOf(m).AddSeconds(m.DurationSeconds ?? 0);

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>What friends see of the player: season, session, treasure odds, treasures found, last match.</summary>
    private Dictionary<string, object?> FriendsSummary(TrackerData view)
    {
        var since = HudModel.HuntSince(_config);
        var hunt = HuntState(view);
        var stats = UiStateBuilder.Stats(view);
        var season = view.Matches.Where(m => Hunt.StartOf(m) >= since).ToList();
        var seasonChests = season.Sum(m => m.ChestsOpened);
        var treasures = season
            .SelectMany(m => m.TreasureFinds.Select(f => (f, at: Hunt.StartOf(m).AddSeconds(f.ObtainedAtSeconds ?? 0))))
            .OrderByDescending(x => x.at).Take(20)
            .Select(x => new Dictionary<string, object?> { ["n"] = x.f.DisplayName, ["g"] = x.f.FromGoldenChest, ["t"] = Ms(x.at) })
            .ToList();
        var odds = hunt.Odds.ToDictionary(o => o.Key, o => (object?)Math.Round(1 - Math.Pow(1 - 1.0 / o.Odds, o.Weighted), 5));
        var last = view.Matches.OrderByDescending(EndOf).FirstOrDefault();
        return new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["season"] = new Dictionary<string, object?> { ["chests"] = seasonChests, ["matches"] = season.Count, ["best"] = season.Count > 0 ? season.Max(m => m.ChestsOpened) : 0 },
            ["session"] = new Dictionary<string, object?> { ["chests"] = stats.Session, ["matches"] = stats.SessionMatches },
            ["total"] = new Dictionary<string, object?> { ["chests"] = stats.Total, ["matches"] = stats.Matches },
            ["odds"] = odds,
            ["treasures"] = treasures,
            ["last"] = last == null ? null : new Dictionary<string, object?>
            {
                ["chests"] = last.ChestsOpened, ["ended"] = Ms(EndOf(last)), ["dur"] = last.DurationSeconds, ["place"] = last.Placement,
            },
        };
    }

    // ---- Sync loop --------------------------------------------------------------------------------------------

    private bool InSquad => _friends is { } f && f.TryGetProperty("squad", out var s) && s.ValueKind == JsonValueKind.Object;

    private bool HasPendingInvites => _friends is { } f && f.TryGetProperty("invites", out var i) && i.GetArrayLength() > 0;

    /// <summary>Called by the main loop: syncs when it is time (or soon after a match started / ended).</summary>
    private void FriendsTick()
    {
        CheckSessionEnd();
        var playing = _tracker.IsLive;
        if (playing != _lastPlaying)
        {
            _lastPlaying = playing;
            _nextFriendsSync = DateTime.UtcNow; // friends see "in a match" / the result at once
        }
        if (_friendsBusy || DateTime.UtcNow < _nextFriendsSync)
        {
            return;
        }
        var me = FriendIdentity();
        if (me == null)
        {
            _friendsStatus = _config.FriendsSharing ? "noprofile" : "off";
            _nextFriendsSync = DateTime.UtcNow.AddSeconds(30);
            _live?.Dispose();
            _live = null;
            if (_friends != null)
            {
                _friends = null;
                UpdateSquadOverlay();
            }
            return;
        }
        if (_syncedFor != me.Id)
        {
            // another profile: everything is sent again for it
            _syncedFor = me.Id;
            _sentMatches.Clear();
            _sentSummary = null;
            _friends = null;
        }

        var view = Profiles.View(_data, _config);
        var summary = JsonSerializer.Serialize(FriendsSummary(view));
        var recent = view.Matches
            .Where(m => !string.IsNullOrEmpty(m.SessionId) && EndOf(m) > DateTime.UtcNow.AddHours(-71) && !_sentMatches.Contains(m.SessionId))
            .OrderBy(EndOf).TakeLast(30).ToList();
        var body = new Dictionary<string, object?>
        {
            ["playing"] = playing,
            ["summary"] = summary == _sentSummary ? null : JsonDocument.Parse(summary).RootElement.Clone(),
            ["matches"] = recent.Select(m => new Dictionary<string, object?>
            {
                ["sid"] = m.SessionId, ["ended"] = Ms(EndOf(m)), ["chests"] = m.ChestsOpened, ["dur"] = m.DurationSeconds, ["place"] = m.Placement,
                ["skin"] = m.Skin, ["mates"] = m.Mates?.Select(x => x.EpicId.ToLowerInvariant()).ToList(),
            }).ToList(),
        };
        _friendsBusy = true;
        _ = Task.Run(async () =>
        {
            var result = await FriendsClient.CallAsync("sync", me, body);
            Post(() =>
            {
                _friendsBusy = false;
                if (result.State is { } state)
                {
                    _sentSummary = summary;
                    foreach (var m in recent)
                    {
                        _sentMatches.Add(m.SessionId);
                    }
                    _friendsFailures = 0;
                    if (_live?.For != me.Id)
                    {
                        // registered now: the server can ring this player
                        _live?.Dispose();
                        _live = new FriendsLive(me, () => Post(() => _nextFriendsSync = DateTime.UtcNow));
                    }
                    ApplyFriends(state);
                }
                else
                {
                    FriendsFailed(result.Error);
                }
                _nextFriendsSync = DateTime.UtcNow.AddSeconds(NextFriendsDelay());
            });
        });
    }

    private int NextFriendsDelay()
    {
        if (_friendsStatus is "offline")
        {
            return Math.Min(300, 20 * (1 << Math.Min(4, _friendsFailures))); // 40 s … 5 min
        }
        if (_friendsStatus is "taken")
        {
            return 300;
        }
        if (_live?.Connected == true)
        {
            return 300; // the server rings when something changed; the player's own changes are sent at once
        }
        return InSquad || HasPendingInvites || _tracker.IsLive ? 10 : 60;
    }

    private void FriendsFailed(string? error)
    {
        _friendsFailures++;
        var status = error == "taken" ? "taken" : "offline";
        if (status != _friendsStatus && status == "taken")
        {
            Activity.Warning(L.T("Amis : ce compte Epic est déjà lié à un autre PC. Colle la clé de l'autre PC dans l'onglet Amis pour lier celui-ci.",
                "Friends: this Epic account is already linked to another PC. Paste the other PC's key in the Friends tab to link this one.",
                "Amigos: esta cuenta de Epic ya está vinculada a otro PC. Pega la clave del otro PC en la pestaña Amigos para vincular este."));
        }
        _friendsStatus = status;
    }

    /// <summary>New state from the server: notifications for what changed, then the squad overlay.</summary>
    private void ApplyFriends(JsonElement state)
    {
        var before = _friends;
        _friends = state;
        _friendsStatus = "ok";

        var incomingBefore = Ids(before, "incoming");
        foreach (var p in Items(state, "incoming").Where(p => !incomingBefore.Contains(Str(p, "id") ?? "")))
        {
            var name = Str(p, "name") ?? "?";
            Activity.Info(L.T($"{name} veut être ton ami (onglet Amis).", $"{name} wants to be your friend (Friends tab).", $"{name} quiere ser tu amigo (pestaña Amigos)."));
            NativeTray.Notify(L.T("Demande d'ami", "Friend request", "Solicitud de amistad"),
                L.T($"{name} veut voir tes coffres et te montrer les siens.", $"{name} wants to see your chests and show you theirs.", $"{name} quiere ver tus cofres y enseñarte los suyos."));
        }
        var outgoingBefore = Ids(before, "outgoing");
        var friendsNow = Ids(state, "friends");
        if (before != null)
        {
            foreach (var p in Items(before, "outgoing").Where(p => friendsNow.Contains(Str(p, "id") ?? "")))
            {
                var name = Str(p, "name") ?? "?";
                Activity.Success(L.T($"{name} a accepté : vous êtes amis !", $"{name} accepted: you are friends!", $"{name} aceptó: ¡sois amigos!"));
            }
        }
        var invitesBefore = Items(before, "invites").Select(i => Str(i, "squad")).ToHashSet();
        foreach (var inv in Items(state, "invites").Where(i => !invitesBefore.Contains(Str(i, "squad"))))
        {
            var from = inv.TryGetProperty("from", out var f) ? Str(f, "name") ?? "?" : "?";
            Activity.Success(L.T($"{from} t'invite dans son escouade (onglet Amis).", $"{from} invites you to their squad (Friends tab).", $"{from} te invita a su escuadrón (pestaña Amigos)."));
            NativeTray.Notify(L.T("Invitation d'escouade", "Squad invite", "Invitación de escuadrón"),
                L.T($"{from} t'invite : vous verrez les coffres de chacun après chaque partie.", $"{from} invites you: you will see each other's chests after every match.", $"{from} te invita: veréis los cofres de cada uno después de cada partida."));
        }

        // Squad: formed, members' results
        var squadBefore = SquadOf(before);
        var squadNow = SquadOf(state);
        if (squadNow is { } sq)
        {
            var id = Str(sq, "id");
            var members = Items(sq, "members").ToList();
            if (squadBefore is not { } sb || Str(sb, "id") != id)
            {
                if (members.Count > 1)
                {
                    var names = string.Join(", ", members.Where(m => Str(m, "id") != _syncedFor).Select(m => Str(m, "name")));
                    Activity.Success(L.T($"Mode escouade avec {names} : vos coffres s'affichent après chaque partie.", $"Squad mode with {names}: your chests show up after every match.", $"Modo escuadrón con {names}: vuestros cofres aparecen después de cada partida."));
                }
            }
            else
            {
                var old = Items(sb, "members").ToDictionary(m => Str(m, "id") ?? "", m => LastSid(m));
                foreach (var m in members.Where(m => Str(m, "id") != _syncedFor))
                {
                    var mid = Str(m, "id") ?? "";
                    if (old.TryGetValue(mid, out var oldSid) && LastSid(m) is { } sid && sid != oldSid && m.TryGetProperty("last", out var last))
                    {
                        var chests = last.TryGetProperty("chests", out var c) && c.TryGetInt32(out var n) ? n : 0;
                        Activity.Info(L.T($"{Str(m, "name")} : {L.Count(chests, "coffre", "chest", "cofre")} dans sa partie.", $"{Str(m, "name")}: {L.Count(chests, "coffre", "chest", "cofre")} in their match.", $"{Str(m, "name")}: {L.Count(chests, "coffre", "chest", "cofre")} en su partida."));
                    }
                }
            }
        }
        if (squadBefore is { } ended && (squadNow == null || Str(squadNow.Value, "id") != Str(ended, "id")))
        {
            SquadRecap(ended); // the squad closed (left, timed out): its recap, if not given yet
        }
        AnnounceFriendRewards(before, state);
        CheckSquadSuggestion();
        UpdateSquadOverlay();
    }

    private static JsonElement? SquadOf(JsonElement? state) =>
        state is { } s && s.TryGetProperty("squad", out var sq) && sq.ValueKind == JsonValueKind.Object ? sq : null;

    private static string? LastSid(JsonElement member) =>
        member.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.Object ? Str(l, "sid") : null;

    private static IEnumerable<JsonElement> Items(JsonElement? state, string name) =>
        state is { } s && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static HashSet<string> Ids(JsonElement? state, string name) => Items(state, name).Select(p => Str(p, "id") ?? "").ToHashSet();

    // ---- After a match: "you played with X, turn on squad mode?" ---------------------------------------------

    /// <summary>Called after new matches were counted.</summary>
    private void OnMatchesAdded()
    {
        _nextFriendsSync = DateTime.UtcNow;
        CheckSquadSuggestion();
    }

    private void CheckSquadSuggestion()
    {
        if (_friends is not { } state)
        {
            return;
        }
        var view = Profiles.View(_data, _config);
        var last = view.Matches.OrderByDescending(EndOf).FirstOrDefault();
        if (last?.Mates == null || EndOf(last) < DateTime.UtcNow.AddMinutes(-20) || _dismissedSuggestions.Contains(last.SessionId))
        {
            _squadSuggestion = null;
            return;
        }
        var inSquad = SquadOf(state) is { } sq ? Items(sq, "members").Concat(Items(sq, "invited")).Select(m => Str(m, "id") ?? "").ToHashSet() : new HashSet<string>();
        var friends = Items(state, "friends").ToDictionary(f => Str(f, "id") ?? "", f => Str(f, "name") ?? "?");
        var played = last.Mates.Select(m => m.EpicId.ToLowerInvariant()).Where(id => friends.ContainsKey(id) && !inSquad.Contains(id))
            .Select(id => (id, friends[id])).ToList();
        if (played.Count == 0)
        {
            _squadSuggestion = null;
            return;
        }
        if (_squadSuggestion?.Sid != last.SessionId)
        {
            var names = string.Join(", ", played.Select(p => p.Item2));
            NativeTray.Notify(L.T("Mode escouade ?", "Squad mode?", "¿Modo escuadrón?"),
                L.T($"Tu viens de jouer avec {names} : active le mode escouade dans l'app pour voir vos coffres ensemble.",
                    $"You just played with {names}: turn on squad mode in the app to see your chests together.",
                    $"Acabas de jugar con {names}: activa el modo escuadrón en la app para ver vuestros cofres juntos."));
        }
        _squadSuggestion = new SquadSuggestion(last.SessionId, Ms(Hunt.StartOf(last)), EndOf(last).AddMinutes(20), played);
    }

    // ---- Squad overlay ---------------------------------------------------------------------------------------

    private void UpdateSquadOverlay()
    {
        var squad = _config.FriendsSharing ? SquadOf(_friends) : null;
        var members = squad is { } sq ? Items(sq, "members").ToList() : new List<JsonElement>();
        var treasures = RecentRewards();
        SquadView? built = null;
        if (squad != null && members.Count >= 2)
        {
            var p = _config.SquadOverlayPeriod == "session" ? "session" : "last";
            built = new SquadView(p, members.Select(m => SquadMemberOf(m, p) with { Treasure = treasures.Contains(Str(m, "id") ?? "") })
                .OrderByDescending(m => m.Chests ?? -1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList());
        }
        ObsPage.WriteSquad(_store, built ?? SquadView.Empty); // OBS shows the squad even when the game overlay is off
        if (built == null || !_config.ShowSquadOverlay)
        {
            if (SquadOverlay.IsRunning && SquadOverlay.IsVisible)
            {
                SquadOverlay.SetVisible(false, quiet: true);
            }
            _squadOverlayFor = null;
            SquadOverlay.Update(SquadView.Empty);
            return;
        }
        var view = built;
        var squadId = Str(squad!.Value, "id");
        if (!SquadOverlay.IsRunning)
        {
            SquadOverlay.SetAppearance(_config.SquadOverlaySize, _config.SquadOverlayOpacity);
            SquadOverlay.Update(view);
            SquadOverlay.Start(_config.SquadOverlayX ?? DefaultSquadX(), _config.SquadOverlayY, _config.SquadOverlayLocked,
                (x, y) => Post(() => { _config.SquadOverlayX = x; _config.SquadOverlayY = y; SaveConfig(); }),
                locked => Post(() => { _config.SquadOverlayLocked = locked; SaveConfig(); }));
            _squadOverlayFor = squadId;
            return;
        }
        if (_squadOverlayFor != squadId)
        {
            // a new squad: shown again (even if it was hidden with a right click during the previous one)
            _squadOverlayFor = squadId;
            SquadOverlay.SetVisible(true, quiet: true);
        }
        SquadOverlay.Update(view);
    }

    /// <summary>A member of the squad as the overlay shows them, for "last" match or "session".</summary>
    private SquadMember SquadMemberOf(JsonElement m, string period)
    {
        var me = Str(m, "id") == _syncedFor;
        int? chests = period == "session"
            ? (m.TryGetProperty("sessionMatches", out var sm) && sm.TryGetInt32(out var smn) && smn > 0 && m.TryGetProperty("sessionChests", out var sc) && sc.TryGetInt32(out var scn) ? scn : null)
            : (m.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty("chests", out var lc) && lc.TryGetInt32(out var lcn) ? lcn : null);
        var playing = me ? _tracker.IsLive : m.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;
        var skin = Str(m, "skin");
        SkinCache.Want(skin);
        return new SquadMember(Str(m, "name") ?? "?", skin, chests, playing, me);
    }

    /// <summary>Golden chests (weekly rewards) of a friend's summary: (name, when in ms).</summary>
    private static IEnumerable<(string Name, long At)> Rewards(JsonElement friend) =>
        friend.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("treasures", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => x.TryGetProperty("g", out var g) && g.ValueKind == JsonValueKind.True && x.TryGetProperty("t", out var at) && at.TryGetInt64(out _))
                .Select(x => (Str(x, "n") ?? "?", x.GetProperty("t").GetInt64()))
            : Enumerable.Empty<(string, long)>();

    /// <summary>Players (Epic ids) who got a weekly reward in the last 15 minutes: the player and their friends.</summary>
    private HashSet<string> RecentRewards()
    {
        var since = Ms(DateTime.UtcNow.AddMinutes(-15));
        var ids = Items(_friends, "friends").Where(f => Rewards(f).Any(r => r.At >= since)).Select(f => Str(f, "id") ?? "").ToHashSet();
        var mine = Profiles.View(_data, _config).Matches
            .SelectMany(m => m.TreasureFinds.Where(f => f.FromGoldenChest).Select(f => Ms(Hunt.StartOf(m).AddSeconds(f.ObtainedAtSeconds ?? 0))));
        if (_syncedFor != null && mine.Any(at => at >= since))
        {
            ids.Add(_syncedFor);
        }
        return ids;
    }

    /// <summary>A friend got a weekly reward since the previous state: notification (only for recent ones, never at start).</summary>
    private void AnnounceFriendRewards(JsonElement? before, JsonElement now)
    {
        if (before == null)
        {
            return;
        }
        var known = Items(before, "friends").ToDictionary(f => Str(f, "id") ?? "", f => Rewards(f).Select(r => r.At).DefaultIfEmpty(0).Max());
        var recent = Ms(DateTime.UtcNow.AddHours(-2));
        foreach (var f in Items(now, "friends"))
        {
            if (!known.TryGetValue(Str(f, "id") ?? "", out var last))
            {
                continue; // a new friend: their old treasures are no news
            }
            foreach (var (prize, at) in Rewards(f).Where(r => r.At > last && r.At >= recent))
            {
                var name = Str(f, "name") ?? "?";
                Activity.Treasure(L.T($"★★ {name} a trouvé un trésor : {prize} !", $"★★ {name} found a treasure: {prize}!", $"★★ ¡{name} encontró un tesoro: {prize}!"));
                NativeTray.Notify(L.T($"{name} a trouvé un trésor !", $"{name} found a treasure!", $"¡{name} encontró un tesoro!"), prize);
            }
        }
    }

    // ---- End of the evening: who opened the most chests in the squad ------------------------------------------

    private string? _recapDoneFor;            // squad id whose recap was given
    private bool _fortniteWasRunning;
    private DateTime _nextFortniteCheck;

    /// <summary>When Fortnite closes during a squad: the ranking of the session, once per squad.</summary>
    private void CheckSessionEnd()
    {
        if (DateTime.UtcNow < _nextFortniteCheck)
        {
            return;
        }
        _nextFortniteCheck = DateTime.UtcNow.AddSeconds(20);
        var running = FortniteWatch.IsFortniteRunning();
        if (_fortniteWasRunning && !running && SquadOf(_friends) is { } squad)
        {
            SquadRecap(squad);
        }
        _fortniteWasRunning = running;
    }

    private void SquadRecap(JsonElement squad)
    {
        var id = Str(squad, "id");
        if (id == null || id == _recapDoneFor)
        {
            return;
        }
        var ranking = Items(squad, "members")
            .Select(m => (Name: Str(m, "id") == _syncedFor ? L.T("toi", "you", "tú") : Str(m, "name") ?? "?",
                Chests: m.TryGetProperty("sessionChests", out var c) && c.TryGetInt32(out var n) ? n : 0,
                Matches: m.TryGetProperty("sessionMatches", out var sm) && sm.TryGetInt32(out var smn) ? smn : 0))
            .Where(m => m.Matches > 0).OrderByDescending(m => m.Chests).ToList();
        if (ranking.Count < 2)
        {
            return; // nobody to compare with
        }
        _recapDoneFor = id;
        var line = string.Join(" · ", ranking.Select((m, i) => $"{i + 1}. {m.Name} {L.N(m.Chests)}"));
        var winner = ranking[0];
        var title = L.T($"Bilan : {winner.Name} gagne la session !", $"Recap: {winner.Name} wins the session!", $"Resumen: ¡{winner.Name} gana la sesión!");
        Activity.Success($"{title} {line}");
        NativeTray.Notify(title, L.T($"Coffres de la session : {line}", $"Session chests: {line}", $"Cofres de la sesión: {line}"));
    }

    /// <summary>First time: to the right of the main overlay's usual place, so they do not overlap.</summary>
    private int? DefaultSquadX() => _config.OverlayX is { } x ? x + 420 : 460;

    // ---- Commands from the page -------------------------------------------------------------------------------

    private static bool IsFriendsNetworkCommand(string name) =>
        name is "friends/add" or "friends/answer" or "friends/remove" or "friends/sync" or "friends/link"
            or "squad/invite" or "squad/answer" or "squad/leave" or "squad/suggest" or "squad/join-latest";

    /// <summary>Runs a command that needs the server: the identity is read on the main loop, the call is awaited here.</summary>
    private async Task<CommandResult> FriendsCommandAsync(string name, JsonElement args)
    {
        var prepared = new TaskCompletionSource<(FriendsClient.Identity? Me, List<(string Op, Dictionary<string, object?> Body)> Calls, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                if (name == "friends/link")
                {
                    var key = Str(args, "key")?.Trim().ToLowerInvariant();
                    if (!FriendsClient.IsKey(key))
                    {
                        prepared.SetResult((null, new(), L.T("clé invalide (64 caractères 0-9 a-f)", "invalid key (64 characters 0-9 a-f)", "clave no válida (64 caracteres 0-9 a-f)")));
                        return;
                    }
                    if (Profiles.Current(_data, _config)?.Key is { } pid && FriendsClient.IsEpicId(pid))
                    {
                        _config.FriendKeys[pid.ToLowerInvariant()] = key!;
                        SaveConfig();
                        _friendsStatus = "connecting";
                    }
                }
                var me = FriendIdentity();
                if (me == null)
                {
                    prepared.SetResult((null, new(), _config.FriendsSharing
                        ? L.T("joue d'abord une partie : l'app doit lire ton compte Epic dans un replay", "play a match first: the app must read your Epic account in a replay", "juega primero una partida: la app debe leer tu cuenta de Epic en una repetición")
                        : L.T("le partage avec tes amis est coupé", "sharing with your friends is off", "compartir con tus amigos está desactivado")));
                    return;
                }
                var calls = new List<(string, Dictionary<string, object?>)>();
                switch (name)
                {
                    case "friends/add":
                        calls.Add(("friend/add", Str(args, "target") is { } t ? new() { ["target"] = t } : new() { ["name"] = Str(args, "name")?.Trim() }));
                        break;
                    case "friends/answer":
                        calls.Add(("friend/answer", new() { ["target"] = Str(args, "target"), ["accept"] = Bool(args, "accept") == true }));
                        break;
                    case "friends/remove":
                        calls.Add(("friend/remove", new() { ["target"] = Str(args, "target") }));
                        break;
                    case "squad/invite":
                        calls.Add(("squad/invite", new() { ["target"] = Str(args, "target") }));
                        break;
                    case "squad/answer":
                        calls.Add(("squad/answer", new() { ["squad"] = Str(args, "squad"), ["accept"] = Bool(args, "accept") == true }));
                        break;
                    case "squad/join-latest":
                        if (Items(_friends, "invites").FirstOrDefault() is { ValueKind: JsonValueKind.Object } latest)
                        {
                            calls.Add(("squad/answer", new() { ["squad"] = Str(latest, "squad"), ["accept"] = true }));
                        }
                        break;
                    case "squad/leave":
                        calls.Add(("squad/leave", new()));
                        break;
                    case "squad/suggest":
                        if (_squadSuggestion is { } sg)
                        {
                            _dismissedSuggestions.Add(sg.Sid);
                            foreach (var (fid, _) in sg.Friends)
                            {
                                calls.Add(("squad/invite", new() { ["target"] = fid, ["since"] = sg.Since }));
                            }
                            _squadSuggestion = null;
                        }
                        break;
                    default: // friends/sync, friends/link
                        calls.Add(("sync", new() { ["playing"] = _tracker.IsLive }));
                        break;
                }
                prepared.SetResult((me, calls, null));
            }
            catch (Exception ex)
            {
                prepared.SetResult((null, new(), ex.Message));
            }
        });
        var (who, todo, failure) = await prepared.Task.WaitAsync(TimeSpan.FromSeconds(20));
        if (failure != null || who == null)
        {
            return CommandResult.Fail(failure ?? "?");
        }
        FriendsClient.Result? result = null;
        foreach (var (op, body) in todo)
        {
            result = await FriendsClient.CallAsync(op, who, body);
            if (result.State == null)
            {
                break;
            }
        }
        var done = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            var outcome = CommandResult.Done;
            if (result?.State is { } state)
            {
                ApplyFriends(state);
                _nextFriendsSync = DateTime.UtcNow.AddSeconds(NextFriendsDelay());
            }
            else if (result != null)
            {
                if (result.Error is "taken" or "offline")
                {
                    FriendsFailed(result.Error);
                }
                outcome = CommandResult.Fail(FriendsClient.Explain(result.Error));
            }
            Publish(); // the page gets the new state before the answer
            done.SetResult(outcome);
        });
        return await done.Task;
    }

    /// <summary>Settings of the Friends tab (main loop).</summary>
    private CommandResult FriendsSettings(JsonElement args)
    {
        if (Bool(args, "sharing") is { } sharing && sharing != _config.FriendsSharing)
        {
            _config.FriendsSharing = sharing;
            _config.FriendsNoticeSeen = true;
            if (!sharing)
            {
                // Turned off: everything the server knows about the player is deleted.
                var me = Profiles.Current(_data, _config) is { } p && FriendsClient.IsEpicId(p.Key) && _config.FriendKeys.TryGetValue(p.Key.ToLowerInvariant(), out var key)
                    ? new FriendsClient.Identity(p.Key.ToLowerInvariant(), key, p.Name, null) : null;
                if (me != null)
                {
                    _ = Task.Run(async () =>
                    {
                        var r = await FriendsClient.CallAsync("forget", me);
                        Activity.Muted(r.Error == null
                            ? L.T("Partage coupé : tes infos ont été effacées du serveur.", "Sharing off: your info was deleted from the server.", "Compartir desactivado: tu información se borró del servidor.")
                            : L.T("Partage coupé (le serveur n'a pas pu être prévenu, il oubliera tes infos sous 3 jours).", "Sharing off (the server could not be told, it forgets your info within 3 days).", "Compartir desactivado (no se pudo avisar al servidor, olvidará tu información en 3 días)."));
                    });
                }
                _friends = null;
                _friendsStatus = "off";
                _live?.Dispose();
                _live = null;
                _squadSuggestion = null;
                _sentMatches.Clear();
                _sentSummary = null;
            }
            else
            {
                _friendsStatus = "connecting";
                _nextFriendsSync = DateTime.UtcNow;
            }
            UpdateSquadOverlay();
        }
        if (Bool(args, "overlay") is { } overlay)
        {
            _config.ShowSquadOverlay = overlay;
            if (overlay)
            {
                _squadOverlayFor = null; // shown again
                if (SquadOverlay.IsRunning && InSquad)
                {
                    SquadOverlay.SetVisible(true, quiet: true);
                }
            }
            UpdateSquadOverlay();
        }
        if (Bool(args, "overlayLocked") is { } locked && SquadOverlay.IsRunning)
        {
            SquadOverlay.SetLocked(locked);
        }
        if (Bool(args, "overlayVisible") is { } visible && SquadOverlay.IsRunning)
        {
            SquadOverlay.SetVisible(visible);
        }
        if (Str(args, "period") is "last" or "session" && Str(args, "period") is { } period)
        {
            _config.SquadOverlayPeriod = period;
            UpdateSquadOverlay();
        }
        if (Int(args, "overlayCorner") is { } corner and >= 0 and <= 3)
        {
            SquadOverlay.SnapTo(corner);
        }
        if (Int(args, "overlaySize") is { } size)
        {
            _config.SquadOverlaySize = OverlayAppearance.ClampSize(size);
            SquadOverlay.SetAppearance(_config.SquadOverlaySize, _config.SquadOverlayOpacity);
        }
        if (Int(args, "overlayOpacity") is { } opacity)
        {
            _config.SquadOverlayOpacity = OverlayAppearance.ClampOpacity(opacity);
            SquadOverlay.SetAppearance(_config.SquadOverlaySize, _config.SquadOverlayOpacity);
        }
        if (Bool(args, "noticeSeen") == true)
        {
            _config.FriendsNoticeSeen = true;
        }
        SaveConfig();
        return CommandResult.Done;
    }

    // ---- State for the page -----------------------------------------------------------------------------------

    private UiFriends FriendsState()
    {
        var me = _config.FriendsSharing ? FriendIdentity() : null;
        var view = Profiles.View(_data, _config);
        var status = !_config.FriendsSharing ? "off" : me == null ? "noprofile" : _friends == null && _friendsStatus == "ok" ? "connecting" : _friendsStatus;
        var squadView = SquadOverlay.View;
        _squadDemo = SquadDemo(me);
        return new UiFriends(_config.FriendsSharing, status, me?.Id, me?.Name, me?.Skin, _friends,
            me != null ? FriendsSummary(view) : null,
            _squadSuggestion is { } s && s.Until > DateTime.UtcNow ? new UiSquadSuggestion(s.Sid, s.Friends.Select(f => f.Name).ToList()) : null,
            new UiSquadOverlay(_config.ShowSquadOverlay, SquadOverlay.IsRunning, SquadOverlay.IsVisible,
                SquadOverlay.IsRunning ? SquadOverlay.IsLocked : _config.SquadOverlayLocked, _config.SquadOverlayPeriod,
                Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(squadView.Key)), 0, 6),
                Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(_squadDemo.Key)), 0, 6))
            {
                Size = _config.SquadOverlaySize,
                Opacity = _config.SquadOverlayOpacity,
                ObsPath = _store.SquadOverlayPath,
            },
            SkinCache.Version, me?.Key, FriendsClient.BaseUrl);
    }

    private void FriendsNotices(List<UiNotice> list)
    {
        if (!_config.FriendsNoticeSeen && _config.FriendsSharing)
        {
            list.Add(new UiNotice("friends-new", "info",
                L.T("Nouveau : tes coffres sont partagés avec tes amis qui ont l'app (pseudo Epic, skin, coffres, chances de trésor). Ajoute-les dans l'onglet Amis, ou coupe le partage là-bas.",
                    "New: your chests are shared with your friends who have the app (Epic name, outfit, chests, treasure odds). Add them in the Friends tab, or turn sharing off there.",
                    "Nuevo: tus cofres se comparten con tus amigos que tienen la app (nombre de Epic, skin, cofres, probabilidades de tesoro). Añádelos en la pestaña Amigos, o desactiva el uso compartido allí."),
                L.T("OK", "OK", "OK"), "friends/notice-ok"));
        }
        if (_squadSuggestion is { } s && s.Until > DateTime.UtcNow)
        {
            var names = string.Join(", ", s.Friends.Select(f => f.Name));
            list.Add(new UiNotice("squad-suggest", "success",
                L.T($"Tu viens de jouer avec {names} : active le mode escouade pour voir vos coffres ensemble (overlay et onglet Amis).",
                    $"You just played with {names}: turn on squad mode to see your chests together (overlay and Friends tab).",
                    $"Acabas de jugar con {names}: activa el modo escuadrón para ver vuestros cofres juntos (overlay y pestaña Amigos)."),
                L.T("Activer", "Turn on", "Activar"), "squad/suggest"));
        }
        if (Items(_friends, "invites").FirstOrDefault() is { ValueKind: JsonValueKind.Object } inv)
        {
            var from = inv.TryGetProperty("from", out var f) ? Str(f, "name") ?? "?" : "?";
            list.Add(new UiNotice("squad-invite", "success",
                L.T($"{from} t'invite dans son escouade : vous verrez les coffres de chacun après chaque partie.",
                    $"{from} invites you to their squad: you will see each other's chests after every match.",
                    $"{from} te invita a su escuadrón: veréis los cofres de cada uno después de cada partida."),
                L.T("Rejoindre", "Join", "Unirse"), "squad/join-latest"));
        }
        var incoming = Items(_friends, "incoming").ToList();
        if (incoming.Count > 0)
        {
            var names = string.Join(", ", incoming.Take(3).Select(p => Str(p, "name")));
            list.Add(new UiNotice("friend-requests", "info",
                L.T($"Demande d'ami : {names}.", $"Friend request: {names}.", $"Solicitud de amistad: {names}."),
                L.T("Voir", "See", "Ver"), "ui:friends"));
        }
    }
}
