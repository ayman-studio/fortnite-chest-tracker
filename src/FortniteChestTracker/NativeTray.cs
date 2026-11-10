using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FortniteChestTracker;

/// <summary>An entry of the tray icon menu.</summary>
/// <param name="Text">Shown text; null = separator.</param>
public sealed record TrayItem(string? Text, Action? OnClick = null, bool Checked = false, bool Default = false, bool Enabled = true)
{
    public static TrayItem Separator { get; } = new((string?)null);
}

/// <summary>
/// The icon next to the Windows clock: the tool has no console window any more, this is how it is reopened or quit.
/// Click = <see cref="Start"/>'s onClick (open the interface). Right click = menu built by the given function.
/// Plain Win32 on its own thread, like the overlay (no WinForms).
/// </summary>
[SupportedOSPlatform("windows")]
public static class NativeTray
{
    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static IntPtr _icon;
    private static WndProcDelegate? _wndProc; // keep the delegate alive
    private static Action? _onClick;
    private static Func<IReadOnlyList<TrayItem>>? _menu;
    private static volatile string _tip = "Fortnite - Chest Tracker";
    private static uint _taskbarCreated;

    public static bool IsRunning => _hwnd != IntPtr.Zero;

    public static void Start(string tip, Action onClick, Func<IReadOnlyList<TrayItem>> menu)
    {
        if (_thread != null)
        {
            return;
        }
        _tip = tip;
        _onClick = onClick;
        _menu = menu;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                Run(ready);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Tray icon unavailable", ex);
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Text shown when the mouse is over the icon (at most 127 characters).</summary>
    public static void SetTooltip(string tip)
    {
        if (tip == _tip)
        {
            return;
        }
        _tip = tip;
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_TIP, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Windows notification next to the clock (kept for rare, important things: it shows over the game).</summary>
    public static void Notify(string title, string text)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        var data = NewData(NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
        data.hBalloonIcon = _icon;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>Removes the icon (also done automatically when the process ends normally).</summary>
    public static void Stop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static void Run(ManualResetEventSlim ready)
    {
        var hInstance = GetModuleHandleW(null);
        _wndProc = WndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = "FortniteChestTrackerTray",
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            throw new InvalidOperationException("RegisterClassEx failed");
        }
        // A real (never shown) window, not a message-only one: only those get "TaskbarCreated" when Explorer restarts.
        _hwnd = CreateWindowExW(0, wc.lpszClassName, "Fortnite - Chest Tracker", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowEx failed");
        }
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = AppIcon.Load(GetSystemMetrics(SM_CXSMICON)) is { } icon ? icon : LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
        AddIcon();
        ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    /// <summary>False when Windows refused the icon (the interface stays reachable by launching the exe again).</summary>
    public static bool IconShown { get; private set; }

    private static void AddIcon()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        IconShown = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!IconShown)
        {
            ErrorLog.Write("Tray icon", $"Shell_NotifyIcon(NIM_ADD) failed, error {Marshal.GetLastWin32Error()}, cbSize {data.cbSize}");
        }
    }

    private static NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = Truncate(_tip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            AddIcon(); // Explorer restarted: the icon must be added again
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case WM_APP_TRAY:
                switch ((uint)((long)lParam & 0xFFFF))
                {
                    case WM_LBUTTONUP:
                        Safe(_onClick);
                        break;
                    case WM_RBUTTONUP:
                        ShowMenu(hwnd);
                        break;
                }
                return IntPtr.Zero;

            case WM_APP_TIP:
            {
                var data = NewData(NIF_TIP);
                Shell_NotifyIconW(NIM_MODIFY, ref data);
                return IntPtr.Zero;
            }

            case WM_CLOSE:
            {
                var data = NewData(0);
                Shell_NotifyIconW(NIM_DELETE, ref data);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            }

            case WM_DESTROY:
                _hwnd = IntPtr.Zero;
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void ShowMenu(IntPtr hwnd)
    {
        var items = _menu?.Invoke() ?? Array.Empty<TrayItem>();
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Text == null)
                {
                    AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }
                var flags = MF_STRING | (item.Checked ? MF_CHECKED : 0) | (item.Enabled ? 0 : MF_GRAYED);
                AppendMenuW(menu, flags, (UIntPtr)(uint)(i + 1), item.Text);
                if (item.Default)
                {
                    SetMenuDefaultItem(menu, (uint)(i + 1), 0);
                }
            }
            GetCursorPos(out var pt);
            SetForegroundWindow(hwnd); // otherwise the menu does not close when clicking elsewhere
            var chosen = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
            PostMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= items.Count)
            {
                Safe(items[chosen - 1].OnClick);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void Safe(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Tray action failed", ex);
        }
    }

    // ---- Win32 ------------------------------------------------------------------------------
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_NULL = 0x0000, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
    private const uint WM_APP_TRAY = 0x8101, WM_APP_TIP = 0x8102;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const uint NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;
    private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
    private const int SM_CXSMICON = 49, IDI_APPLICATION = 32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}

/// <summary>The chest icon (Assets/app.ico, embedded): exe icon, tray icon and favicon of the web interface.</summary>
public static class AppIcon
{
    /// <summary>The .ico file as embedded in the exe.</summary>
    public static byte[]? Bytes()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("FortniteChestTracker.app.ico");
        if (stream == null)
        {
            return null;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>An HICON of about <paramref name="size"/> pixels made from the embedded .ico, or null.</summary>
    [SupportedOSPlatform("windows")]
    public static IntPtr? Load(int size)
    {
        var ico = Bytes();
        if (ico == null || ico.Length < 6)
        {
            return null;
        }
        // ICONDIR: reserved, type (1), count; then 16-byte entries: width, height, ..., bytes in res, image offset.
        var count = BitConverter.ToUInt16(ico, 4);
        int bestOffset = -1, bestLength = 0, bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var e = 6 + i * 16;
            if (e + 16 > ico.Length)
            {
                break;
            }
            var w = ico[e] == 0 ? 256 : ico[e];
            var length = BitConverter.ToInt32(ico, e + 8);
            var offset = BitConverter.ToInt32(ico, e + 12);
            if (offset < 0 || length <= 0 || offset + length > ico.Length)
            {
                continue;
            }
            // Smallest image at least as big as asked, else the biggest one.
            var better = bestOffset < 0
                || (w >= size && (bestSize < size || w < bestSize))
                || (w < size && bestSize < size && w > bestSize);
            if (better)
            {
                bestOffset = offset;
                bestLength = length;
                bestSize = w;
            }
        }
        if (bestOffset < 0)
        {
            return null;
        }
        var image = new byte[bestLength];
        Array.Copy(ico, bestOffset, image, 0, bestLength);
        var handle = CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000, size, size, 0);
        return handle == IntPtr.Zero ? null : handle;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, bool icon, uint version, int cx, int cy, uint flags);
}
