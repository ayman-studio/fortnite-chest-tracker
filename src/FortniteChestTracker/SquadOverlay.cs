using System.Runtime.Versioning;

namespace FortniteChestTracker;

/// <param name="Name">Epic name.</param>
/// <param name="Skin">Outfit id (icon from <see cref="SkinCache"/>), null = placeholder.</param>
/// <param name="Chests">Chests of the period, null = no match yet.</param>
/// <param name="Playing">In a match right now: their chests arrive when it ends.</param>
/// <param name="Me">The player of this PC.</param>
/// <param name="Fresh">Their number just changed (glow animation).</param>
public sealed record SquadMember(string Name, string? Skin, int? Chests, bool Playing, bool Me, bool Fresh = false)
{
    /// <summary>Got a weekly reward (golden chest) in the last 15 minutes: "TREASURE!" on their tile.</summary>
    public bool Treasure { get; init; }
}

/// <summary>What the squad overlay shows: each member's outfit above their chests of the last match or of the session.</summary>
/// <param name="Period">"last" (last match) or "session" (since the squad was formed).</param>
public sealed record SquadView(string Period, IReadOnlyList<SquadMember> Members)
{
    public static SquadView Empty { get; } = new("last", Array.Empty<SquadMember>());

    /// <summary>Changes whenever something drawn changes (picture cache of the page).</summary>
    public string Key => Period + "|" + L.Code + "|" + string.Join(";", Members.Select(m => $"{m.Name}/{m.Skin}/{m.Chests}/{m.Playing}/{m.Me}/{m.Treasure}/{SkinCache.Image(m.Skin) != null}"));
}

/// <summary>
/// The squad overlay: a second floating HUD (<see cref="HudWindow"/>) next to the main one, with the outfit of each
/// member of the squad above their chest count. Shown only while the player is in a squad (Friends tab).
/// </summary>
[SupportedOSPlatform("windows")]
public static class SquadOverlay
{
    private static volatile SquadView _view = SquadView.Empty;
    private static readonly HudWindow Window = new("FortniteChestTrackerSquad", L.T("Escouade", "Squad", "Escuadrón"), hotkeys: false,
        L.T("overlay d'escouade", "squad overlay", "overlay de escuadrón"),
        (renderer, scale, edit, minWidth, pop, popCount) => renderer.RenderSquad(_view, scale, edit, minWidth, pop));

    static SquadOverlay() => Window.StateChanged += () => StateChanged?.Invoke();

    public static SquadView View => _view;

    public static void SetAppearance(int sizePercent, int opacityPercent) => Window.SetAppearance(sizePercent, opacityPercent);

    public static bool IsRunning => Window.IsRunning;

    public static bool IsLocked => Window.IsLocked;

    public static bool IsVisible => Window.IsVisible;

    /// <summary>Raised (on the overlay thread) when it is shown, hidden, locked or unlocked, whatever the cause.</summary>
    public static event Action? StateChanged;

    public static void SetVisible(bool visible, bool quiet = false) => Window.SetVisible(visible, quiet);

    public static void SetLocked(bool locked) => Window.SetLocked(locked);

    public static void SnapTo(int corner) => Window.SnapTo(corner);

    public static void Start(int? x, int? y, bool locked, Action<int, int> onMoved, Action<bool> onLockChanged) =>
        Window.Start(x, y, locked, onMoved, onLockChanged);

    /// <summary>Replaces what is shown; members whose chests went up glow for a moment.</summary>
    public static void Update(SquadView view)
    {
        var old = _view;
        var gained = 0;
        var members = view.Members.Select(m =>
        {
            var before = old.Members.FirstOrDefault(o => o.Name == m.Name);
            var fresh = old.Period == view.Period && before != null && m.Chests is { } now && now > (before.Chests ?? 0) && !ReferenceEquals(old, SquadView.Empty);
            if (fresh)
            {
                gained += m.Chests!.Value - (before!.Chests ?? 0);
            }
            return m with { Fresh = fresh };
        }).ToList();
        _view = view with { Members = members };
        Window.Relayout(gained, resetWidth: members.Count != old.Members.Count || view.Period != old.Period);
    }
}
