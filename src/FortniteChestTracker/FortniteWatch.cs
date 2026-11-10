using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FortniteChestTracker;

/// <summary>
/// "Start with Fortnite". Windows cannot start a program when another one starts (not without admin rights), so a
/// tiny invisible process ("standby") is started at Windows logon: every few seconds it checks whether Fortnite is
/// running, and opens the tool when it is. The tool closes itself once Fortnite is closed and the last match counted.
/// The standby reads nothing from the game: it only looks at the list of running programs.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FortniteWatch
{
    internal static string RunKey { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Run"; // under HKEY_CURRENT_USER; another key in tests
    // FCT_RUN_VALUE: another value name, so that tests never touch the real entry.
    internal static string RunValue { get; set; } = Environment.GetEnvironmentVariable("FCT_RUN_VALUE") is { Length: > 0 } name ? name : "FortniteChestTracker";
    /// <summary>Value name used by versions 1.0.x (moved to <see cref="RunValue"/>). Another name in tests.</summary>
    internal static string LegacyRunValue { get; set; } = Legacy.RunValue;
    // FCT_GAME_PROCESS: another process name, to test the standby without Fortnite.
    private static readonly string[] GameProcesses =
        { Environment.GetEnvironmentVariable("FCT_GAME_PROCESS") is { Length: > 0 } test ? test : "FortniteClient-Win64-Shipping" };

    public static bool IsFortniteRunning()
    {
        foreach (var name in GameProcesses)
        {
            var found = Process.GetProcessesByName(name);
            var any = found.Length > 0;
            foreach (var p in found)
            {
                p.Dispose();
            }
            if (any)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Turns "start with Fortnite" on or off: entry at Windows logon + standby started or stopped right away.
    /// Returns false when it cannot be done (tool not run from its published exe).
    /// </summary>
    public static bool Apply(bool enabled, string dataDir)
    {
        var exe = AppInfo.ExePath;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                if (exe == null)
                {
                    return false;
                }
                key.SetValue(RunValue, $"\"{exe}\" --standby {DataArg(dataDir)}"); // refreshed at each launch: follows the exe if it is moved
                RemoveLegacyEntry(key, exe);
                // A standby started by another exe (older version, exe moved) would keep opening that one until the
                // next logon: replace it with ours.
                StopStandby();
                for (var i = 0; i < 30 && Mutex.TryOpenExisting(AppInfo.StandbyMutex, out var previous); i++)
                {
                    previous.Dispose();
                    Thread.Sleep(100);
                }
                StartStandby(exe, dataDir);
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
                if (exe != null)
                {
                    RemoveLegacyEntry(key, exe);
                }
                StopStandby();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The 1.0.x entry, when it starts <paramref name="exe"/> or the installed copy: replaced by the current one.</summary>
    private static void RemoveLegacyEntry(RegistryKey key, string exe)
    {
        if (key.GetValue(LegacyRunValue) is string command && ExeOf(command) is { } legacy
            && (Installer.SamePath(legacy, exe) || Installer.IsInside(legacy, Installer.InstallFolder)))
        {
            key.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Uninstall: removes the entry at Windows logon when it starts an exe from <paramref name="folder"/> (an entry
    /// that starts another copy of the tool is left alone). True when it was removed.
    /// </summary>
    public static bool RemoveRunEntryIn(string folder)
    {
        var removed = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            foreach (var value in new[] { RunValue, LegacyRunValue })
            {
                if (key?.GetValue(value) is string command && ExeOf(command) is { } exe && Installer.IsInside(exe, folder))
                {
                    key.DeleteValue(value, throwOnMissingValue: false);
                    removed = true;
                }
            }
        }
        catch
        {
            // left as is
        }
        return removed;
    }

    /// <summary>
    /// The installed copy was renamed (<see cref="Installer.RenameLegacyInstalledExe(string)"/>): the 1.0.x entry
    /// that starts a program of <paramref name="folder"/> is replaced by the current entry for <paramref name="exe"/>.
    /// True when it was moved.
    /// </summary>
    internal static bool MoveLegacyRunEntry(string exe, string dataDir, string folder)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyRunValue) is not string command || ExeOf(command) is not { } legacy || !Installer.IsInside(legacy, folder))
        {
            return false;
        }
        key.SetValue(RunValue, $"\"{exe}\" --standby {DataArg(dataDir)}");
        key.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        return true;
    }

    /// <summary>True when an entry at Windows logon (current or 1.0.x) starts <paramref name="exe"/>.</summary>
    internal static bool RunEntryStarts(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return new[] { RunValue, LegacyRunValue }.Any(v => key?.GetValue(v) is string command && ExeOf(command) is { } program && Installer.SamePath(program, exe));
        }
        catch
        {
            return true; // unknown: keep the file
        }
    }

    /// <summary>The program of a command line (<c>"C:\x\FortniteChestTracker.exe" --standby …</c> gives <c>C:\x\FortniteChestTracker.exe</c>).</summary>
    internal static string? ExeOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static string DataArg(string dataDir) => $"--data \"{Path.GetFullPath(dataDir).TrimEnd('\\', '/')}\"";

    public static void StartStandby(string exe, string dataDir)
    {
        if (Mutex.TryOpenExisting(AppInfo.StandbyMutex, out var running))
        {
            running.Dispose();
            return;
        }
        Process.Start(new ProcessStartInfo(exe, $"--standby {DataArg(dataDir)}") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }

    /// <summary>Stops the standby, also one of versions 1.0.x (it would keep opening the old exe).</summary>
    public static void StopStandby()
    {
        foreach (var name in new[] { AppInfo.StandbyStopEvent, Legacy.StandbyStopEvent })
        {
            if (EventWaitHandle.TryOpenExisting(name, out var stop))
            {
                stop.Set();
                stop.Dispose();
            }
        }
    }

    /// <summary>The standby process: no window, waits for Fortnite, opens the tool, waits for Fortnite to close.</summary>
    public static int RunStandby(string dataDir)
    {
        using var single = new Mutex(false, AppInfo.StandbyMutex);
        try
        {
            if (!single.WaitOne(0))
            {
                return 0; // already waiting
            }
        }
        catch (AbandonedMutexException)
        {
            // previous standby was killed: take over
        }

        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.StandbyStopEvent);
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { }
        try { EcoQos.Enable(); } catch { }
        GC.Collect();

        var exe = AppInfo.ExePath;
        if (exe == null)
        {
            return 1;
        }

        while (true)
        {
            if (stop.WaitOne(TimeSpan.FromSeconds(5)))
            {
                return 0;
            }
            if (!IsFortniteRunning())
            {
                continue;
            }

            // Turned off in the settings meanwhile: stop waiting.
            Config? current = null;
            try { current = new Store(dataDir).LoadConfig(); } catch { /* unreadable right now: open the tool anyway */ }
            if (current is { StartWithFortnite: false })
            {
                return 0;
            }

            if (Mutex.TryOpenExisting(AppInfo.InstanceMutexFor(dataDir), out var open))
            {
                open.Dispose(); // the tool is already open
            }
            else
            {
                try { Process.Start(new ProcessStartInfo(exe, $"--auto {DataArg(dataDir)}") { UseShellExecute = false })?.Dispose(); } catch { }
            }

            // Arm again only for the next Fortnite launch (the player may have closed the tool on purpose).
            while (IsFortniteRunning())
            {
                if (stop.WaitOne(TimeSpan.FromSeconds(10)))
                {
                    return 0;
                }
            }
            GC.Collect();
        }
    }
}
