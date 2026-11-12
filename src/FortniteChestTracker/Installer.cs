using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FortniteChestTracker;

/// <summary>
/// "Install on this PC": copies the exe to %LOCALAPPDATA%\Programs\Fortnite Chest Tracker (per user, no admin rights),
/// adds Start menu and desktop shortcuts and an entry in Windows' "Installed apps", whose Uninstall button runs
/// <c>FortniteChestTracker.exe --uninstall</c>. "Open with Fortnite" and the updates then always use that copy.
/// The player's data (%APPDATA%\FortniteChestTracker) is never touched, not even by the uninstall.
/// The folders and the registry key can be redirected (FCT_INSTALL_ROOT, FCT_STARTMENU_DIR,
/// FCT_DESKTOP_DIR, FCT_UNINSTALL_KEY) so that tests never touch the real ones.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    public const string AppName = "Fortnite - Chest Tracker";
    public const string Publisher = "ayman-studio";
    private const string ExeName = AppInfo.ExeName;
    private const string ShortcutName = AppName + ".lnk";

    internal static string InstallFolder { get; set; } = Env("FCT_INSTALL_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Fortnite Chest Tracker");

    internal static string StartMenuFolder { get; set; } = Env("FCT_STARTMENU_DIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    internal static string DesktopFolder { get; set; } = Env("FCT_DESKTOP_DIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Under HKEY_CURRENT_USER.</summary>
    internal static string UninstallKey { get; set; } = Env("FCT_UNINSTALL_KEY")
        ?? @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FortniteChestTracker";

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    public static string InstalledExe => Path.Combine(InstallFolder, ExeName);

    /// <summary>The installed copy under the file name of versions 1.0.x (see <see cref="RenameLegacyInstalledExe(string)"/>).</summary>
    internal static string LegacyInstalledExe => Path.Combine(InstallFolder, Legacy.ExeName);

    public static bool InstalledExeExists => File.Exists(InstalledExe);

    /// <summary>The running exe is the installed one.</summary>
    public static bool IsInstalled => IsInstalledPath(AppInfo.ExePath);

    internal static bool IsInstalledPath(string? exe) => exe != null && (SamePath(exe, InstalledExe) || SamePath(exe, LegacyInstalledExe));

    internal static IEnumerable<string> Shortcuts =>
        new[] { StartMenuFolder, DesktopFolder }.Where(f => !string.IsNullOrEmpty(f)).Select(f => Path.Combine(f, ShortcutName));

    // ---- Install ------------------------------------------------------------------------------------

    /// <summary>Installs the running exe. Returns null when done, else the reason it failed.</summary>
    public static string? Install()
    {
        if (AppInfo.ExePath is not { } exe)
        {
            return L.T("possible seulement depuis FortniteChestTracker.exe", "only possible from FortniteChestTracker.exe", "solo es posible desde FortniteChestTracker.exe");
        }
        return Install(exe, AppInfo.VersionText, AppInfo.RepoUrl);
    }

    internal static string? Install(string sourceExe, string version, string aboutUrl)
    {
        try
        {
            Directory.CreateDirectory(InstallFolder);
            var target = InstalledExe;
            if (!SamePath(sourceExe, target) && NeedsCopy(sourceExe, target))
            {
                CopyInPlace(sourceExe, target);
            }
            // A newer copy already installed is kept (never downgraded): register its own version.
            var installedVersion = ExeVersion(target) is { } v && !SamePath(sourceExe, target) ? v.ToString(3) : version;
            foreach (var link in Shortcuts)
            {
                CreateShortcut(link, target, ShortcutDescription);
            }
            Register(target, installedVersion, aboutUrl);
            return null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Install on this PC", ex);
            return ex.Message;
        }
    }

    private static string ShortcutDescription => L.T("Compte les coffres ouverts dans Fortnite", "Counts the chests opened in Fortnite", "Cuenta los cofres abiertos en Fortnite");

    // ---- Installed copy of versions 1.0.x -----------------------------------------------------------

    /// <summary>
    /// When this exe is the installed copy still under its 1.0.x file name (their updater replaced the file in place):
    /// copies it to <see cref="InstalledExe"/>, points the shortcuts, the "Installed apps" entry and "open with
    /// Fortnite" to the copy and returns its path, to restart from it. The old file stays until the new one runs
    /// (<see cref="DeleteLegacyExe"/>). Null when there is nothing to do or it failed (the tool goes on from here).
    /// </summary>
    public static string? RenameLegacyInstalledExe(string dataDir)
    {
        if (AppInfo.ExePath is not { } exe || !SamePath(exe, LegacyInstalledExe))
        {
            return null;
        }
        var renamed = RenameLegacyInstalledExe(exe, AppInfo.VersionText, AppInfo.RepoUrl, dataDir);
        if (renamed != null)
        {
            FortniteWatch.StopStandby(); // a standby running the old file: started again from the new one by the counter
        }
        return renamed;
    }

    internal static string? RenameLegacyInstalledExe(string exe, string version, string aboutUrl, string dataDir)
    {
        if (!SamePath(exe, LegacyInstalledExe) || !File.Exists(exe))
        {
            return null;
        }
        try
        {
            var target = InstalledExe;
            if (NeedsCopy(exe, target)) // a newer copy already there is kept
            {
                CopyInPlace(exe, target);
            }
            if (!File.Exists(target) || new FileInfo(target).Length == 0)
            {
                return null;
            }
            foreach (var link in Shortcuts)
            {
                // Only the shortcuts the install made (the player may have deleted one on purpose).
                if (File.Exists(link) && ShortcutTarget(link) is { } current && SamePath(current, exe))
                {
                    CreateShortcut(link, target, ShortcutDescription);
                }
            }
            RefreshRegistration(target, ExeVersion(target)?.ToString(3) ?? version, aboutUrl);
            FortniteWatch.MoveLegacyRunEntry(target, dataDir, InstallFolder);
            return target;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Renaming the installed exe", ex);
            return null;
        }
    }

    /// <summary>
    /// Run by the renamed installed exe: deletes the copy left under the 1.0.x name, unless a shortcut or the logon
    /// entry still starts it. True when it was deleted.
    /// </summary>
    internal static bool DeleteLegacyExe(string? runningExe)
    {
        var legacy = LegacyInstalledExe;
        if (runningExe == null || !SamePath(runningExe, InstalledExe) || !File.Exists(legacy))
        {
            return false;
        }
        if (Shortcuts.Any(link => File.Exists(link) && ShortcutTarget(link) is { } t && SamePath(t, legacy)) || FortniteWatch.RunEntryStarts(legacy))
        {
            return false;
        }
        try
        {
            File.Delete(legacy);
            return true;
        }
        catch
        {
            return false; // still closing: next time
        }
    }

    /// <summary>Not installed yet, or a different build; an installed copy that is newer is kept.</summary>
    internal static bool NeedsCopy(string source, string target)
    {
        if (!File.Exists(target))
        {
            return true;
        }
        if (ExeVersion(source) is { } s && ExeVersion(target) is { } t && t > s)
        {
            return false;
        }
        var a = new FileInfo(source);
        var b = new FileInfo(target);
        if (a.Length != b.Length)
        {
            return true;
        }
        using var fa = File.OpenRead(source);
        using var fb = File.OpenRead(target);
        return !SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    /// <summary>Like the updater: the installed exe may be running (counter or "open with Fortnite"), so it is renamed, not overwritten.</summary>
    private static void CopyInPlace(string source, string target)
    {
        var download = target + ".download";
        File.Copy(source, download, overwrite: true);
        var old = target + ".old";
        var moved = false;
        try
        {
            if (File.Exists(target))
            {
                if (File.Exists(old))
                {
                    try { File.Delete(old); } catch { old = target + $".{DateTime.Now:yyyyMMddHHmmss}.old"; }
                }
                File.Move(target, old);
                moved = true;
            }
            File.Move(download, target);
        }
        catch
        {
            try { File.Delete(download); } catch { }
            try { if (moved && !File.Exists(target)) File.Move(old, target); } catch { }
            throw;
        }
    }

    /// <summary>Version of an exe (major.minor.build), null when it has none (not a real exe).</summary>
    internal static Version? ExeVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileMajorPart + info.FileMinorPart + info.FileBuildPart > 0
                ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The entry in Windows' "Installed apps" (Settings → Apps), per user.</summary>
    internal static void Register(string exe, string version, string aboutUrl)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", Path.GetDirectoryName(exe) ?? "");
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("URLInfoAbout", aboutUrl);
        if (key.GetValue("InstallDate") == null)
        {
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
    }

    /// <summary>
    /// At startup of the installed exe: after an update (the exe was replaced), the version shown in "Installed apps"
    /// follows. Writes nothing when it is already right.
    /// </summary>
    public static void RefreshRegistration()
    {
        if (AppInfo.ExePath is { } exe && IsInstalledPath(exe))
        {
            RefreshRegistration(exe, AppInfo.VersionText, AppInfo.RepoUrl);
        }
    }

    internal static bool RefreshRegistration(string exe, string version, string aboutUrl)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
        {
            // Only an existing entry is refreshed: creating one is Install's job (a copy left after a failed uninstall
            // must not register itself again).
            if (key == null || (key.GetValue("DisplayVersion") as string == version && key.GetValue("UninstallString") as string == $"\"{exe}\" --uninstall"))
            {
                return false;
            }
        }
        Register(exe, version, aboutUrl);
        return true;
    }

    // ---- Uninstall ----------------------------------------------------------------------------------

    /// <summary>
    /// <c>FortniteChestTracker.exe --uninstall</c> (Windows' "Installed apps", or the settings page). Asks first, unless
    /// <c>--confirmed</c> (already asked by the page) or <c>--quiet</c> (no window at all).
    /// </summary>
    public static int RunUninstall(string[] args, string dataDir)
    {
        var quiet = args.Contains("--quiet");
        L.Set(ReadLanguage(dataDir) ?? L.SystemDefault);

        if (!quiet && !args.Contains("--confirmed") && !App.Ask(L.T(
                $"Désinstaller Fortnite - Chest Tracker de ce PC ?\n\nLe programme, ses raccourcis (menu Démarrer et bureau) et l'ouverture avec Fortnite seront retirés.\n\nTes données (historique, réglages) sont conservées dans :\n{dataDir}\nSupprime ce dossier toi-même si tu veux tout effacer.",
                $"Uninstall Fortnite - Chest Tracker from this PC?\n\nThe program, its shortcuts (Start menu and desktop) and \"Open with Fortnite\" will be removed.\n\nYour data (history, settings) is kept in:\n{dataDir}\nDelete that folder yourself if you want to erase everything.",
                $"¿Desinstalar Fortnite - Chest Tracker de este PC?\n\nSe quitarán el programa, sus accesos directos (menú Inicio y escritorio) y la apertura con Fortnite.\n\nTus datos (historial, ajustes) se conservan en:\n{dataDir}\nBorra esa carpeta tú mismo si quieres eliminarlo todo.")))
        {
            return 0;
        }

        try
        {
            Uninstall(dataDir);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Uninstall", ex);
            if (!quiet)
            {
                App.Message(L.T("Désinstallation incomplète : ", "Uninstall incomplete: ", "Desinstalación incompleta: ") + ex.Message, error: true);
            }
            return 1;
        }
        if (!quiet)
        {
            App.Message(L.T($"Fortnite - Chest Tracker a été désinstallé.\n\nTes données restent dans :\n{dataDir}",
                $"Fortnite - Chest Tracker has been uninstalled.\n\nYour data stays in:\n{dataDir}",
                $"Fortnite - Chest Tracker se ha desinstalado.\n\nTus datos siguen en:\n{dataDir}"));
        }
        DeleteFolderLater(); // last: this exe is in that folder and stays locked while this process runs (even behind a message box)
        return 0;
    }

    /// <summary>Removes what the install added, except the program folder (<see cref="DeleteFolderLater"/>, once this process exits).</summary>
    internal static void Uninstall(string dataDir)
    {
        var exe = InstalledExe;
        // "Open with Fortnite": its entry at Windows logon, when it starts the installed exe.
        var standbyInstalled = FortniteWatch.RemoveRunEntryIn(InstallFolder);
        // The counter and its waiting process ("standby"), if they run from the install folder.
        CloseRunning(dataDir, standbyInstalled);
        foreach (var link in Shortcuts)
        {
            if (File.Exists(link) && ShortcutTarget(link) is { } target && SamePath(target, exe))
            {
                File.Delete(link);
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Invite.Unregister(exe);
    }

    private static string? ReadLanguage(string dataDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "config.json")));
            return doc.RootElement.TryGetProperty("Language", out var lang) && lang.ValueKind == JsonValueKind.String ? lang.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Other processes of the tool started from the install folder: asked to close, then ended after a while.</summary>
    private static void CloseRunning(string dataDir, bool stopStandby)
    {
        var running = new List<Process>();
        foreach (var p in new[] { ExeName, Legacy.ExeName }.SelectMany(name => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name))))
        {
            string? path = null;
            try { path = p.Id != Environment.ProcessId ? p.MainModule?.FileName : null; } catch { }
            if (path != null && IsInside(path, InstallFolder))
            {
                running.Add(p);
            }
            else
            {
                p.Dispose();
            }
        }
        if (running.Count == 0)
        {
            return;
        }
        if (stopStandby)
        {
            FortniteWatch.StopStandby(); // the logon entry started the installed exe: so does the standby running now
        }
        try { QuitThroughInterface(dataDir); } catch { /* not that data folder, or no interface: ended below */ }
        var deadline = DateTime.UtcNow.AddSeconds(15);
        foreach (var p in running)
        {
            try
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !p.WaitForExit(left))
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch
            {
                // already gone
            }
            p.Dispose();
        }
    }

    /// <summary>Same as the page's Quit button: the counter saves and closes normally.</summary>
    private static void QuitThroughInterface(string dataDir)
    {
        var url = File.ReadAllText(Path.Combine(dataDir, "interface-url.txt")).Trim();
        if (!url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
        {
            return;
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var page = http.GetStringAsync(url).GetAwaiter().GetResult();
        var token = Regex.Match(page, "<meta name=\"fct-token\" content=\"([0-9a-f]+)\"").Groups[1].Value;
        if (token.Length == 0)
        {
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/api/cmd/quit")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Tracker-Token", token);
        http.Send(request).Dispose();
    }

    /// <summary>
    /// A running exe cannot be deleted: a hidden cmd waits ~2 s for this process to exit (and ~5 s more if the exe is
    /// still there), then deletes the exe, what updates leave next to it, and the folder if it is then empty.
    /// Never a recursive delete of a whole folder.
    /// </summary>
    internal static void DeleteFolderLater()
    {
        var folder = Path.GetFullPath(InstallFolder).TrimEnd('\\', '/');
        if (!Directory.Exists(folder))
        {
            return;
        }
        var exe = Path.Combine(folder, ExeName);
        var legacy = Path.Combine(folder, Legacy.ExeName);
        var del = $"del /f /q \"{exe}\" \"{exe}.*\" \"{legacy}\" \"{legacy}.*\" > nul 2>&1";
        var script = $"ping 127.0.0.1 -n 3 > nul & {del} & (if exist \"{exe}\" (ping 127.0.0.1 -n 6 > nul & {del})) & rmdir \"{folder}\" > nul 2>&1";
        Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c " + script)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(), // not the folder being deleted
        })?.Dispose();
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    internal static bool IsInside(string path, string folder)
    {
        try
        {
            var dir = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---- Shortcuts (.lnk) through the Windows shell, no PowerShell ----------------------------------

    internal static void CreateShortcut(string path, string target, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>The program a shortcut opens, or null when it cannot be read.</summary>
    internal static string? ShortcutTarget(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0); // STGM_READ
            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0x4); // SLGP_RAWPATH
            return buffer.Length > 0 ? Environment.ExpandEnvironmentVariables(buffer.ToString()) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
