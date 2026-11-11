using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using FortniteChestTracker.Web;

namespace FortniteChestTracker;

/// <summary>
/// The counter with its web interface: no console window. It watches the replays folder, keeps the overlay up to
/// date, serves the interface on http://127.0.0.1:5317 and lives in the tray (icon next to the clock).
/// Threading: the history, the config and the tracker are only touched by the main loop (this class's thread).
/// Web requests, the tray and the overlay hand their actions to it (<see cref="Post"/>), except a few thread-safe
/// ones (opening a folder, showing / locking the overlay).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class App
{
    private sealed record Snapshot(long Version, string Json);

    private readonly string _dataDir;
    private readonly Store _store;
    private readonly Config _config;
    private readonly Mutex _instance;
    private readonly string[] _args;
    private readonly bool _afterUpdate;
    private bool _auto;
    private bool _setupPending;

    private TrackerData _data = null!;
    private Tracker _tracker = null!;
    private WebServer? _server;
    private FileSystemWatcher? _watcher;

    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<Action> _posted = new();
    private volatile bool _filesChanged;
    private volatile bool _quit;
    private bool _restart;
    private string? _restartExe; // restart from another exe (the copy just installed on this PC); null = this one
    private DateTime _nextScan = DateTime.UtcNow.AddSeconds(2);

    private volatile Snapshot _state = new(0, "{}");

    // Updates
    private volatile UpdateInfo? _update;
    private bool _updateAnnounced;
    private volatile bool _checking;
    private volatile string? _lastCheck;
    private bool _installing;

    // Notices
    private bool _profileChoiceNeeded;
    private bool _hasReplays = true;
    /// <summary>Milestones of the profile shown at the previous state (see <see cref="AnnounceMilestones"/>).</summary>
    private (string? Profile, IReadOnlyList<Milestones.State> States)? _milestones;
    private DateTime? _autoCloseAt;
    private DateTime? _fortniteClosedSince;

    // Estimate of the matches without replay
    private volatile bool _estimateFetching;
    private string? _estimateError;

    public App(string dataDir, Store store, Config config, bool setupPending, bool auto, bool afterUpdate, Mutex instance, string[] args)
    {
        _dataDir = dataDir;
        _store = store;
        _config = config;
        _setupPending = setupPending;
        _auto = auto;
        _afterUpdate = afterUpdate;
        _instance = instance;
        _args = args;
    }

    /// <summary>File where the running counter writes its address (a second launch opens the interface with it).</summary>
    public static string UrlFile(Store store) => Path.Combine(store.Folder, "interface-url.txt");

    public int Run()
    {
        LowerPriority();
        _store.LoadPrizeNames();
        _data = _store.LoadData();
        _tracker = new Tracker(_store, _config, _data);
        if (_tracker.UpgradeIfNeeded())
        {
            Activity.Info(L.T("Nouvelle version de l'analyse : les replays encore présents vont être réanalysés (sans doublon).",
                "New analysis version: the replays still on disk will be analyzed again (no duplicates).",
                "Nueva versión del análisis: las repeticiones del disco se analizarán de nuevo (sin duplicados)."));
        }

        Activity.Changed += () => _wake.Set();
        NativeOverlay.StateChanged += () => _wake.Set();
        _tracker.Changed += Publish; // raised by Scan, on this thread: lets the page follow a long batch
        Publish(); // a first state before the server answers anything

        _server = new WebServer(() => { var s = _state; return (s.Version, s.Json); }, RunCommandAsync, UiTexts.Json)
        {
            HudPng = HudPng,
            ExportData = () => File.Exists(_store.DataPath) ? File.ReadAllBytes(_store.DataPath) : System.Text.Encoding.UTF8.GetBytes("{}"),
            ImportData = ImportHistoryAsync,
        };
        if (_server.Start())
        {
            try { File.WriteAllText(UrlFile(_store), _server.Url); } catch { /* only used by a second launch */ }
        }
        else
        {
            Activity.Error(L.T("Interface web indisponible : aucun port libre entre 5317 et 5336.", "Web interface unavailable: no free port between 5317 and 5336.",
                "Interfaz web no disponible: ningún puerto libre entre 5317 y 5336."));
            _server = null;
        }

        NativeTray.Start(Tooltip(), OpenUi, TrayMenu);
        if (!NativeTray.IconShown)
        {
            Activity.Warning(L.T("L'icône près de l'horloge n'a pas pu être ajoutée : relance FortniteChestTracker.exe pour rouvrir cette page.",
                "The icon next to the clock could not be added: start FortniteChestTracker.exe again to reopen this page.",
                "No se pudo añadir el icono junto al reloj: vuelve a abrir FortniteChestTracker.exe para volver a esta página."));
        }

        if (!_setupPending)
        {
            StartRuntime();
            StartUpdateCheck(manual: false);
        }
        CreateWatcher();
        Publish();

        if (!_auto && !_afterUpdate && (_setupPending || _config.OpenUiOnStart))
        {
            OpenUi();
        }

        while (!_quit)
        {
            RunPosted();

            if (!_quit && !_setupPending && DateTime.UtcNow >= _nextScan) // not when about to restart (update, install)
            {
                ScanOnce();
            }
            if (_auto && !_setupPending)
            {
                CheckAutoClose();
            }
            AnnounceUpdate();
            if (!_setupPending)
            {
                SeasonTick();
            }
            Publish();

            var wait = _setupPending ? TimeSpan.FromMinutes(1) : _nextScan - DateTime.UtcNow;
            if (wait > TimeSpan.Zero && !_quit)
            {
                _wake.WaitOne(wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait);
            }
            if (_filesChanged)
            {
                // A replay changed: check again within 2 s (the readiness checks decide when it is complete).
                _filesChanged = false;
                var soon = DateTime.UtcNow.AddSeconds(2);
                if (soon < _nextScan)
                {
                    _nextScan = soon;
                }
            }
        }

        _watcher?.Dispose();
        Publish();
        _server?.Dispose();
        NativeTray.Stop();
        try { File.Delete(UrlFile(_store)); } catch { }

        if (_restart && (_restartExe ?? AppInfo.ExePath) is { } newExe)
        {
            try { _instance.ReleaseMutex(); } catch { }
            var start = new ProcessStartInfo(newExe) { UseShellExecute = false };
            foreach (var a in _args.Where(a => a != "--after-update").Append("--after-update"))
            {
                start.ArgumentList.Add(a);
            }
            Process.Start(start)?.Dispose();
        }
        return 0;
    }

    // ---- Main loop pieces -------------------------------------------------------------------------

    /// <summary>Runs an action on the main loop (from a web request, the tray or the overlay thread).</summary>
    private void Post(Action action)
    {
        _posted.Enqueue(action);
        _wake.Set();
    }

    private void RunPosted()
    {
        while (_posted.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Command failed", ex);
                Activity.Error(ex.Message);
            }
        }
    }

    /// <summary>What the first launch used to do before showing the main screen (once the setup is done).</summary>
    private void StartRuntime()
    {
        if (_config.ShowOverlay)
        {
            StartOverlay();
        }
        try { Installer.RefreshRegistration(); } catch (Exception ex) { ErrorLog.Write("Installed apps entry", ex); } // version shown after an update
        if (_config.StartWithFortnite)
        {
            FortniteWatch.Apply(true, _dataDir); // follows the exe if it was moved or updated, and restarts the standby
        }
        Updater.CleanupOld(); // after the two above: nothing points to an old file any more
        UpdateOverlay();
        _hasReplays = _tracker.ReplayFiles().Any();
        _nextScan = DateTime.UtcNow; // replays left from earlier: read them right away
    }

    private void ScanOnce()
    {
        try
        {
            var added = _tracker.Scan();
            if (_tracker.NewProfilesFound)
            {
                _tracker.NewProfilesFound = false;
                _profileChoiceNeeded = true;
            }
            var liveChanged = _tracker.UpdateLive();
            if (added > 0 || liveChanged)
            {
                UpdateOverlay();
            }
            _hasReplays = added > 0 || _tracker.ReplayFiles().Any();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Watching the replays folder", ex);
            Activity.Error(L.T($"Erreur pendant la surveillance : {ex.Message}", $"Error while watching: {ex.Message}", $"Error durante la vigilancia: {ex.Message}"));
        }
        // A replay being written: check again soon. Otherwise only a safety scan once a minute.
        _nextScan = DateTime.UtcNow.AddSeconds(_tracker.IsFinishing ? 2 : _tracker.HasPending ? 15 : (_watcher != null && !_auto ? 60 : 10));
    }

    private void CreateWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            if (Directory.Exists(_config.DemosFolder))
            {
                _watcher = new FileSystemWatcher(_config.DemosFolder, "*.replay")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += (_, _) => FilesChanged();
                _watcher.Changed += (_, _) => FilesChanged();
                _watcher.Renamed += (_, _) => FilesChanged();
            }
        }
        catch
        {
            _watcher = null; // fall back to the periodic scan
        }
    }

    private void FilesChanged()
    {
        _filesChanged = true;
        _wake.Set();
    }

    /// <summary>Opened with Fortnite: closes once Fortnite is closed and the last match counted (30 s to change your mind).</summary>
    private void CheckAutoClose()
    {
        var now = DateTime.UtcNow;
        if (FortniteWatch.IsFortniteRunning())
        {
            _fortniteClosedSince = null;
            _autoCloseAt = null; // Fortnite started again
            return;
        }
        _fortniteClosedSince ??= now;
        // Wait for the last match to be counted (at most 5 minutes, in case a replay was left unfinished by a crash).
        var lastMatchDone = !_tracker.IsLive && !_tracker.HasPending && !_tracker.IsFinishing;
        if (!lastMatchDone && now - _fortniteClosedSince < TimeSpan.FromMinutes(5))
        {
            return;
        }
        if (_autoCloseAt == null)
        {
            _autoCloseAt = now.AddSeconds(30);
            Activity.Warning(L.T("Fortnite est fermé : le compteur se ferme dans 30 secondes.", "Fortnite is closed: the counter closes in 30 seconds.",
                "Fortnite está cerrado: el contador se cierra en 30 segundos."));
            _nextScan = _autoCloseAt.Value;
        }
        else if (now >= _autoCloseAt)
        {
            _quit = true;
        }
    }

    private void KeepOpen()
    {
        _autoCloseAt = null;
        _auto = false;
        Activity.Success(L.T("Le compteur reste ouvert.", "The counter stays open.", "El contador sigue abierto."));
    }

    // ---- Overlay --------------------------------------------------------------------------------

    private void StartOverlay()
    {
        if (NativeOverlay.IsRunning)
        {
            NativeOverlay.SetVisible(true);
            return;
        }
        NativeOverlay.SetAppearance(_config.OverlaySize, _config.OverlayOpacity);
        NativeOverlay.Start(_config.OverlayX, _config.OverlayY, _config.OverlayLocked,
            (x, y) => Post(() =>
            {
                _config.OverlayX = x;
                _config.OverlayY = y;
                SaveConfig();
            }),
            locked => Post(() =>
            {
                _config.OverlayLocked = locked;
                SaveConfig();
            }));
        UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        var data = Profiles.View(_data, _config);
        var estimated = _config.Estimate is { Include: true } ? Estimates.Compute(_config.Estimate, data.Matches)?.Estimated ?? 0 : 0;
        var view = HudModel.Build(data, _config, _tracker.LiveMatch, _tracker.IsLive, estimated);
        _hud = view;
        ObsPage.Write(_store, view);
        if (NativeOverlay.IsRunning)
        {
            NativeOverlay.Update(view);
            // "hide while playing": hidden when a match starts, back in the lobby (only if it was hidden by this)
            var playing = view.Mode != HudMode.Idle;
            if (_config.OverlayHideInMatch && playing && NativeOverlay.IsVisible)
            {
                NativeOverlay.SetVisible(false);
                _hiddenForMatch = true;
            }
            else if (_hiddenForMatch && (!playing || !_config.OverlayHideInMatch))
            {
                NativeOverlay.SetVisible(true);
                _hiddenForMatch = false;
            }
        }
    }

    private bool _hiddenForMatch;

    /// <summary>Adds the matches of a history file (export of this app, from another PC) that are not known yet.</summary>
    private Task<CommandResult> ImportHistoryAsync(string json)
    {
        var done = new TaskCompletionSource<CommandResult>();
        Post(() =>
        {
            try
            {
                var imported = Store.ParseData(json);
                if (imported == null)
                {
                    done.SetResult(CommandResult.Fail(L.T("ce fichier n'est pas un historique Chest Tracker", "this file is not a Chest Tracker history", "este archivo no es un historial de Chest Tracker")));
                    return;
                }
                static string KeyOf(MatchRecord m) => !string.IsNullOrEmpty(m.SessionId) ? m.SessionId : $"{m.ReplayFile}|{m.MatchStartUtc:o}";
                var known = _data.Matches.Select(KeyOf).ToHashSet();
                var added = imported.Matches.Where(m => known.Add(KeyOf(m))).ToList();
                _data.Matches.AddRange(added);
                foreach (var (file, value) in imported.ProcessedFiles)
                {
                    _data.ProcessedFiles.TryAdd(file, value);
                }
                _store.SaveData(_data);
                UpdateOverlay();
                var what = L.Count(added.Count, "partie ajoutée", "parties ajoutées", "match added", "matches added", "partida añadida", "partidas añadidas");
                Activity.Success(L.T($"Historique importé : {what}", $"History imported: {what}", $"Historial importado: {what}"));
                done.SetResult(CommandResult.Done);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Importing a history", ex);
                done.SetResult(CommandResult.Fail(ex.Message));
            }
        });
        return done.Task;
    }

    // Latest overlay view, also drawn for the page (/hud.png) and the OBS file.
    private volatile HudView _hud = HudView.Empty;
    private sealed record HudPicture(string Key, byte[] Png);
    private volatile HudPicture? _hudPng;
    private CancellationTokenSource? _archiveCopy; // bulk copy of the existing replays to the archive

    /// <summary>The overlay as a PNG for the page (drawn at 2× for sharp screens), cached until it changes.</summary>
    private byte[] HudPng()
    {
        var view = _hud;
        var key = view.Key;
        if (_hudPng is { } cached && cached.Key == key)
        {
            return cached.Png;
        }
        var png = HudRenderer.RenderPng(view, 2f);
        _hudPng = new HudPicture(key, png);
        return png;
    }

    // ---- Updates ----------------------------------------------------------------------------------

    private void StartUpdateCheck(bool manual)
    {
        if (!AppInfo.UpdatesAvailable || _config.UpdateMode == "off" || _checking)
        {
            return;
        }
        _checking = true;
        _ = Task.Run(async () =>
        {
            var (found, reached) = await Updater.CheckDetailedAsync();
            if (found != null)
            {
                _update = found;
            }
            _lastCheck = found != null ? null : reached ? "upToDate" : "unreachable";
            _checking = false;
            if (manual && found == null)
            {
                Activity.Muted(reached
                    ? L.T($"Tu as déjà la dernière version ({AppInfo.VersionText}).", $"You already have the latest version ({AppInfo.VersionText}).", $"Ya tienes la última versión ({AppInfo.VersionText}).")
                    : L.T("GitHub est injoignable pour l'instant : réessaie plus tard.", "GitHub cannot be reached right now: try again later.", "No se puede acceder a GitHub ahora mismo: inténtalo más tarde."));
            }
            _wake.Set();
        });
    }

    private void AnnounceUpdate()
    {
        var pending = _update;
        if (pending == null || _updateAnnounced)
        {
            return;
        }
        var idle = !_tracker.IsLive && !_tracker.HasPending && !_tracker.IsFinishing;
        if (_config.UpdateMode == "auto")
        {
            if (idle && AppInfo.ExePath != null)
            {
                _updateAnnounced = true;
                InstallUpdate(pending);
            }
            return;
        }
        _updateAnnounced = true;
        Activity.Success(L.T($"Nouvelle version {pending.Version.ToString(3)} disponible (tu as la {AppInfo.VersionText}).",
            $"New version {pending.Version.ToString(3)} available (you have {AppInfo.VersionText}).",
            $"Nueva versión {pending.Version.ToString(3)} disponible (tienes la {AppInfo.VersionText})."));
    }

    private CommandResult InstallUpdate(UpdateInfo u)
    {
        _installing = true;
        Activity.Info(L.T($"Téléchargement de la version {u.Version.ToString(3)}…", $"Downloading version {u.Version.ToString(3)}…", $"Descargando la versión {u.Version.ToString(3)}…"));
        Publish();
        var error = Updater.Install(u);
        if (error != null)
        {
            _installing = false;
            Activity.Error(L.T($"Mise à jour impossible : {error}. Tu peux la télécharger à la main : {u.PageUrl}",
                $"Update failed: {error}. You can download it manually: {u.PageUrl}",
                $"No se pudo actualizar: {error}. Puedes descargarla a mano: {u.PageUrl}"));
            return CommandResult.Fail(error);
        }
        Activity.Success(L.T("Mise à jour installée : redémarrage du compteur…", "Update installed: restarting the counter…", "Actualización instalada: reiniciando el contador…"));
        _restart = true;
        _quit = true;
        return CommandResult.Done;
    }

    // ---- Commands from the web page ----------------------------------------------------------------

    /// <summary>Called on a web request thread. Thread-safe commands run here; the others on the main loop.</summary>
    private async Task<CommandResult> RunCommandAsync(string name, JsonElement args)
    {
        switch (name)
        {
            case "open" when Str(args, "what") != "fortnitegg": // fortnite.gg needs the profile: main loop
                return Open(Str(args, "what"));
            case "overlay" when NativeOverlay.IsRunning:
                if (Bool(args, "visible") is { } visible) NativeOverlay.SetVisible(visible);
                if (Bool(args, "locked") is { } locked) NativeOverlay.SetLocked(locked);
                return CommandResult.Done;
        }

        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                var result = Execute(name, args);
                Publish(); // the page gets the new state before the answer: no flash of the old one
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                ErrorLog.Write($"Command {name}", ex);
                tcs.TrySetResult(CommandResult.Fail(ex.Message));
            }
        });
        try
        {
            // The main loop may be busy reading many replays: the command runs right after, the page follows the state.
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            return new CommandResult(true, Data: "queued");
        }
    }

    /// <summary>Runs a command on the main loop.</summary>
    private CommandResult Execute(string name, JsonElement args)
    {
        switch (name)
        {
            case "setup/language":
                if (L.Normalize(Str(args, "language")) is not { } code)
                {
                    return CommandResult.Fail("language");
                }
                _config.Language = code;
                L.Set(code);
                return CommandResult.Done;

            case "setup/check-folder":
            {
                var path = Str(args, "path")?.Trim() ?? "";
                var exists = path.Length > 0 && Directory.Exists(path);
                return new CommandResult(true, Data: new { exists, replays = exists ? ExistingReplays(path).Count : 0 });
            }

            case "setup/finish":
                return FinishSetup(args);

            case "settings":
                return ApplySettings(args);

            case "profile":
            {
                var key = Str(args, "key");
                var profile = Profiles.List(_data).FirstOrDefault(p => p.Key == key);
                if (profile == null)
                {
                    return CommandResult.Fail("profile");
                }
                _config.Profile = profile.Key;
                _profileChoiceNeeded = false;
                SaveConfig();
                UpdateOverlay();
                Activity.Info(L.T($"Profil affiché : {profile.Name}", $"Profile shown: {profile.Name}", $"Perfil mostrado: {profile.Name}"));
                return CommandResult.Done;
            }

            case "overlay":
                // Not running yet (turned off at startup): showing it starts it.
                if (Bool(args, "visible") == true)
                {
                    StartOverlay();
                    if (Bool(args, "locked") is { } locked) NativeOverlay.SetLocked(locked);
                }
                return CommandResult.Done;

            case "scan":
                _nextScan = DateTime.UtcNow;
                return CommandResult.Done;

            case "update/check":
                if (!AppInfo.UpdatesAvailable)
                {
                    return CommandResult.Fail(L.T("version compilée localement", "local build", "versión compilada localmente"));
                }
                if (_config.UpdateMode == "off")
                {
                    return CommandResult.Fail(L.T("mises à jour désactivées", "updates are off", "actualizaciones desactivadas"));
                }
                StartUpdateCheck(manual: true);
                return CommandResult.Done;

            case "update/install":
                return _update is { } u ? InstallUpdate(u) : CommandResult.Fail(L.T("aucune nouvelle version", "no new version", "ninguna versión nueva"));

            case "goal":
            {
                var key = Str(args, "key");
                if (!HudModel.IsGoal(key))
                {
                    return CommandResult.Fail("goal");
                }
                if (_config.Goal != key)
                {
                    _config.Goal = key;
                    SaveConfig();
                    UpdateOverlay();
                }
                return CommandResult.Done;
            }

            case "prize":
            {
                var id = Str(args, "id");
                var known = !string.IsNullOrEmpty(id) && id.Length <= 200
                    && (TreasureNames.PrizeNames.ContainsKey(id)
                        || _data.Matches.Any(m => m.TreasureFinds.Any(f => f.PrizeId == id || f.RawItem == id)));
                if (!known)
                {
                    return CommandResult.Fail("prize");
                }
                var prizeName = (Str(args, "name") ?? "").Trim();
                _store.SetPrizeName(id, prizeName.Length > 200 ? prizeName[..200] : prizeName);
                return CommandResult.Done;
            }

            case "open":
                return Open(Str(args, "what"));

            case "token/activate":
                _config.TokenActivatedUtc = DateTime.UtcNow;
                SaveConfig();
                return CommandResult.Done;

            case "token/reset":
                _config.TokenActivatedUtc = null;
                SaveConfig();
                return CommandResult.Done;

            case "season/notify":
                _config.NotifyTreasureHours = Bool(args, "on") ?? !_config.NotifyTreasureHours;
                SaveConfig();
                return CommandResult.Done;

            case "estimate/save-player":
                if (Str(args, "player")?.Trim() is { Length: > 0 } who)
                {
                    (_config.Estimate ??= new EstimateSettings { Include = true }).Player = who;
                    SaveConfig();
                }
                return CommandResult.Done;

            case "estimate/save":
                return SaveEstimate(args);

            case "estimate/fetch":
                return FetchEstimate(args);

            case "estimate/clear":
                _config.Estimate = null;
                _estimateError = null;
                SaveConfig();
                UpdateOverlay();
                return CommandResult.Done;

            case "keep-open":
                KeepOpen();
                return CommandResult.Done;

            case "install":
                if (AppInfo.ExePath == null)
                {
                    return CommandResult.Fail(OnlyFromExe);
                }
                return InstallAndRestart() is { } installError ? CommandResult.Fail(installError) : CommandResult.Done;

            case "uninstall":
            {
                if (!Installer.InstalledExeExists)
                {
                    return CommandResult.Fail(L.T("pas installé sur ce PC", "not installed on this PC", "no instalado en este PC"));
                }
                // The installed exe removes itself once this counter has closed (it was already confirmed in the page).
                var start = new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
                foreach (var a in new[] { "--uninstall", "--confirmed", "--data", Path.GetFullPath(_dataDir) })
                {
                    start.ArgumentList.Add(a);
                }
                Process.Start(start)?.Dispose();
                Activity.Muted(L.T("Désinstallation…", "Uninstalling…", "Desinstalando…"));
                _quit = true;
                return CommandResult.Done;
            }

            case "quit":
                Activity.Muted(L.T("Fermeture du compteur…", "Closing the counter…", "Cerrando el contador…"));
                _quit = true;
                return CommandResult.Done;

            default:
                return CommandResult.Fail("unknown command");
        }
    }

    // ---- Estimate of the matches without replay ------------------------------------------------------

    /// <summary>Epic name used for fortnite.gg / fortnite-api.com: the one typed in, else the current profile's.</summary>
    private string EstimatePlayer() =>
        _config.Estimate?.Player is { Length: > 0 } p ? p : Profiles.Current(_data, _config)?.Name ?? "";

    private CommandResult SaveEstimate(JsonElement args)
    {
        var matches = new Dictionary<string, int>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("matches", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in Estimates.Buckets.Append("total"))
            {
                if (Int(m, key) is { } v && v > 0 && v < 1_000_000)
                {
                    matches[key] = v;
                }
            }
        }
        if (matches.Count == 0)
        {
            return CommandResult.Fail(L.T("entre au moins un nombre de parties", "enter at least one match count", "introduce al menos un número de partidas"));
        }
        var e = _config.Estimate ??= new EstimateSettings { Include = true };
        e.Source = "manual";
        e.Matches = matches;
        e.Player = Str(args, "player")?.Trim() is { Length: > 0 } p ? p : e.Player;
        e.UpdatedUtc = DateTime.UtcNow;
        _estimateError = null;
        SaveConfig();
        UpdateOverlay();
        LogEstimate();
        return CommandResult.Done;
    }

    /// <summary>Asks fortnite-api.com in the background (the main loop keeps running), then saves the numbers.</summary>
    private CommandResult FetchEstimate(JsonElement args)
    {
        var e = _config.Estimate ??= new EstimateSettings { Include = true };
        if (Str(args, "apiKey")?.Trim() is { Length: > 0 } key)
        {
            if (!Guid.TryParse(key, out _))
            {
                return CommandResult.Fail(L.T("une clé fortnite-api.com a la forme 01234567-89ab-cdef-0123-456789abcdef", "a fortnite-api.com key looks like 01234567-89ab-cdef-0123-456789abcdef", "una clave de fortnite-api.com tiene la forma 01234567-89ab-cdef-0123-456789abcdef"));
            }
            e.ApiKey = key;
        }
        if (Str(args, "player")?.Trim() is { Length: > 0 } p)
        {
            e.Player = p;
        }
        SaveConfig();
        if (string.IsNullOrEmpty(e.ApiKey))
        {
            return CommandResult.Fail(L.T("colle d'abord ta clé fortnite-api.com", "paste your fortnite-api.com key first", "pega primero tu clave de fortnite-api.com"));
        }
        var player = EstimatePlayer();
        if (player.Length == 0)
        {
            return CommandResult.Fail(L.T("indique ton pseudo Epic", "enter your Epic name", "indica tu nombre de Epic"));
        }
        if (_estimateFetching)
        {
            return CommandResult.Done;
        }
        _estimateFetching = true;
        _estimateError = null;
        var apiKey = e.ApiKey;
        _ = Task.Run(async () =>
        {
            var (matches, error) = await Estimates.FetchAsync(player, apiKey);
            Post(() =>
            {
                _estimateFetching = false;
                if (matches == null)
                {
                    _estimateError = error;
                    Activity.Error(L.T($"Estimation : {error}", $"Estimate: {error}", $"Estimación: {error}"));
                    return;
                }
                var settings = _config.Estimate ??= new EstimateSettings { Include = true };
                settings.Source = "api";
                settings.Matches = matches;
                settings.Player = player;
                settings.UpdatedUtc = DateTime.UtcNow;
                SaveConfig();
                UpdateOverlay();
                LogEstimate();
            });
        });
        return CommandResult.Done;
    }

    private void LogEstimate()
    {
        if (Estimates.Compute(_config.Estimate, Profiles.View(_data, _config).Matches) is { } r)
        {
            Activity.Success(L.T($"Estimation : {L.N(r.Untracked)} parties sans replay ≈ {L.N(r.Estimated)} coffres (entre {L.N(r.Low)} et {L.N(r.High)})",
                $"Estimate: {L.N(r.Untracked)} matches without replay ≈ {L.N(r.Estimated)} chests (between {L.N(r.Low)} and {L.N(r.High)})",
                $"Estimación: {L.N(r.Untracked)} partidas sin repetición ≈ {L.N(r.Estimated)} cofres (entre {L.N(r.Low)} y {L.N(r.High)})"));
        }
    }

    private static string ModeName(string bucket) => bucket switch
    {
        "solo" => "Solo",
        "duo" => L.T("Duo", "Duos", "Dúos"),
        "trio" => L.T("Trio", "Trios", "Tríos"),
        "squad" => L.T("Section", "Squads", "Escuadrones"),
        "total" => L.T("Tous les modes", "All modes", "Todos los modos"),
        _ => L.T("Autres modes", "Other modes", "Otros modos"),
    };

    private UiEstimate EstimateState(TrackerData view)
    {
        var e = _config.Estimate;
        var r = Estimates.Compute(e, view.Matches);
        var player = EstimatePlayer();
        var real = view.Matches.Sum(m => m.ChestsOpened);
        return new UiEstimate(
            r != null, e?.Source ?? "manual", player.Length > 0 ? player : null, e?.UpdatedUtc, e?.Include ?? false,
            !string.IsNullOrEmpty(e?.ApiKey), _estimateFetching, _estimateError,
            r?.Played, view.Matches.Count, r?.Untracked, r?.Estimated, r?.Low, r?.High, r != null ? real + r.Estimated : null,
            r?.Parts.Select(p => new UiEstimatePart(p.Bucket, ModeName(p.Bucket), p.Played, p.Tracked, p.Untracked, p.Average, p.Estimated)).ToList()
                ?? new List<UiEstimatePart>(),
            e?.Matches ?? new Dictionary<string, int>(),
            player.Length > 0 ? Estimates.FortniteGgUrl(player) : null);
    }


    private List<FileInfo> ExistingReplays(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*.replay", SearchOption.TopDirectoryOnly)
                .Where(f => !_data.ProcessedFiles.ContainsKey(Tracker.FileKey(f)) && ReplayHeaderProbe.IsLive(f.FullName) != true)
                .ToList();
        }
        catch
        {
            return new List<FileInfo>();
        }
    }

    private CommandResult FinishSetup(JsonElement args)
    {
        if (L.Normalize(Str(args, "language")) is { } code)
        {
            _config.Language = code;
            L.Set(code);
        }
        var folder = Str(args, "demosFolder")?.Trim();
        if (!string.IsNullOrEmpty(folder))
        {
            _config.DemosFolder = folder;
        }
        SaveConfig();

        if (Bool(args, "analyzeExisting") == false)
        {
            _tracker.MarkSkipped(ExistingReplays(_config.DemosFolder));
        }
        var withFortnite = Bool(args, "startWithFortnite") == true && AppInfo.ExePath != null;
        if (Bool(args, "install") == true && AppInfo.ExePath != null && !Installer.IsInstalled)
        {
            if (withFortnite)
            {
                // Turned on by the installed copy when it starts (StartRuntime): the logon entry then points to it.
                _config.StartWithFortnite = true;
                SaveConfig();
            }
            if (InstallAndRestart() == null)
            {
                _setupPending = false;
                return CommandResult.Done;
            }
            // Could not install: the counter carries on from where it is.
        }
        if (withFortnite)
        {
            _config.StartWithFortnite = FortniteWatch.Apply(true, _dataDir);
            SaveConfig();
        }

        _setupPending = false;
        CreateWatcher();
        StartRuntime();
        StartUpdateCheck(manual: false);
        Activity.Success(L.T("C'est prêt : le compteur tourne. Les coffres de chaque partie sont comptés quelques secondes après sa fin.",
            "All set: the counter is running. The chests of each match are counted a few seconds after it ends.",
            "Todo listo: el contador está en marcha. Los cofres de cada partida se cuentan unos segundos después de que termine."));
        return CommandResult.Done;
    }

    /// <summary>
    /// Copies this exe to the install folder (Start menu, desktop, "Installed apps") and restarts from there, like
    /// after an update. Returns null when done, else the reason it failed.
    /// </summary>
    private string? InstallAndRestart()
    {
        Activity.Info(L.T("Installation sur ce PC…", "Installing on this PC…", "Instalando en este PC…"));
        var error = Installer.Install();
        if (error != null)
        {
            Activity.Error(L.T($"Installation impossible : {error}", $"Could not install: {error}", $"No se pudo instalar: {error}"));
            return error;
        }
        Activity.Success(L.T("Installé sur ce PC (menu Démarrer et bureau) : redémarrage depuis la copie installée… Tu peux supprimer le fichier téléchargé.",
            "Installed on this PC (Start menu and desktop): restarting from the installed copy… You can delete the downloaded file.",
            "Instalado en este PC (menú Inicio y escritorio): reiniciando desde la copia instalada… Puedes borrar el archivo descargado."));
        _restartExe = Installer.InstalledExe;
        _restart = true;
        _quit = true;
        return null;
    }

    private CommandResult ApplySettings(JsonElement args)
    {
        if (L.Normalize(Str(args, "language")) is { } code && code != _config.Language)
        {
            _config.Language = code;
            L.Set(code);
            UpdateOverlay();
        }
        if (Bool(args, "openUiOnStart") is { } openUi)
        {
            _config.OpenUiOnStart = openUi;
        }
        if (Bool(args, "startWithFortnite") is { } withFortnite && withFortnite != _config.StartWithFortnite)
        {
            if (!FortniteWatch.Apply(withFortnite, _dataDir))
            {
                return CommandResult.Fail(OnlyFromExe);
            }
            _config.StartWithFortnite = withFortnite;
        }
        if (Bool(args, "showOverlay") is { } showOverlay)
        {
            _config.ShowOverlay = showOverlay;
            if (showOverlay)
            {
                StartOverlay();
            }
            else if (NativeOverlay.IsRunning)
            {
                NativeOverlay.SetVisible(false);
            }
        }
        if (Str(args, "updateMode") is "prompt" or "auto" or "off" && Str(args, "updateMode") is { } mode && AppInfo.UpdatesAvailable)
        {
            var wasOff = _config.UpdateMode == "off";
            _config.UpdateMode = mode;
            if (wasOff && mode != "off")
            {
                StartUpdateCheck(manual: false);
            }
        }
        if (Bool(args, "includeEstimate") is { } include && _config.Estimate != null)
        {
            _config.Estimate.Include = include;
            UpdateOverlay();
        }
        if (Int(args, "overlaySize") is { } size)
        {
            _config.OverlaySize = OverlayAppearance.ClampSize(size);
            NativeOverlay.SetAppearance(_config.OverlaySize, _config.OverlayOpacity);
        }
        if (Str(args, "overlayStyle") is OverlayAppearance.Full or OverlayAppearance.Compact && Str(args, "overlayStyle") is { } style)
        {
            _config.OverlayStyle = style;
            UpdateOverlay();
        }
        if (Bool(args, "hideInMatch") is { } hideInMatch)
        {
            _config.OverlayHideInMatch = hideInMatch;
            UpdateOverlay();
        }
        if (Bool(args, "scene3d") is { } scene3d)
        {
            _config.Scene3D = scene3d;
        }
        var parts = false;
        if (Bool(args, "partGoal") is { } partGoal) { _config.OverlayGoal = partGoal; parts = true; }
        if (Bool(args, "partStats") is { } partStats) { _config.OverlayStats = partStats; parts = true; }
        if (Bool(args, "partLast") is { } partLast) { _config.OverlayLast = partLast; parts = true; }
        if (Bool(args, "partSite") is { } partSite) { _config.OverlaySite = partSite; parts = true; }
        if (parts)
        {
            UpdateOverlay();
        }
        if (Int(args, "overlayCorner") is { } corner and >= 0 and <= 3)
        {
            NativeOverlay.SnapTo(corner);
        }
        if (Int(args, "overlayOpacity") is { } opacity)
        {
            _config.OverlayOpacity = OverlayAppearance.ClampOpacity(opacity);
            NativeOverlay.SetAppearance(_config.OverlaySize, _config.OverlayOpacity);
        }
        if (Str(args, "archiveFolder")?.Trim() is { } archive)
        {
            // "" = off, "*" = default folder (Documents), else a full path chosen by the player.
            archive = archive == "*" ? Tracker.DefaultArchiveFolder : archive;
            if (archive.Length > 0 && !Path.IsPathFullyQualified(archive))
            {
                return CommandResult.Fail(L.T("chemin complet attendu (ex. D:\\Replays)", "full path expected (e.g. D:\\Replays)", "se espera una ruta completa (p. ej. D:\\Replays)"));
            }
            if (archive != _config.ArchiveFolder)
            {
                _config.ArchiveFolder = archive;
                if (archive.Length > 0)
                {
                    // The replays already counted are copied too, in the background (it can be a few GB).
                    var files = _tracker.ArchivableFiles();
                    Activity.Info(L.T($"Archive des replays : {archive}", $"Replay archive: {archive}", $"Archivo de repeticiones: {archive}"));
                    _archiveCopy?.Cancel();
                    var cancel = _archiveCopy = new CancellationTokenSource();
                    _ = Task.Run(() =>
                    {
                        var copied = 0;
                        try
                        {
                            foreach (var f in files)
                            {
                                if (cancel.IsCancellationRequested)
                                {
                                    break; // another folder was chosen meanwhile
                                }
                                if (Tracker.CopyToArchive(archive, f, report: false))
                                {
                                    copied++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Activity.Error(L.T($"Archive des replays : {ex.Message}", $"Replay archive: {ex.Message}", $"Archivo de repeticiones: {ex.Message}"));
                        }
                        if (copied > 0)
                        {
                            Activity.Success(L.T($"{copied} replays copiés dans l'archive.", $"{copied} replays copied to the archive.", $"{copied} repeticiones copiadas al archivo."));
                        }
                        _wake.Set();
                    });
                }
            }
        }
        if (Str(args, "demosFolder")?.Trim() is { Length: > 0 } demos && demos != _config.DemosFolder)
        {
            _config.DemosFolder = demos;
            CreateWatcher();
            _hasReplays = _tracker.ReplayFiles().Any();
            _nextScan = DateTime.UtcNow;
            Activity.Info(L.T($"Dossier des replays : {demos}", $"Replays folder: {demos}", $"Carpeta de repeticiones: {demos}"));
        }
        SaveConfig();
        return CommandResult.Done;
    }

    private CommandResult Open(string? what)
    {
        string? target = what switch
        {
            "data" => _store.Folder,
            "demos" => Directory.Exists(_config.DemosFolder) ? _config.DemosFolder : null,
            "archive" => Directory.Exists(_config.ArchiveFolder) ? _config.ArchiveFolder : null,
            "obs" => _store.OverlayPath,
            "errors" => File.Exists(_store.ErrorLogPath) ? _store.ErrorLogPath : null,
            "repo" => AppInfo.RepoUrl,
            "issues" => AppInfo.IssuesUrl,
            "author" => AppInfo.AuthorUrl,
            "release" => _update?.PageUrl,
            "fortnitegg" => EstimatePlayer() is { Length: > 0 } who ? Estimates.FortniteGgUrl(who) : "https://fortnite.gg/stats",
            "fortniteapi" => "https://dash.fortnite-api.com/account",
            _ => null,
        };
        if (target == null)
        {
            return CommandResult.Fail(what == "errors"
                ? L.T("aucune erreur enregistrée", "no error recorded", "ningún error registrado")
                : L.T("introuvable", "not found", "no encontrado"));
        }
        return OpenTarget(target) ? CommandResult.Done : CommandResult.Fail(target);
    }

    private void OpenUi()
    {
        if (_server != null)
        {
            OpenTarget(_server.Url);
        }
    }

    private static string OnlyFromExe => L.T("possible seulement depuis FortniteChestTracker.exe", "only possible from FortniteChestTracker.exe", "solo es posible desde FortniteChestTracker.exe");

    private static bool OpenTarget(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveConfig()
    {
        try
        {
            _store.SaveConfig(_config);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Saving config.json", ex);
        }
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    // ---- Tray --------------------------------------------------------------------------------------

    /// <summary>Built on the tray thread when the menu opens: only thread-safe reads here.</summary>
    private IReadOnlyList<TrayItem> TrayMenu()
    {
        var items = new List<TrayItem>
        {
            new(L.T("Ouvrir l'interface", "Open the interface", "Abrir la interfaz"), OpenUi, Default: true),
            TrayItem.Separator,
            new(L.T("Afficher l'overlay", "Show the overlay", "Mostrar el overlay"),
                () => { if (NativeOverlay.IsRunning) NativeOverlay.SetVisible(!NativeOverlay.IsVisible); else Post(StartOverlay); },
                Checked: NativeOverlay.IsVisible, Enabled: !_setupPending),
            new(L.T("Verrouiller l'overlay", "Lock the overlay", "Bloquear el overlay"), () => NativeOverlay.SetLocked(!NativeOverlay.IsLocked),
                Checked: NativeOverlay.IsLocked, Enabled: NativeOverlay.IsRunning),
            new(L.T("Chercher de nouveaux replays", "Look for new replays", "Buscar nuevas repeticiones"), () => Post(() => _nextScan = DateTime.UtcNow), Enabled: !_setupPending),
        };
        if (_autoCloseAt != null)
        {
            items.Add(new(L.T("Garder le compteur ouvert", "Keep the counter open", "Mantener el contador abierto"), () => Post(KeepOpen)));
        }
        items.Add(TrayItem.Separator);
        items.Add(new(L.T("Quitter", "Quit", "Salir"), () => Post(() => _quit = true)));
        return items;
    }

    private string Tooltip()
    {
        if (_setupPending || _data == null)
        {
            return "Fortnite - Chest Tracker";
        }
        var view = Profiles.View(_data, _config);
        var total = view.Matches.Sum(m => m.ChestsOpened);
        var tip = $"Fortnite - Chest Tracker · {L.Count(total, "coffre", "chest", "cofre")}";
        if (_tracker.IsLive)
        {
            tip += L.T(" · partie en cours", " · match in progress", " · partida en curso");
        }
        return tip;
    }

    // ---- State for the web page ---------------------------------------------------------------------

    /// <summary>Rebuilds what the page shows; when it changed, the open pages get it at once.</summary>
    private void Publish()
    {
        UiState state;
        try
        {
            AnnounceMilestones();
            state = BuildState();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Building the interface state", ex);
            return;
        }
        var json = JsonSerializer.Serialize(state, UiJson.Options);
        var current = _state;
        if (json != current.Json)
        {
            _state = new Snapshot(current.Version + 1, json);
            _server?.Notify();
        }
        NativeTray.SetTooltip(Tooltip());
    }

    /// <summary>
    /// Tells the player about the milestones crossed since the previous state (shown as a notification on the page, like a
    /// counted match). The first state, a profile switch and long batches (first analysis of the existing replays,
    /// re-analysis) only set the reference: a pile of old milestones is no news.
    /// </summary>
    private void AnnounceMilestones()
    {
        var profile = Profiles.Current(_data, _config)?.Key;
        var states = Milestones.Of(Profiles.View(_data, _config));
        var before = _milestones;
        _milestones = (profile, states);
        if (before is not { } b || b.Profile != profile || _setupPending || _tracker.Progress is { Total: > 1 })
        {
            return;
        }
        foreach (var crossed in Milestones.Crossed(b.States, states))
        {
            Activity.Success(Milestones.Announce(crossed));
        }
    }

    private UiState BuildState()
    {
        var view = Profiles.View(_data, _config);
        var current = Profiles.Current(_data, _config);
        var profiles = Profiles.List(_data).Select(p => new UiProfile(p.Key, p.Name, p.Matches, p.Chests, p.Key == current?.Key)).ToList();
        var progress = _tracker.Progress is { } p0 && p0.Total > 0 ? new UiProgress(p0.Done, p0.Total) : null;
        var status = _setupPending ? "setup"
            : progress != null ? "analyzing"
            : _tracker.IsLive ? "live"
            : _tracker.IsFinishing ? "finishing"
            : "idle";
        var demosExists = Directory.Exists(_config.DemosFolder);

        UiSetup? setup = null;
        if (_setupPending)
        {
            var defaultDemos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");
            setup = new UiSetup(_config.DemosFolder, demosExists, demosExists ? ExistingReplays(_config.DemosFolder).Count : 0, defaultDemos, AppInfo.ExePath != null,
                AppInfo.ExePath != null && !Installer.IsInstalled);
        }

        return new UiState(
            new UiApp(AppInfo.VersionText, L.Code, _store.Folder, _config.DemosFolder, demosExists, AppInfo.RepoUrl, AppInfo.IssuesUrl, _auto,
                _autoCloseAt is { } at ? Math.Max(0, (int)(at - DateTime.UtcNow).TotalSeconds) : null, true, AppInfo.SiteName),
            status,
            progress,
            setup,
            profiles,
            _profileChoiceNeeded && profiles.Count > 1,
            UiStateBuilder.Stats(view),
            new UiLive(_tracker.IsLive, _tracker.LiveMatch?.ChestsOpened, _tracker.LiveMatch?.TreasureFinds.Count),
            UiStateBuilder.PerDay(view, 90),
            UiStateBuilder.ByKind(view),
            view.Matches.Sum(m => m.Chests.Count(c => c.Destroyed)),
            UiStateBuilder.OtherInteractions(view),
            UiStateBuilder.ByMode(view),
            UiStateBuilder.Treasures(view),
            UiStateBuilder.Matches(view),
            UiStateBuilder.Prizes(),
            new UiOverlay(true, NativeOverlay.IsRunning, NativeOverlay.IsVisible, NativeOverlay.IsRunning ? NativeOverlay.IsLocked : _config.OverlayLocked,
                _config.OverlaySize, _config.OverlayOpacity,
                !NativeOverlay.IsRunning || NativeOverlay.HotkeyRegistered, !NativeOverlay.IsRunning || NativeOverlay.LockHotkeyRegistered, _store.OverlayPath,
                Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(_hud.Key)), 0, 6), _config.Goal,
                _config.OverlayStyle),
            new UiUpdate(AppInfo.UpdatesAvailable, _config.UpdateMode, _update?.Version.ToString(3), _update?.PageUrl, _checking, _lastCheck, _installing),
            new UiSettings(L.Code, _config.StartWithFortnite, AppInfo.ExePath != null, _config.UpdateMode, _config.ShowOverlay, _config.OverlaySize,
                _config.OverlayOpacity, _config.OpenUiOnStart, _config.DemosFolder, OverlayAppearance.Sizes, OverlayAppearance.Opacities,
                L.Codes.Select(c => new UiLanguage(c, L.NativeName(c))).ToList(), _config.ArchiveFolder, Tracker.ArchiveCount(_config.ArchiveFolder),
                Installer.IsInstalled, AppInfo.ExePath != null, !Installer.IsInstalled && AppInfo.ExePath != null && Installer.InstalledExeExists)
            {
                HideInMatch = _config.OverlayHideInMatch,
                Scene3D = _config.Scene3D,
                Parts = new Dictionary<string, bool> { ["goal"] = _config.OverlayGoal, ["stats"] = _config.OverlayStats, ["last"] = _config.OverlayLast, ["site"] = _config.OverlaySite },
            },
            Notices(demosExists, profiles.Count),
            UiStateBuilder.Activity(),
            EstimateState(view),
            UiStateBuilder.ByWeekday(view),
            HuntState(view),
            UiStateBuilder.ChestsVsPlacement(view),
            Milestones.Of(view).Select(m => new UiMilestone(m.Key, m.Value, m.Reached, m.Next)).ToList())
        {
            Season = SeasonState(view),
        };
    }

    private UiSeason SeasonState(TrackerData view)
    {
        var s = Season.Current;
        var since = HudModel.HuntSince(_config);
        var hunt = view.Matches.Where(m => Hunt.Counts(m, since)).ToList();
        var weeks = new List<UiSeasonWeek>();
        for (var i = 0; i < s.Weeks.Count; i++)
        {
            var (start, end) = Season.WeekBounds(i);
            var inWeek = hunt.Where(m => Hunt.StartOf(m) >= start && Hunt.StartOf(m) < end).ToList();
            var ids = inWeek.SelectMany(m => m.TreasureFinds).Select(f => f.PrizeId).Where(id => id != null).ToHashSet();
            var prizes = s.Weeks[i].Prizes.Select(p => new UiSeasonPrize(p.Key, p.Name, p.Img, p.Odds,
                p.Id != null && ids.Any(id => id!.EndsWith(":" + p.Id, StringComparison.Ordinal) || id == p.Id))).ToList();
            weeks.Add(new UiSeasonWeek(start, end, s.Weeks[i].Title, s.Weeks[i].Art, prizes, inWeek.Sum(m => m.ChestsOpened),
                inWeek.Sum(m => m.TreasureFinds.Count(f => f.FromGoldenChest))));
        }
        var hours = s.TreasureHours.Select(h => new UiBoostWindow(h.Start, h.End, h.Boosts, h.Confirmed)).ToList();
        return new UiSeason(s.Name, s.Start, s.End, Season.WeekAt(DateTime.UtcNow), weeks, hours, _config.TokenActivatedUtc, _config.NotifyTreasureHours);
    }

    // ---- Season: calendar refreshed from the website, a notice before each Treasure Hour ------------------

    private DateTime _nextSeasonFetch = DateTime.MinValue;
    private DateTime _notifiedHour = DateTime.MinValue;
    private int _seasonMinute = -1;

    /// <summary>Called by the main loop (at least once a minute).</summary>
    private void SeasonTick()
    {
        var now = DateTime.UtcNow;
        if (now >= _nextSeasonFetch && _config.UpdateMode != "off" && Season.IsOn(now.AddDays(-1)))
        {
            _nextSeasonFetch = now.AddHours(6);
            var folder = _store.Folder;
            _ = Task.Run(async () =>
            {
                if (await Season.RefreshAsync(folder))
                {
                    Post(UpdateOverlay);
                }
            });
        }
        if (_config.NotifyTreasureHours && Season.NextAfter(now) is { } next && next.Start - now <= TimeSpan.FromMinutes(10) && next.Start != _notifiedHour)
        {
            _notifiedHour = next.Start;
            var boost = next.BoostOf("odds_pirate");
            NativeTray.Notify(L.T("Heure du trésor dans 10 min", "Treasure Hour in 10 min", "Hora del tesoro en 10 min"),
                L.T($"Jeton pirate ×{L.N1(boost)} jusqu'à {next.End.ToLocalTime():HH:mm}. Bonne chasse !",
                    $"Pirate token ×{L.N1(boost)} until {next.End.ToLocalTime():HH:mm}. Happy hunting!",
                    $"Ficha pirata ×{L.N1(boost)} hasta las {next.End.ToLocalTime():HH:mm}. ¡Buena caza!"));
        }
        // the overlay shows the countdown to the next Treasure Hour: redrawn every minute
        if (now.Minute != _seasonMinute)
        {
            _seasonMinute = now.Minute;
            if (Season.IsOn(now))
            {
                UpdateOverlay();
            }
        }
    }

    private UiHunt HuntState(TrackerData view)
    {
        var since = HudModel.HuntSince(_config);
        var played = view.Matches.Where(m => Hunt.StartOf(m) >= since).ToList();
        // Ranked gives no treasure-hunt rewards: its chests stay in the totals but not in the chances
        var matches = played.Where(m => Hunt.Counts(m, since)).ToList();
        var ranked = played.Where(m => !matches.Contains(m)).Sum(m => m.ChestsOpened);
        var finds = matches.SelectMany(m => m.TreasureFinds).ToList();
        // Golden chest prizes carry their prize id: those are the weekly rewards; the rest are gizmos.
        var rewards = finds.Count(f => f.FromGoldenChest);
        var gizmos = finds.Count - rewards;
        var chests = matches.Sum(m => m.ChestsOpened);
        var gizmoOdds = TreasureOdds.All.First(o => o.Key == "odds_gizmo");
        var gizmosExpected = TreasureOdds.ExpectedFor(gizmoOdds, matches, since);
        var rewardsExpected = TreasureOdds.ExpectedRewardsFor(matches, since);
        var odds = TreasureOdds.All.Select(o => new UiOdds(o.Key, o.Odds, o.HourBoost, TreasureOdds.ExpectedFor(o, matches, since))
        {
            Weighted = Hunt.Weighted(matches, o.Key),
        }).ToList();
        var boosted = Hunt.DuringTreasureHours(matches);
        if (rewards == 0)
        {
            // Without a golden chest in the replays, a weekly reward cannot be told apart from a gizmo (both come as the
            // same generic treasure): one luck figure for all the treasures, against everything expected.
            var expected = gizmosExpected + rewardsExpected;
            return new UiHunt(since, chests, matches.Count, gizmos, rewards, odds, expected, 0,
                TreasureOdds.LuckPercent(finds.Count, expected), null, Combined: true) { RankedChests = ranked, BoostedChests = boosted };
        }
        return new UiHunt(since, chests, matches.Count, gizmos, rewards, odds,
            gizmosExpected, rewardsExpected, TreasureOdds.LuckPercent(gizmos, gizmosExpected), TreasureOdds.LuckPercent(rewards, rewardsExpected))
            { RankedChests = ranked, BoostedChests = boosted };
    }

    private List<UiNotice> Notices(bool demosExists, int profiles)
    {
        var list = new List<UiNotice>();
        if (_setupPending)
        {
            return list;
        }
        if (_autoCloseAt != null)
        {
            list.Add(new UiNotice("autoclose", "warning",
                L.T("Fortnite est fermé : le compteur se ferme dans quelques secondes.", "Fortnite is closed: the counter closes in a few seconds.", "Fortnite está cerrado: el contador se cierra en unos segundos."),
                L.T("Garder ouvert", "Keep it open", "Mantener abierto"), "keep-open"));
        }
        if (_update is { } u && _config.UpdateMode != "auto" && !_installing)
        {
            list.Add(new UiNotice("update", "success",
                L.T($"Nouvelle version {u.Version.ToString(3)} disponible (tu as la {AppInfo.VersionText}). Ton historique est conservé.",
                    $"New version {u.Version.ToString(3)} available (you have {AppInfo.VersionText}). Your history is kept.",
                    $"Nueva versión {u.Version.ToString(3)} disponible (tienes la {AppInfo.VersionText}). Tu historial se conserva."),
                L.T("Installer", "Install", "Instalar"), "update/install"));
        }
        if (_profileChoiceNeeded && profiles > 1)
        {
            list.Add(new UiNotice("profiles", "info",
                L.T("Plusieurs comptes Fortnite ont enregistré des replays sur ce PC : chacun a son profil. Choisis le tien en haut de la page.",
                    "Several Fortnite accounts recorded replays on this PC: each one has its own profile. Choose yours at the top of the page.",
                    "Varias cuentas de Fortnite grabaron repeticiones en este PC: cada una tiene su perfil. Elige el tuyo arriba de la página."),
                L.T("Choisir", "Choose", "Elegir"), "ui:profile"));
        }
        if (!demosExists)
        {
            list.Add(new UiNotice("demos", "warning",
                L.T("Le dossier des replays n'existe pas encore : Fortnite le crée à la première partie enregistrée. Vérifie que Paramètres → Jeu → Replays → Enregistrer les replays est activé.",
                    "The replays folder does not exist yet: Fortnite creates it with the first recorded match. Check that Settings → Game → Replays → Record Replays is on.",
                    "La carpeta de repeticiones aún no existe: Fortnite la crea con la primera partida grabada. Comprueba que Ajustes → Juego → Repeticiones → Grabar repeticiones está activado."),
                L.T("Réglages", "Settings", "Ajustes"), "ui:settings"));
        }
        else if (!_hasReplays)
        {
            list.Add(new UiNotice("noreplay", "info",
                L.T("Aucun replay pour l'instant : c'est normal si tu n'as pas encore joué depuis que les replays sont activés (Paramètres → Jeu → Replays).",
                    "No replay yet: that is expected if you have not played since replays were turned on (Settings → Game → Replays).",
                    "Aún no hay repeticiones: es normal si no has jugado desde que activaste las repeticiones (Ajustes → Juego → Repeticiones).")));
        }
        return list;
    }

    // ---- Misc ---------------------------------------------------------------------------------------

    /// <summary>Below-normal priority + Windows 11 "efficiency mode" (EcoQoS): the game always comes first.</summary>
    public static void LowerPriority()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        try { EcoQos.Enable(); } catch { }
    }

    /// <summary>A Windows message box (the tool has no console to show errors in).</summary>
    public static void Message(string text, bool error = false) =>
        MessageBoxW(IntPtr.Zero, text, "Fortnite - Chest Tracker", (error ? 0x10u : 0x40u) | 0x10000u); // MB_SETFOREGROUND

    /// <summary>A Yes / No question in a Windows message box. True = Yes.</summary>
    public static bool Ask(string text) =>
        MessageBoxW(IntPtr.Zero, text, "Fortnite - Chest Tracker", 0x4u | 0x20u | 0x10000u) == 6; // MB_YESNO, MB_ICONQUESTION, MB_SETFOREGROUND; IDYES

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
