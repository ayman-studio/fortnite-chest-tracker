namespace FortniteChestTracker;

/// <summary>
/// Watches the Fortnite Demos folder, analyzes every finished replay once and accumulates the results.
/// </summary>
public sealed class Tracker
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

    /// <summary>Replay finalized by Fortnite (no longer live, real key written): only a few quiet seconds are needed.</summary>
    private static readonly TimeSpan FinalizedQuietPeriod = TimeSpan.FromSeconds(4);

    /// <summary>A finished replay not modified for this long is complete for sure (replays left from earlier).</summary>
    private static readonly TimeSpan OldReplayAge = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Fortnite appends to the replay of a match in progress about every 25 s. Still flagged "live" but untouched for
    /// this long: the recording was cut (Fortnite crashed or was killed) and will never be finished.
    /// </summary>
    private static readonly TimeSpan AbandonedLiveAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromMinutes(3);

    private readonly Store _store;
    private readonly Config _config;
    private readonly TrackerData _data;
    private readonly Dictionary<string, long> _lastSeenSize = new();

    // Failures that may not happen again (timeout on a busy PC, file locked by an antivirus, low memory): retried a few
    // times, later and with more time, instead of marking the replay as unreadable for good.
    private const int MaxAttempts = 3;
    private readonly Dictionary<string, (int Attempts, DateTime RetryAfterUtc)> _retries = new();
    private static Thread? _stuckWorker; // a parser thread that ignored the cancellation (shares static hooks)

    private static bool IsTransient(Exception ex) => ex is TimeoutException or IOException or UnauthorizedAccessException or OutOfMemoryException;

    /// <summary>The previous parser thread is still running: the replay waits (it is not its fault).</summary>
    private sealed class ParserBusyException() : Exception(L.T("analyse précédente encore en cours : redémarre le compteur", "previous analysis still running: restart the counter", "análisis anterior aún en curso: reinicia el contador"));
    private bool _busyReported;

    public Tracker(Store store, Config config, TrackerData data)
    {
        _store = store;
        _config = config;
        _data = data;
    }

    public TrackerData Data => _data;

    /// <summary>Default archive folder: Documents\Fortnite Chest Tracker\Replays.</summary>
    public static string DefaultArchiveFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fortnite Chest Tracker", "Replays");

    /// <summary>Replays already counted that can be archived (finished, still in the replays folder).</summary>
    public List<FileInfo> ArchivableFiles() => ReplayFiles()
        .Where(f => _data.ProcessedFiles.TryGetValue(FileKey(f), out var v) && _data.Matches.Any(m => m.SessionId == v))
        .ToList();

    /// <summary>
    /// Copies a replay into the archive folder (same name; skipped when an identical-size copy is there). Never throws:
    /// the archive is a convenience, the count must go on.
    /// </summary>
    public static bool CopyToArchive(string? folder, FileInfo file, bool report = true)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }
        try
        {
            Directory.CreateDirectory(folder);
            var target = new FileInfo(Path.Combine(folder, file.Name));
            if (target.Exists && target.Length == file.Length)
            {
                return false;
            }
            File.Copy(file.FullName, target.FullName, overwrite: true);
            ArchiveChanged();
            return true;
        }
        catch (Exception ex)
        {
            if (!report)
            {
                throw;
            }
            Activity.Error(L.T($"Archive des replays : {ex.Message}", $"Replay archive: {ex.Message}", $"Archivo de repeticiones: {ex.Message}"));
            return false;
        }
    }

    private static (string? Folder, int Count, DateTime AtUtc) _archiveCount;

    /// <summary>Number of replays in the archive folder (0 when off or missing), listed at most every 30 s.</summary>
    public static int ArchiveCount(string? folder)
    {
        var cached = _archiveCount;
        if (cached.Folder == folder && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromSeconds(30))
        {
            return cached.Count;
        }
        var count = 0;
        try
        {
            count = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.replay").Count() : 0;
        }
        catch
        {
            // unreachable drive: 0
        }
        _archiveCount = (folder, count, DateTime.UtcNow);
        return count;
    }

    /// <summary>Forgets the cached count (after copies).</summary>
    public static void ArchiveChanged() => _archiveCount = default;

    /// <summary>True while a replay is waiting to be finished / to become stable.</summary>
    public bool HasPending { get; private set; }

    /// <summary>True when a match has just ended and its replay is about to be read: check again within seconds.</summary>
    public bool IsFinishing { get; private set; }

    /// <summary>Gives the memory used by the analysis back to Windows (the tool idles at a few MB).</summary>
    private static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    // During a batch (replays left from earlier), data.json is saved every few seconds instead of after every replay:
    // the whole history is rewritten each time. A crash in between only means a few replays are read again.
    private static readonly TimeSpan BatchSaveInterval = TimeSpan.FromSeconds(5);
    private bool _deferSaves;
    private bool _unsaved;
    private DateTime _lastSaveUtc = DateTime.MinValue;

    private void SaveData(bool force = false)
    {
        _unsaved = true;
        if (force || !_deferSaves || DateTime.UtcNow - _lastSaveUtc >= BatchSaveInterval)
        {
            _store.SaveData(_data);
            _unsaved = false;
            _lastSaveUtc = DateTime.UtcNow;
        }
    }

    public static string FileKey(FileInfo f) => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}";

    public IEnumerable<FileInfo> ReplayFiles()
    {
        var dir = new DirectoryInfo(_config.DemosFolder);
        if (!dir.Exists)
        {
            return Enumerable.Empty<FileInfo>();
        }
        return dir.EnumerateFiles("*.replay", SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc);
    }

    public IEnumerable<FileInfo> UnprocessedFiles() => ReplayFiles().Where(f => !_data.ProcessedFiles.ContainsKey(FileKey(f)));

    public void MarkSkipped(IEnumerable<FileInfo> files)
    {
        foreach (var f in files)
        {
            _data.ProcessedFiles[FileKey(f)] = "skipped";
        }
        _store.SaveData(_data);
    }

    /// <summary>Result of the analysis of one replay.</summary>
    private sealed record Outcome(MatchRecord? Match = null, bool Updated = false, string? Skipped = null, string? Error = null, List<string>? NewPrizeIds = null);

    /// <summary>Replays being analyzed right now (null when idle). Read by the web interface.</summary>
    public ScanProgress? Progress { get; private set; }

    /// <summary>Raised on the scanning thread before and after each replay: lets the interface follow a long batch.</summary>
    public event Action? Changed;

    /// <summary>
    /// One pass over the folder. Returns the number of new matches recorded. Several replays at once (replays left
    /// from earlier) are followed through <see cref="Progress"/> and summed up; a single one (end of a match) gets its own line.
    /// </summary>
    /// <param name="ignoreQuietPeriod">Process finished replays immediately (analysis of a whole folder in the tests).</param>
    public int Scan(bool ignoreQuietPeriod = false)
    {
        HasPending = false;
        IsFinishing = false;
        var ready = new List<FileInfo>();
        foreach (var file in UnprocessedFiles().ToList())
        {
            file.Refresh();
            if (!file.Exists)
            {
                continue;
            }
            if (!IsReady(file, ignoreQuietPeriod))
            {
                HasPending = true;
                continue;
            }
            if (_retries.TryGetValue(FileKey(file), out var retry) && DateTime.UtcNow < retry.RetryAfterUtc && !ignoreQuietPeriod)
            {
                HasPending = true; // tried a moment ago: later
                continue;
            }
            ready.Add(file);
        }
        if (ready.Count == 0)
        {
            return 0;
        }

        var batch = ready.Count >= 2;
        var profilesBefore = Profiles.List(_data).Count;
        if (batch)
        {
            Activity.Info(L.T($"Analyse de {ready.Count} replays…", $"Analyzing {ready.Count} replays…", $"Analizando {ready.Count} repeticiones…"));
        }
        var outcomes = new List<Outcome>();
        _deferSaves = batch;
        try
        {
            for (var i = 0; i < ready.Count; i++)
            {
                var file = ready[i];
                Progress = new ScanProgress(i, ready.Count);
                Changed?.Invoke();
                if (!batch)
                {
                    var mb = (file.Length / 1024 / 1024.0).ToString("F1", L.Culture);
                    var justEnded = DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(2);
                    Activity.Muted(justEnded
                        ? L.T($"Partie terminée : analyse du replay ({mb} Mo)…", $"Match over: analyzing the replay ({mb} MB)…", $"Partida terminada: analizando la repetición ({mb} MB)…")
                        : L.T($"Analyse de {file.Name} ({mb} Mo)…", $"Analyzing {file.Name} ({mb} MB)…", $"Analizando {file.Name} ({mb} MB)…"));
                }

                var outcome = Process(file);
                outcomes.Add(outcome);
                if (!batch)
                {
                    PrintOutcome(outcome);
                }
            }
        }
        finally
        {
            _deferSaves = false;
            if (_unsaved)
            {
                SaveData(force: true);
            }
            Progress = null;
        }

        if (batch)
        {
            PrintBatchSummary(outcomes);
            if (profilesBefore < 2 && Profiles.List(_data).Count >= 2)
            {
                NewProfilesFound = true;
            }
        }

        var added = outcomes.Count(o => o.Match != null);
        Changed?.Invoke();
        return added;
    }

    /// <summary>Set when a scan found replays of a second account: the player should choose their profile.</summary>
    public bool NewProfilesFound { get; set; }

    private bool IsReady(FileInfo file, bool ignoreQuietPeriod)
    {
        var header = ReplayHeaderProbe.Read(file.FullName);
        var live = header?.IsLive;
        var quietFor = DateTime.UtcNow - file.LastWriteTimeUtc;
        if (live == true)
        {
            // Match still being recorded, unless the recording was cut long ago (Process deals with it).
            return quietFor > AbandonedLiveAge;
        }

        if (live == false && (ignoreQuietPeriod || quietFor > OldReplayAge))
        {
            return true;
        }

        // The file must not have changed for a while and its size must be stable between two scans.
        var stable = _lastSeenSize.TryGetValue(file.FullName, out var size) && size == file.Length;
        _lastSeenSize[file.FullName] = file.Length;

        if (live == null)
        {
            // Header could not be read: be extra careful.
            return stable && quietFor > TimeSpan.FromMinutes(2);
        }

        // Fortnite clears the "live" flag and writes the real encryption key when it stops recording: the replay
        // is complete. Read it as soon as its size has not changed for a few seconds (two checks in a row).
        if (header is { KeyMissing: false })
        {
            IsFinishing = true;
            return stable && quietFor >= FinalizedQuietPeriod;
        }
        return stable && quietFor > QuietPeriod;
    }

    private Outcome Process(FileInfo file)
    {
        var key = FileKey(file);

        // Recording cut in the middle of the match: Fortnite never wrote the decryption key, nothing can be read.
        // Without this, the replay would stay "pending" forever (no auto update, auto close delayed every time).
        if (ReplayHeaderProbe.Read(file.FullName) is { IsLive: true, KeyMissing: true })
        {
            _data.ProcessedFiles[key] = "incomplete";
            SaveData();
            return new Outcome(Skipped: L.T("replay incomplet (Fortnite s'est fermé pendant la partie) : cette partie ne peut pas être comptée",
                "incomplete replay (Fortnite closed during the match): this match cannot be counted",
                "repetición incompleta (Fortnite se cerró durante la partida): esta partida no se puede contar"));
        }

        MatchRecord record;
        var attempts = _retries.TryGetValue(key, out var tried) ? tried.Attempts : 0;
        try
        {
            record = Analyze(file, ParseTimeout * (1 + attempts));
            _retries.Remove(key);
        }
        catch (Exception ex)
        {
            var inner = (ex as AggregateException)?.InnerException ?? ex;
            var message = inner.Message;
            ErrorLog.Write($"Unreadable replay: {file.FullName} ({file.Length} bytes, {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}, attempt {attempts + 1})", ex);
            if (_liveFile == file.FullName)
            {
                ClearLive();
            }
            if (inner is ParserBusyException)
            {
                // Not counted as an attempt: the replay is read once the counter is restarted (or the thread ends).
                _retries[key] = (attempts, DateTime.UtcNow.AddMinutes(2));
                if (_busyReported)
                {
                    return new Outcome();
                }
                _busyReported = true;
                return new Outcome(Error: message);
            }
            if (IsTransient(inner) && attempts + 1 < MaxAttempts)
            {
                // Not saved: the replay stays "to analyze" and is tried again in a few minutes.
                _retries[key] = (attempts + 1, DateTime.UtcNow.AddMinutes(2 * (attempts + 1)));
                return new Outcome(Error: $"{file.Name} : {message} ({L.T("nouvel essai dans quelques minutes", "trying again in a few minutes", "nuevo intento en unos minutos")})");
            }
            _retries.Remove(key);
            _data.ProcessedFiles[key] = "error: " + message;
            SaveData();
            return new Outcome(Error: $"{file.Name} : {message}");
        }

        record.ReplayFile = file.Name;

        if (!record.IsBattleRoyale)
        {
            if (_liveFile == file.FullName)
            {
                ClearLive();
            }
            // Also drop a record left by an older version of the tool for this replay.
            _data.Matches.RemoveAll(m => m.SessionId == record.SessionId);
            _data.ProcessedFiles[key] = "autre mode";
            SaveData();
            return new Outcome(Skipped: L.T("pas une partie Battle Royale (créatif / autre mode)", "not a Battle Royale match (Creative / other mode)", "no es una partida de Battle Royale (Creativo / otro modo)"));
        }

        var existing = _data.Matches.FirstOrDefault(m => m.SessionId == record.SessionId);
        if (existing != null && existing.AnalyzerVersion >= record.AnalyzerVersion)
        {
            _data.ProcessedFiles[key] = record.SessionId;
            SaveData();
            return new Outcome(Skipped: L.T("partie déjà comptée (même partie, fichier renommé)", "match already counted (same match, renamed file)", "partida ya contada (misma partida, archivo renombrado)"));
        }

        if (existing != null)
        {
            // Re-analysis with a newer version of the tool: replace the old record.
            record.AnalyzedAtUtc = existing.AnalyzedAtUtc;
            _data.Matches.Remove(existing);
        }

        _data.Matches.Add(record);
        _data.ProcessedFiles[key] = record.SessionId;
        SaveData();
        if (!string.IsNullOrWhiteSpace(_config.ArchiveFolder))
        {
            var folder = _config.ArchiveFolder;
            _ = Task.Run(() => CopyToArchive(folder, file)); // a sleeping USB or network drive must not stall the count
        }
        if (_liveFile == file.FullName)
        {
            ClearLive(); // the final analysis replaces the live estimate
        }

        // First match ever: its account becomes the profile.
        if (string.IsNullOrEmpty(_config.Profile))
        {
            _config.Profile = Profiles.Key(record);
            try { _store.SaveConfig(_config); } catch { /* ignore */ }
        }

        var newIds = _store.RegisterPrizeIds(record.TreasureFinds.Where(f => f.PrizeId != null).Select(f => f.PrizeId!));
        return new Outcome(record, Updated: existing != null, NewPrizeIds: newIds);
    }

    private void PrintOutcome(Outcome o)
    {
        if (o.Error != null)
        {
            Activity.Error(L.T($"Impossible de lire ce replay : {o.Error}", $"Cannot read this replay: {o.Error}", $"No se puede leer esta repetición: {o.Error}"));
            PrintErrorLogHint();
            return;
        }
        if (o.Skipped != null)
        {
            Activity.Muted($"{L.T("Ignoré", "Skipped", "Ignorada")}{L.T(" : ", ": ", ": ")}{o.Skipped}.");
            return;
        }

        var m = o.Match!;
        PrintMatch(m, o.Updated);
        var current = Profiles.Current(_data, _config);
        if (current != null && Profiles.Key(m) != current.Key)
        {
            var name = m.PlayerName ?? L.T("compte inconnu", "unknown account", "cuenta desconocida");
            Activity.Warning(L.T($"Partie jouée sur un autre compte ({name}) : elle compte pour son propre profil. Profil affiché : {current.Name}. Change-le en haut de l'interface.",
                $"Match played on another account ({name}): it counts for its own profile. Profile shown: {current.Name}. Switch it at the top of the interface.",
                $"Partida jugada con otra cuenta ({name}): cuenta para su propio perfil. Perfil mostrado: {current.Name}. Cámbialo arriba en la interfaz."));
        }
        PrintNewPrizeIds(o.NewPrizeIds);
    }

    /// <summary>Only real prize ids are worth asking about (unknown gizmos are listed in treasures.json anyway).</summary>
    private void PrintNewPrizeIds(List<string>? ids)
    {
        ids = ids?.Where(id => !id.StartsWith(TreasureNames.UnknownPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ids is { Count: > 0 })
        {
            Activity.Warning(L.T($"Trésor(s) à nommer (section Trésors de l'interface) : {string.Join(", ", ids)}",
                $"Treasure(s) to name (Treasures section of the interface): {string.Join(", ", ids)}",
                $"Tesoro(s) por nombrar (sección Tesoros de la interfaz): {string.Join(", ", ids)}"));
        }
    }

    private void PrintErrorLogHint()
    {
        Activity.Muted(L.T($"Détails dans {_store.ErrorLogPath}. Souvent dû à une mise à jour de Fortnite : signale-le sur {AppInfo.IssuesUrl}",
            $"Details in {_store.ErrorLogPath}. Usually caused by a Fortnite update: report it at {AppInfo.IssuesUrl}",
            $"Detalles en {_store.ErrorLogPath}. Suele deberse a una actualización de Fortnite: avísalo en {AppInfo.IssuesUrl}"));
    }

    private void PrintBatchSummary(List<Outcome> outcomes)
    {
        var matches = outcomes.Where(o => o.Match != null).Select(o => o.Match!).ToList();
        var skipped = outcomes.Count(o => o.Skipped != null);
        var errors = outcomes.Where(o => o.Error != null).Select(o => o.Error!).ToList();

        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        Activity.Success($"✔ {L.Count(outcomes.Count, "replay analysé", "replays analysés", "replay analyzed", "replays analyzed", "repetición analizada", "repeticiones analizadas")}{L.T(" : ", ": ", ": ")}"
            + $"{L.Count(matches.Count, "partie", "parties", "match", "matches", "partida", "partidas")}, {L.Count(matches.Sum(m => m.ChestsOpened), "coffre", "chest", "cofre")}"
            + (treasures > 0 ? $", ★ {L.Count(treasures, "trésor", "treasure", "tesoro")}" : ""));
        if (skipped > 0)
        {
            Activity.Muted(L.T($"{skipped} ignoré(s) : modes créatifs, parties déjà comptées ou replays incomplets.", $"{skipped} skipped: Creative modes, matches already counted or incomplete replays.", $"{skipped} ignorada(s): modos creativos, partidas ya contadas o repeticiones incompletas."));
        }
        if (errors.Count > 0)
        {
            Activity.Error(L.T($"{errors.Count} replay(s) illisible(s) : ", $"{errors.Count} unreadable replay(s): ", $"{errors.Count} repetición(es) ilegible(s): ") + string.Join(" · ", errors.Take(5)));
            PrintErrorLogHint();
        }
        PrintNewPrizeIds(outcomes.Where(o => o.NewPrizeIds != null).SelectMany(o => o.NewPrizeIds!).ToList());
    }

    /// <summary>Reads the file (even while Fortnite writes it) and parses it on a lowest-priority background thread.</summary>
    private MatchRecord Analyze(FileInfo file, TimeSpan timeout)
    {
        if (_stuckWorker is { IsAlive: true })
        {
            // The previous parser still runs (it ignored the cancellation): a new one would share its static hooks.
            throw new ParserBusyException();
        }
        _stuckWorker = null;
        byte[] bytes;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        MatchRecord? result = null;
        Exception? failure = null;
        using var cancel = new CancellationTokenSource();
        var worker = new Thread(() =>
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                // The replays saved on a PC are those of its player: the replay owner is tracked (one profile per account).
                result = new ReplayAnalyzer { Cancellation = cancel.Token }.Analyze(ms, file.Name, "");
            }
            catch (Exception e)
            {
                failure = e;
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "ReplayAnalysis",
        };
        worker.Start();
        if (!worker.Join(timeout))
        {
            // Stop the parser before the next analysis starts (it shares static hooks with it).
            cancel.Cancel();
            if (!worker.Join(TimeSpan.FromSeconds(30)))
            {
                ErrorLog.Write($"Analysis thread did not stop after the timeout: {file.Name}", "cancellation ignored");
                _stuckWorker = worker;
            }
            bytes = Array.Empty<byte>();
            ReleaseMemory();
            throw new TimeoutException(L.T("analyse trop longue", "analysis took too long", "análisis demasiado largo"));
        }
        bytes = Array.Empty<byte>();
        ReleaseMemory();
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); // keep the original stack
        }
        return result!;
    }

    // ---- Live match ---------------------------------------------------------------------------
    private string? _liveFile;
    private long _liveSize = -1;
    private DateTime _liveParsedUtc = DateTime.MinValue;

    /// <summary>Estimate of the match being played (replay still recorded), or null. Not saved.</summary>
    public MatchRecord? LiveMatch { get; private set; }

    /// <summary>True while a replay is being recorded (a match is in progress).</summary>
    public bool IsLive => _liveFile != null;

    private void ClearLive()
    {
        _liveFile = null;
        _liveSize = -1;
        LiveMatch = null;
    }

    /// <summary>
    /// Re-reads the replay being recorded when Fortnite has appended data to it (about every 25 s), at most once
    /// every <see cref="Config.LiveIntervalSeconds"/>. Returns true when the live figures changed.
    /// </summary>
    public bool UpdateLive()
    {
        // The newest replay still flagged "live" and written recently (an old one left by a crash is ignored).
        var file = UnprocessedFiles()
            .Where(f => DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(3))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault(f => ReplayHeaderProbe.IsLive(f.FullName) == true);

        if (file == null)
        {
            var had = _liveFile != null;
            // Match over: keep the estimate on screen until the final analysis replaces it, unless it went stale.
            if (had && (!File.Exists(_liveFile) || DateTime.UtcNow - File.GetLastWriteTimeUtc(_liveFile!) > TimeSpan.FromMinutes(5)))
            {
                ClearLive();
                return true;
            }
            return false;
        }

        var started = false;
        if (file.FullName != _liveFile)
        {
            ClearLive();
            _liveFile = file.FullName;
            started = true;
            Activity.Muted(L.T("Partie en cours… (ses coffres seront comptés à la fin)", "Match in progress… (its chests will be counted at the end)", "Partida en curso… (sus cofres se contarán al final)"));
        }

        // Fortnite writes the encryption key only at the end of the match: until then the data cannot be read,
        // so we only know that a match is in progress (no useless re-reading).
        if (!_config.LiveUpdate || ReplayHeaderProbe.KeyMissing(file.FullName))
        {
            return started;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _config.LiveIntervalSeconds));
        if (file.Length == _liveSize || DateTime.UtcNow - _liveParsedUtc < interval)
        {
            return started;
        }

        MatchRecord record;
        try
        {
            record = Analyze(file, TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            _liveParsedUtc = DateTime.UtcNow; // try again at the next interval
            if (Environment.GetEnvironmentVariable("FCT_DEBUG") is { Length: > 0 })
            {
                Console.Error.WriteLine($"DBG live parse failed: {ex}");
            }
            return started;
        }
        _liveSize = file.Length;
        _liveParsedUtc = DateTime.UtcNow;

        var before = LiveMatch;
        LiveMatch = record;
        var changed = before?.ChestsOpened != LiveMatch?.ChestsOpened || before?.TreasureFinds.Count != LiveMatch?.TreasureFinds.Count || (before == null) != (LiveMatch == null);
        if (changed && LiveMatch != null)
        {
            Activity.Muted($"{L.T("Partie en cours", "Match in progress", "Partida en curso")}{L.T(" : ", ": ", ": ")}{L.Count(LiveMatch.ChestsOpened, "coffre", "chest", "cofre")}"
                + (LiveMatch.TreasureFinds.Count > 0 ? $", {L.Count(LiveMatch.TreasureFinds.Count, "trésor", "treasure", "tesoro")}" : ""));
        }
        return changed || started;
    }

    /// <summary>
    /// When the analysis logic changed, re-analyze the replays still on disk (records are replaced, not duplicated).
    /// Must run before any scan. Returns true when existing matches will be re-analyzed (worth telling the player).
    /// </summary>
    public bool UpgradeIfNeeded()
    {
        if (_data.AnalyzerVersion >= ReplayAnalyzer.CurrentVersion)
        {
            return false;
        }

        var hadData = _data.Matches.Count > 0;
        foreach (var key in _data.ProcessedFiles.Where(kv => kv.Value != "skipped").Select(kv => kv.Key).ToList())
        {
            _data.ProcessedFiles.Remove(key);
        }
        _data.AnalyzerVersion = ReplayAnalyzer.CurrentVersion;
        _store.SaveData(_data);
        return hadData;
    }

    private void PrintMatch(MatchRecord m, bool updated)
    {
        var total = _data.Matches.Where(x => Profiles.Key(x) == Profiles.Key(m)).Sum(x => x.ChestsOpened);
        var when = (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime();

        var destroyed = m.Chests.Count(c => c.Destroyed);
        Activity.Success($"{(updated ? "↻" : "✔")} {L.T("Partie du", "Match of", "Partida del")} {L.DateTimeShort(when)} ({Pretty.Playlist(m.Playlist)}, {Pretty.Placement(m)}){L.T(" : ", ": ", ": ")}"
            + L.Count(m.ChestsOpened, "coffre", "chest", "cofre")
            + (destroyed > 0 ? L.T($" (dont {L.Count(destroyed, "détruit", "", "")})", $" ({destroyed} destroyed)", $" ({L.Count(destroyed, "", "", "destruido")})") : "")
            + $"  →  total{L.T(" : ", ": ", ": ")}{L.N(total)}");

        if (!updated && ThisSession.Contains(m) && m.TreasureFinds.FirstOrDefault(f => f.FromGoldenChest) is { } prize)
        {
            NativeTray.Notify(L.T("Trésor !", "Treasure!", "¡Tesoro!"),
                L.T($"{prize.DisplayName} dans ta dernière partie. Bien joué !", $"{prize.DisplayName} in your last match. Well played!", $"{prize.DisplayName} en tu última partida. ¡Bien jugado!"));
        }
        // Only for new matches of this session: a re-analysis of the whole history would flood the activity log.
        foreach (var r in !updated && ThisSession.Contains(m) ? m.RareNearby ?? new() : new())
        {
            var who = r.PlayerName ?? L.T("Un joueur", "A player", "Un jugador");
            Activity.Info(r.Golden
                ? L.T($"◆ {who} a ouvert un coffre doré près de toi : {r.DisplayName}", $"◆ {who} opened a golden chest near you: {r.DisplayName}", $"◆ {who} abrió un cofre dorado cerca de ti: {r.DisplayName}")
                : L.T($"◆ {who} avait {r.DisplayName} près de toi", $"◆ {who} had {r.DisplayName} near you", $"◆ {who} tenía {r.DisplayName} cerca de ti"));
        }
        foreach (var find in m.TreasureFinds)
        {
            Activity.Treasure($"{(find.FromGoldenChest ? "★★" : "★")} {L.T("Trésor obtenu : ", "Treasure obtained: ", "Tesoro obtenido: ")}{find.DisplayName} ({Pretty.Obtained(find)})");
            if (!find.SourceIsChest)
            {
                Activity.Warning(L.T($"⚑ Source inhabituelle : {find.SourceLabel}{(find.SourceCertain ? "" : " (probable)")} [{find.SourceBlueprint}]",
                    $"⚑ Unusual source: {find.SourceLabel}{(find.SourceCertain ? "" : " (likely)")} [{find.SourceBlueprint}]",
                    $"⚑ Origen inusual: {find.SourceLabel}{(find.SourceCertain ? "" : " (probable)")} [{find.SourceBlueprint}]"));
            }
        }
    }
}

/// <summary>Replays analyzed so far / to analyze in a scan of several replays.</summary>
public sealed record ScanProgress(int Done, int Total);

/// <summary>What the replay header says about the recording.</summary>
/// <param name="IsLive">The match is still being recorded (or the recording was cut).</param>
/// <param name="KeyMissing">
/// The replay is encrypted and its key is not written yet. Fortnite writes the real key only when the match ends
/// (all zeros before): the data of a match in progress cannot be read.
/// </param>
public readonly record struct ReplayHeaderInfo(bool IsLive, bool KeyMissing);

/// <summary>Reads just enough of the replay file header to know whether the match is still being recorded.</summary>
public static class ReplayHeaderProbe
{
    private const uint FileMagic = 0x1CA2E27F;

    /// <summary>The header of the file, or null when it is not a replay or cannot be read.</summary>
    public static ReplayHeaderInfo? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(fs);
        }
        catch
        {
            return null;
        }
    }

    public static ReplayHeaderInfo? Read(Stream stream)
    {
        using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        bool isLive;
        try
        {
            if (br.ReadUInt32() != FileMagic)
            {
                return null;
            }
            var fileVersion = br.ReadUInt32();
            if (fileVersion >= 7)
            {
                var count = br.ReadInt32(); // custom versions (guid + version: 20 bytes each)
                if (count < 0 || count > 64)
                {
                    return null;
                }
                stream.Seek(count * 20L, SeekOrigin.Current);
            }

            br.ReadUInt32(); // LengthInMS
            br.ReadUInt32(); // NetworkVersion
            br.ReadUInt32(); // Changelist
            var len = br.ReadInt32(); // FriendlyName (FString: negative length = UTF-16)
            stream.Seek(len < 0 ? -2L * len : len, SeekOrigin.Current);
            isLive = br.ReadUInt32() != 0;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            return null;
        }

        try
        {
            br.ReadInt64(); // timestamp
            br.ReadUInt32(); // compressed
            if (br.ReadUInt32() == 0)
            {
                return new ReplayHeaderInfo(isLive, KeyMissing: false); // not encrypted
            }
            var keyLen = br.ReadInt32();
            if (keyLen <= 0 || keyLen > 256)
            {
                return new ReplayHeaderInfo(isLive, KeyMissing: true);
            }
            var key = br.ReadBytes(keyLen);
            return new ReplayHeaderInfo(isLive, KeyMissing: key.Length < keyLen || key.All(b => b == 0));
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            return new ReplayHeaderInfo(isLive, KeyMissing: true);
        }
    }

    public static bool? IsLive(string path) => Read(path)?.IsLive;

    /// <summary>See <see cref="ReplayHeaderInfo.KeyMissing"/>. True as well when the header cannot be read.</summary>
    public static bool KeyMissing(string path) => Read(path)?.KeyMissing ?? true;
}
