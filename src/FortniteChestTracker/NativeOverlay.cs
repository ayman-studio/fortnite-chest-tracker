using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FortniteChestTracker;

/// <summary>
/// The in-game overlay: a floating HUD (see <see cref="HudRenderer"/>) in an always-on-top, per-pixel transparent
/// window. Unlocked = edit mode (dashed frame, drag it anywhere, padlock to lock); locked = only the HUD, and the mouse
/// goes through it. Right-click hides it, Ctrl+Shift+O shows/hides it, Ctrl+Shift+L locks/unlocks it.
/// Visible over Fortnite when the game runs in "windowed fullscreen" (not exclusive fullscreen).
/// No timer while nothing changes: the thread sleeps until the counter changes (a short animation after a chest).
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeOverlay
{
    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static volatile HudView _view = HudView.Empty;
    private static HudRenderer? _renderer; // overlay thread only
    private static HudFrame? _frame;       // last frame shown (overlay thread)
    // Kept between frames (re-created only when the size changes): memory DC + DIB section + byte buffer.
    private static IntPtr _memDc, _dib, _dibBits;
    private static int _dibW, _dibH;
    private static byte[]? _bgra;
    private static Action<int, int>? _onMoved;
    private static Action<bool>? _onLockChanged;
    private static volatile bool _locked;
    private static bool _offscreen;
    private static int _stableW;
    private static volatile bool _resetWidth;
    private static WndProcDelegate? _wndProc; // keep the delegate alive
    private static float _scale = 1f;    // display scaling × size chosen by the player
    private static float _dpiScale = 1f; // display scaling only
    private static volatile int _sizePercent = 100;
    private static volatile int _opacityPercent = 100;

    // "+1" animation after a chest of the match in progress.
    private const int PopMs = 1400;
    private static int _popPending;
    private static int _popCount;
    private static long _popStart;

    /// <summary>Size and opacity of the overlay (percent). Applied at once when the overlay is shown.</summary>
    public static void SetAppearance(int sizePercent, int opacityPercent)
    {
        _sizePercent = OverlayAppearance.ClampSize(sizePercent);
        _opacityPercent = OverlayAppearance.ClampOpacity(opacityPercent);
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_APPEARANCE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static byte Alpha => (byte)Math.Round(_opacityPercent * 255 / 100.0);

    public static bool IsRunning => _hwnd != IntPtr.Zero;

    /// <summary>True when the global Ctrl+Shift+O shortcut could be registered.</summary>
    public static bool HotkeyRegistered { get; private set; }

    /// <summary>True when the global Ctrl+Shift+L shortcut (lock/unlock) could be registered.</summary>
    public static bool LockHotkeyRegistered { get; private set; }

    /// <summary>Locked = click-through: the mouse goes through the overlay and it cannot be moved.</summary>
    public static bool IsLocked => _locked;

    /// <summary>Shown on screen (not hidden with a right click or the shortcut).</summary>
    public static bool IsVisible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

    /// <summary>Raised (on the overlay thread) when it is shown, hidden, locked or unlocked, whatever the cause.</summary>
    public static event Action? StateChanged;

    /// <summary>Shows or hides the overlay (no change when it already is).</summary>
    public static void SetVisible(bool visible)
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_SHOW, (IntPtr)(visible ? 1 : 0), IntPtr.Zero);
        }
    }

    /// <summary>Locks or unlocks the overlay (no change when it already is).</summary>
    public static void SetLocked(bool locked)
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_LOCK, (IntPtr)(locked ? 1 : 2), IntPtr.Zero);
        }
    }

    /// <summary>Moves the overlay to a corner of its screen: 0 top left, 1 top right, 2 bottom left, 3 bottom right.</summary>
    public static void SnapTo(int corner)
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_CORNER, (IntPtr)corner, IntPtr.Zero);
        }
    }

    public static void Toggle()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void Start(int? x, int? y, bool locked, Action<int, int> onMoved, Action<bool> onLockChanged)
    {
        if (_thread != null)
        {
            return;
        }

        _onMoved = onMoved;
        _offscreen = x <= -10000;
        _onLockChanged = onLockChanged;
        _locked = locked;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                Run(x, y, ready);
            }
            catch (Exception ex)
            {
                Activity.Error(L.T($"Overlay indisponible : {ex.Message}", $"Overlay unavailable: {ex.Message}", $"Overlay no disponible: {ex.Message}"));
                _hwnd = IntPtr.Zero;
                _thread = null; // a later attempt can start it again
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Overlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Replaces what is shown (with the "+1" animation when the match in progress gained chests).</summary>
    public static void Update(HudView view)
    {
        var old = _view;
        _view = view;
        if (view.Compact != old.Compact)
        {
            _resetWidth = true; // the other layout has its own width
        }
        if (view.Mode == HudMode.Live && old.Mode == HudMode.Live && view.Chests > old.Chests)
        {
            Interlocked.Add(ref _popPending, view.Chests.Value - (old.Chests ?? 0));
        }
        else if (!ReferenceEquals(old, HudView.Empty) && view.Mode == HudMode.Idle && view.Matches == old.Matches + 1 && view.Chests > 0)
        {
            // Fortnite only lets the replay be read once the match is over: that is when its chests arrive, all at once.
            Interlocked.Add(ref _popPending, view.Chests.Value);
        }
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_RELAYOUT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void Run(int? x, int? y, ManualResetEventSlim ready)
    {
        try { SetProcessDPIAware(); } catch { /* old Windows */ }

        var screenDc = GetDC(IntPtr.Zero);
        _dpiScale = GetDeviceCaps(screenDc, LOGPIXELSY) / 96f;
        ReleaseDC(IntPtr.Zero, screenDc);
        _scale = _dpiScale * _sizePercent / 100f;
        _renderer = new HudRenderer();

        var hInstance = GetModuleHandleW(null);
        _wndProc = WndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_SIZEALL),
            lpszClassName = "FortniteChestTrackerHud",
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            throw new InvalidOperationException("RegisterClassEx failed");
        }

        _hwnd = CreateWindowExW(
            // Per-pixel transparent: only the HUD itself is drawn, the game shows around it.
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            wc.lpszClassName, L.T("Coffres", "Chests", "Cofres"), WS_POPUP,
            x ?? (int)(24 * _dpiScale), y ?? (int)(24 * _dpiScale), 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowEx failed");
        }

        ApplyLock(_hwnd, _locked, notify: false);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        // Global shortcuts, they work even when the game has the focus.
        HotkeyRegistered = RegisterHotKey(_hwnd, 1, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_O);
        LockHotkeyRegistered = RegisterHotKey(_hwnd, 2, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_L);
        ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                // Padlock = client area (clickable); everywhere else = caption (drag to move).
                // Locked, the window is click-through and never even receives this message.
                GetWindowRect(hwnd, out var wr);
                var px = (short)((long)lParam & 0xFFFF) - wr.Left;
                var py = (short)(((long)lParam >> 16) & 0xFFFF) - wr.Top;
                var b = _frame?.LockButton ?? default;
                var onButton = b.W > 0 && px >= b.X && px < b.X + b.W && py >= b.Y && py < b.Y + b.H;
                return (IntPtr)(onButton ? HTCLIENT : HTCAPTION);
            }

            case WM_SETCURSOR:
                if (((long)lParam & 0xFFFF) == HTCLIENT)
                {
                    SetCursor(LoadCursorW(IntPtr.Zero, IDC_HAND));
                    return (IntPtr)1;
                }
                break;

            case WM_LBUTTONUP:
                ApplyLock(hwnd, true, notify: true);
                return IntPtr.Zero;

            case WM_APP_LOCK:
            {
                // wParam: 0 = toggle, 1 = lock, 2 = unlock.
                var wanted = (long)wParam switch { 1 => true, 2 => false, _ => !_locked };
                if (wanted != _locked || (long)wParam == 0)
                {
                    ApplyLock(hwnd, wanted, notify: true);
                }
                return IntPtr.Zero;
            }

            case WM_APP_SHOW:
                if (((long)wParam != 0) != IsWindowVisible(hwnd))
                {
                    SetVisible(hwnd, (long)wParam != 0);
                }
                return IntPtr.Zero;

            case WM_NCRBUTTONUP:
                SetVisible(hwnd, false);
                return IntPtr.Zero;

            case WM_HOTKEY:
                if ((long)wParam == 2)
                {
                    ApplyLock(hwnd, !_locked, notify: true);
                }
                else
                {
                    SetVisible(hwnd, !IsWindowVisible(hwnd));
                }
                return IntPtr.Zero;

            case WM_EXITSIZEMOVE:
                if (GetWindowRect(hwnd, out var r))
                {
                    _onMoved?.Invoke(r.Left, r.Top);
                }
                return IntPtr.Zero;

            case WM_APP_TOGGLE:
                SetVisible(hwnd, !IsWindowVisible(hwnd));
                return IntPtr.Zero;

            case WM_APP_CORNER:
            {
                var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetWindowRect(hwnd, out var now) && GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info))
                {
                    var work = info.rcWork;
                    var gap = (int)(16 * _dpiScale);
                    int ww = now.Right - now.Left, hh = now.Bottom - now.Top;
                    var c = (long)wParam;
                    var x = c is 0 or 2 ? work.Left + gap : work.Right - ww - gap;
                    var y = c is 0 or 1 ? work.Top + gap : work.Bottom - hh - gap;
                    SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
                    _onMoved?.Invoke(x, y);
                    Present(hwnd);
                }
                return IntPtr.Zero;
            }

            case WM_APP_RELAYOUT:
            {
                var added = Interlocked.Exchange(ref _popPending, 0);
                if (added > 0 && IsWindowVisible(hwnd))
                {
                    _popCount = Animating ? _popCount + added : added;
                    _popStart = Environment.TickCount64;
                    SetTimer(hwnd, (UIntPtr)1, 33, IntPtr.Zero); // ~30 fps: smooth enough, light on the game
                }
                Present(hwnd);
                return IntPtr.Zero;
            }

            case WM_TIMER:
                if (!Animating)
                {
                    KillTimer(hwnd, (UIntPtr)1);
                    _popCount = 0;
                }
                Present(hwnd);
                return IntPtr.Zero;

            case WM_APP_APPEARANCE:
                _scale = _dpiScale * _sizePercent / 100f;
                _stableW = 0; // the width may shrink when the overlay gets smaller
                Present(hwnd);
                return IntPtr.Zero;

            case WM_DPICHANGED:
                Present(hwnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                UnregisterHotKey(hwnd, 1);
                UnregisterHotKey(hwnd, 2);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static bool Animating => _popCount > 0 && Environment.TickCount64 - _popStart < PopMs;

    /// <summary>Draws the HUD and puts it on screen (the window takes the size of the picture).</summary>
    private static void Present(IntPtr hwnd)
    {
        if (_renderer == null)
        {
            return;
        }
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0 && Math.Abs(dpi / 96f - _dpiScale) > 0.01f)
            {
                _dpiScale = dpi / 96f; // moved to a screen with another scaling
                _scale = _dpiScale * _sizePercent / 100f;
                _resetWidth = true;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Windows before 10 1607: the system scaling read at start stays
        }
        if (_resetWidth)
        {
            _resetWidth = false;
            _stableW = 0;
        }
        var pop = Animating ? 1 - (Environment.TickCount64 - _popStart) / (float)PopMs : 0;
        var frame = _renderer.Render(_view, _scale, edit: !_locked, minWidth: _stableW, pop: pop, popCount: pop > 0 ? _popCount : 0);
        // Never narrower than before: it keeps the same size between a match in progress and the end of the match.
        _stableW = Math.Max(_stableW, frame.Width);
        int w = frame.Width, h = frame.Height;

        // Keep it fully on its screen: on the right half of the screen it grows to the left, and it is pushed back
        // inside when it would overflow.
        GetWindowRect(hwnd, out var cur);
        int nx = cur.Left, ny = cur.Top;
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!_offscreen && GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
        {
            var work = mi.rcWork;
            if (cur.Right - cur.Left > 1 && (cur.Left + cur.Right) / 2 > (work.Left + work.Right) / 2)
            {
                nx = cur.Right - w; // anchored on the right edge
            }
            nx = Math.Max(work.Left, Math.Min(nx, work.Right - w));
            ny = Math.Max(work.Top, Math.Min(ny, work.Bottom - h));
        }

        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (_dib == IntPtr.Zero || _dibW != w || _dibH != h)
            {
                if (_dib != IntPtr.Zero) DeleteObject(_dib);
                if (_memDc == IntPtr.Zero) _memDc = CreateCompatibleDC(screen);
                var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                _dib = CreateDIBSection(_memDc, ref bi, 0, out _dibBits, IntPtr.Zero, 0);
                if (_dib == IntPtr.Zero || _dibBits == IntPtr.Zero)
                {
                    _dib = IntPtr.Zero;
                    return; // out of GDI resources: skip this frame rather than crash
                }
                SelectObject(_memDc, _dib);
                _dibW = w;
                _dibH = h;
            }
            _bgra = frame.ToBgraPremultiplied(_bgra);
            Marshal.Copy(_bgra, 0, _dibBits, w * h * 4);
            var pos = new POINT { X = nx, Y = ny };
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = Alpha, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(hwnd, screen, ref pos, ref size, _memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
        _frame = frame;
        if ((nx != cur.Left || ny != cur.Top) && cur.Right - cur.Left > 1)
        {
            _onMoved?.Invoke(nx, ny);
        }
    }

    /// <summary>Locked: click-through, only the HUD. Unlocked: edit mode (frame, padlock), can be dragged.</summary>
    private static void ApplyLock(IntPtr hwnd, bool locked, bool notify)
    {
        _locked = locked;
        var ex = (long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        ex = locked ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (IntPtr)ex);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        Present(hwnd);

        if (notify)
        {
            _onLockChanged?.Invoke(locked);
            Activity.Muted(locked
                ? L.T($"Overlay verrouillé : la souris passe au travers.{(LockHotkeyRegistered ? " Ctrl+Maj+L pour le déverrouiller." : "")}",
                      $"Overlay locked: the mouse goes through it.{(LockHotkeyRegistered ? " Ctrl+Shift+L to unlock it." : "")}",
                      $"Overlay bloqueado: el ratón lo atraviesa.{(LockHotkeyRegistered ? " Ctrl+Mayús+L para desbloquearlo." : "")}")
                : L.T("Overlay déverrouillé : glisse-le où tu veux, puis clique sur le cadenas pour le verrouiller.",
                      "Overlay unlocked: drag it wherever you want, then click the padlock to lock it.",
                      "Overlay desbloqueado: arrástralo donde quieras y pulsa el candado para bloquearlo."));
            StateChanged?.Invoke();
        }
    }

    private static void SetVisible(IntPtr hwnd, bool visible)
    {
        if (visible)
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Present(hwnd);
        }
        else
        {
            ShowWindow(hwnd, SW_HIDE);
            Activity.Muted(HotkeyRegistered
                ? L.T("Overlay masqué : Ctrl+Maj+O (n'importe où) pour le réafficher.", "Overlay hidden: Ctrl+Shift+O (anywhere) to show it again.", "Overlay oculto: Ctrl+Mayús+O (desde cualquier sitio) para volver a mostrarlo.")
                : L.T("Overlay masqué : réaffiche-le depuis l'interface.", "Overlay hidden: show it again from the interface.", "Overlay oculto: vuelve a mostrarlo desde la interfaz."));
        }
        StateChanged?.Invoke();
    }

    // ---- Win32 ------------------------------------------------------------------------------
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20, HTCLIENT = 1, HTCAPTION = 2;
    private const uint WM_DESTROY = 0x0002, WM_SETCURSOR = 0x0020, WM_NCHITTEST = 0x0084, WM_NCRBUTTONUP = 0x00A5, WM_TIMER = 0x0113,
        WM_LBUTTONUP = 0x0202, WM_EXITSIZEMOVE = 0x0232, WM_HOTKEY = 0x0312,
        WM_APP_TOGGLE = 0x8001, WM_APP_RELAYOUT = 0x8002, WM_APP_LOCK = 0x8003, WM_APP_APPEARANCE = 0x8004, WM_APP_SHOW = 0x8005, WM_APP_CORNER = 0x8006;
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_O = 0x4F, VK_L = 0x4C;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, LOGPIXELSY = 90;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr IDC_SIZEALL = new(32646), IDC_HAND = new(32649);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private const uint WM_DPICHANGED = 0x02E0;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref POINT pos, ref SIZE size, IntPtr srcDc, ref POINT srcPos, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
