using FortniteChestTracker;

// A Windows app without a console: its interface is a web page (http://127.0.0.1:5317), an icon next to the Windows
// clock and the overlay. Options are only used internally (standby, uninstall, restart after an update) or in development.
if (!OperatingSystem.IsWindows())
{
    return 1;
}

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    ErrorLog.Write("Crash", e.ExceptionObject);
    if (!OperatingSystem.IsWindows())
    {
        return;
    }
    App.Message(L.T("Erreur inattendue : ", "Unexpected error: ", "Error inesperado: ") + (e.ExceptionObject as Exception)?.Message
        + "\n\n" + L.T("Le détail est dans errors.log (dossier des données).", "Details are in errors.log (data folder).", "El detalle está en errors.log (carpeta de datos)."), error: true);
};

// ---- Arguments ---------------------------------------------------------------------------
string? dataArg = null;      // --data <folder>: another data folder (development, tests)
string? previewPath = null;  // --overlay-preview <file.png>: development, saves pictures of the overlay and quits
var auto = false;            // --auto: opened by the standby when Fortnite started (no page opened, closes after Fortnite)
var afterUpdate = false;     // --after-update: restarted by the updater, waits for the previous instance to quit
for (var i = 0; i < args.Length; i++)
{
    string? Next() => i + 1 < args.Length ? args[++i] : null;
    switch (args[i])
    {
        case "--data": dataArg = Next(); break;
        case "--overlay-preview": previewPath = Next(); break;
        case "--auto": auto = true; break;
        case "--after-update": afterUpdate = true; break;
    }
}

// ---- Data folder: the default one is moved from its 1.0.x location first ------------------
if (DataFolders.IsDefault(dataArg) && DataFolders.MigrationPending && !WaitForLegacyInstance(afterUpdate || auto ? 20 : 5))
{
    // A 1.0.x copy still runs on that folder (only once, before the move): it must be closed first.
    if (args.Contains("--standby") || auto)
    {
        return 0;
    }
    L.Set(L.SystemDefault);
    App.Message(L.T("Une ancienne version de Fortnite - Chest Tracker est encore ouverte : quitte-la (icône près de l'horloge Windows → Quitter), puis relance celle-ci.",
        "An older version of Fortnite - Chest Tracker is still open: quit it (icon next to the Windows clock → Quit), then start this one again.",
        "Una versión anterior de Fortnite - Chest Tracker sigue abierta: ciérrala (icono junto al reloj de Windows → Salir) y vuelve a abrir esta."));
    return 0;
}
var dataDir = DataFolders.Resolve(dataArg);

// --uninstall: run by Windows' "Installed apps" (or the settings page) for the copy installed on this PC. Works on
// its own: no interface, only a confirmation box. The player's data is kept.
if (args.Contains("--uninstall"))
{
    return Installer.RunUninstall(args, dataDir);
}

// --standby: the invisible process that waits for Fortnite to start ("open with Fortnite"). No window, nothing else.
if (args.Contains("--standby"))
{
    return FortniteWatch.RunStandby(dataDir);
}

var store = new Store(dataDir);
ErrorLog.FilePath = store.ErrorLogPath;

// One instance at a time: two would count the same matches and fight over the shortcuts.
using var instance = new Mutex(false, AppInfo.InstanceMutexFor(dataDir));
if (previewPath == null && !AcquireInstance(instance, afterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero))
{
    if (auto)
    {
        return 0; // opened with Fortnite while the player had already opened it: nothing to do
    }
    L.Set(SafeLoadConfig(store)?.Language ?? L.SystemDefault);
    // Already running: show its interface instead (double-clicking the exe again = "open the counter").
    string? url = null;
    try { url = File.ReadAllText(App.UrlFile(store)).Trim(); } catch { }
    if (url != null && url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }
    else
    {
        App.Message(L.T("Fortnite - Chest Tracker est déjà ouvert : son icône est près de l'horloge Windows.",
            "Fortnite - Chest Tracker is already open: its icon is next to the Windows clock.",
            "Fortnite - Chest Tracker ya está abierto: su icono está junto al reloj de Windows."));
    }
    return 0;
}

// The installed copy still has the file name of version 1.0.x (that is what their updater leaves): it takes the
// current name, the shortcuts, "Installed apps" and "open with Fortnite" follow, and it restarts from there.
if (previewPath == null && Installer.RenameLegacyInstalledExe(dataDir) is { } renamedExe)
{
    try { instance.ReleaseMutex(); } catch { }
    try
    {
        var restart = new System.Diagnostics.ProcessStartInfo(renamedExe) { UseShellExecute = false };
        foreach (var a in args)
        {
            restart.ArgumentList.Add(a);
        }
        System.Diagnostics.Process.Start(restart)?.Dispose();
        return 0;
    }
    catch (Exception ex)
    {
        // Could not start the new file: carry on from this one (it is deleted only once the new one runs).
        ErrorLog.Write("Restart after renaming the installed exe", ex);
        if (!AcquireInstance(instance, TimeSpan.FromSeconds(5)))
        {
            return 0;
        }
    }
}

// ---- Configuration -----------------------------------------------------------------------
Config? saved;
try
{
    saved = store.LoadConfig();
}
catch (IOException ex)
{
    // config.json locked or unreadable and impossible to back up: do not start over with a blank config.
    App.Message(ex.Message, error: true);
    return 1;
}
var setupPending = saved == null;
var config = saved ?? new Config();
// Language: the saved choice, else the Windows one (the setup lets the player change it).
config.Language = L.Normalize(config.Language) ?? L.SystemDefault;
L.Set(config.Language);
if (string.IsNullOrWhiteSpace(config.DemosFolder))
{
    config.DemosFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");
}

if (previewPath != null)
{
    // Development: draws the overlay off-screen and saves it as PNG (idle + match in progress), then quits.
    store.LoadPrizeNames();
    var previewData = Profiles.View(store.LoadData(), config);
    // In memory only: the 3 latest matches count as "this session", to show a realistic session line.
    foreach (var m in previewData.Matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).Take(3))
    {
        m.MatchStartUtc = DateTime.UtcNow;
    }
    // Same drawing as the window (transparent PNG): idle, match in progress, live, live just after a chest, edit mode.
    using var renderer = new HudRenderer();
    var scale = config.OverlaySize / 100f;
    var fakeLive = new MatchRecord { ChestsOpened = 12, TreasureFinds = { new TreasureFind { Item = "Trésor" } } };
    foreach (var (suffix, live, inProgress, edit, pop) in new[]
             {
                 ("-idle", (MatchRecord?)null, false, false, 0f), ("-inprogress", null, true, false, 0f), ("-live", fakeLive, true, false, 0f),
                 ("-counted", null, false, false, 0.6f), ("-edit", fakeLive, true, true, 0f),
                 ("-compact-idle", null, false, false, 0f), ("-compact-live", fakeLive, true, false, 0f), ("-compact-counted", null, false, false, 0.6f),
                 ("-compact-edit", fakeLive, true, true, 0f),
             })
    {
        config.OverlayStyle = suffix.StartsWith("-compact", StringComparison.Ordinal) ? OverlayAppearance.Compact : OverlayAppearance.Full;
        var file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(previewPath)) ?? ".", Path.GetFileNameWithoutExtension(previewPath) + suffix + ".png");
        var view = HudModel.Build(previewData, config, live, inProgress);
        // "-counted": a match was just counted, its chests land at once ("+N")
        var frame = renderer.Render(view, scale, edit, 0, pop, pop > 0 ? view.Chests ?? 0 : 0);
        File.WriteAllBytes(file, frame.ToPng());
    }
    return 0;
}

// ---- The counter, with its web interface -------------------------------------------------
if (!setupPending)
{
    try { store.SaveConfig(config); } catch { /* saved again on the next change */ }
}
return new App(dataDir, store, config, setupPending, auto, afterUpdate, instance, args).Run();

static bool AcquireInstance(Mutex m, TimeSpan wait)
{
    try
    {
        return m.WaitOne(wait);
    }
    catch (AbandonedMutexException)
    {
        return true; // the previous instance was killed: it is ours now
    }
}

static Config? SafeLoadConfig(Store s)
{
    try { return s.LoadConfig(); } catch { return null; }
}

// True when no 1.0.x copy runs on the old data folder (any more), waiting up to the given number of seconds
// (after an update, the previous version is still closing).
static bool WaitForLegacyInstance(int seconds)
{
    var name = AppInfo.InstanceMutexFor(DataFolders.LegacyDefault, Legacy.InstanceMutexPrefix);
    for (var waited = 0; ; waited++)
    {
        if (!Mutex.TryOpenExisting(name, out var running))
        {
            return true;
        }
        running.Dispose();
        if (waited >= seconds * 4)
        {
            return false;
        }
        Thread.Sleep(250);
    }
}
