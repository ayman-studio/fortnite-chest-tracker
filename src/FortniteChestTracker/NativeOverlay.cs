using System.Runtime.Versioning;

namespace FortniteChestTracker;

/// <summary>
/// The in-game overlay: the player's floating HUD (see <see cref="HudRenderer"/>) in a <see cref="HudWindow"/>.
/// Right-click hides it, Ctrl+Shift+O shows/hides it, Ctrl+Shift+L locks/unlocks it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeOverlay
{
    private static volatile HudView _view = HudView.Empty;
    private static readonly HudWindow Window = new("FortniteChestTrackerHud", L.T("Coffres", "Chests", "Cofres"), hotkeys: true, "overlay",
        (renderer, scale, edit, minWidth, pop, popCount) => renderer.Render(_view, scale, edit, minWidth, pop, popCount));

    static NativeOverlay() => Window.StateChanged += () => StateChanged?.Invoke();

    /// <summary>Size and opacity of the overlay (percent). Applied at once when the overlay is shown.</summary>
    public static void SetAppearance(int sizePercent, int opacityPercent) => Window.SetAppearance(sizePercent, opacityPercent);

    public static bool IsRunning => Window.IsRunning;

    /// <summary>True when the global Ctrl+Shift+O shortcut could be registered.</summary>
    public static bool HotkeyRegistered => Window.HotkeyRegistered;

    /// <summary>True when the global Ctrl+Shift+L shortcut (lock/unlock) could be registered.</summary>
    public static bool LockHotkeyRegistered => Window.LockHotkeyRegistered;

    /// <summary>Locked = click-through: the mouse goes through the overlay and it cannot be moved.</summary>
    public static bool IsLocked => Window.IsLocked;

    /// <summary>Shown on screen (not hidden with a right click or the shortcut).</summary>
    public static bool IsVisible => Window.IsVisible;

    /// <summary>Raised (on the overlay thread) when it is shown, hidden, locked or unlocked, whatever the cause.</summary>
    public static event Action? StateChanged;

    /// <summary>Shows or hides the overlay (no change when it already is).</summary>
    public static void SetVisible(bool visible) => Window.SetVisible(visible);

    /// <summary>Locks or unlocks the overlay (no change when it already is).</summary>
    public static void SetLocked(bool locked) => Window.SetLocked(locked);

    /// <summary>Moves the overlay to a corner of its screen: 0 top left, 1 top right, 2 bottom left, 3 bottom right.</summary>
    public static void SnapTo(int corner) => Window.SnapTo(corner);

    public static void Toggle() => Window.Toggle();

    public static void Start(int? x, int? y, bool locked, Action<int, int> onMoved, Action<bool> onLockChanged) =>
        Window.Start(x, y, locked, onMoved, onLockChanged);

    /// <summary>Replaces what is shown (with the "+1" animation when the match in progress gained chests).</summary>
    public static void Update(HudView view)
    {
        var old = _view;
        _view = view;
        var pop = 0;
        if (view.Mode == HudMode.Live && old.Mode == HudMode.Live && view.Chests > old.Chests)
        {
            pop = view.Chests.Value - (old.Chests ?? 0);
        }
        else if (!ReferenceEquals(old, HudView.Empty) && view.Mode == HudMode.Idle && view.Matches == old.Matches + 1 && view.Chests > 0)
        {
            // Fortnite only lets the replay be read once the match is over: that is when its chests arrive, all at once.
            pop = view.Chests.Value;
        }
        Window.Relayout(pop, resetWidth: view.Compact != old.Compact); // the other layout has its own width
    }
}
