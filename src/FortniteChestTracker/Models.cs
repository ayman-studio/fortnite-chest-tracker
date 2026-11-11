using System.Text.Json;
using System.Text.Json.Serialization;

namespace FortniteChestTracker;

public sealed class ChestRecord
{
    public float TimeSeconds { get; set; }
    public string Kind { get; set; } = "";
    public string Blueprint { get; set; } = "";
    /// <summary>True when the chest was destroyed by the player instead of being opened.</summary>
    public bool Destroyed { get; set; }
    public List<string> Items { get; set; } = new();
    public List<string> TreasureItems { get; set; } = new();
}

public sealed class TreasureFind
{
    /// <summary>Name to display (re-resolved from treasures.json so renaming updates old matches).</summary>
    public string DisplayName => PrizeId != null ? TreasureNames.Friendly(PrizeId)
        : TreasureNames.PrizeNames.TryGetValue(RawItem, out var named) && !string.IsNullOrWhiteSpace(named) ? named
        : L.Name(Item);

    public string Item { get; set; } = "";
    public string RawItem { get; set; } = "";
    /// <summary>Prize id sent by the server with the golden chest, e.g. "DelilahPrize:dpid_item4".</summary>
    public string? PrizeId { get; set; }
    /// <summary>True when detected from the golden treasure chest itself.</summary>
    public bool FromGoldenChest { get; set; }
    /// <summary>True when detected only from the prize handler (exact time, item unknown unless sent at that moment).</summary>
    public bool FromPrizeHandler { get; set; }
    /// <summary>True when the time is exact (golden chest or prize handler), false when estimated.</summary>
    public bool TimeExact { get; set; }
    /// <summary>True when the source chest was destroyed rather than opened.</summary>
    public bool SourceDestroyed { get; set; }
    public string SourceLabel => SourceDestroyed ? $"{L.Name(SourceKind)} ({L.T("détruit", "destroyed", "destruido")})" : L.Name(SourceKind);
    /// <summary>When it was obtained (replay seconds). Exact for golden chests, estimated otherwise.</summary>
    public float? ObtainedAtSeconds { get; set; }
    /// <summary>Kind of container / interaction it came from (Coffre, Canon à butin...).</summary>
    public string SourceKind { get; set; } = "";
    public string SourceBlueprint { get; set; } = "";
    public bool SourceIsChest { get; set; } = true;
    /// <summary>True when the source is known for sure (golden chest), false when it is the last interaction before use.</summary>
    public bool SourceCertain { get; set; }
    /// <summary>For estimated sources: other (non-chest) interactions between the source and the first use.</summary>
    public List<string> AlsoPossible { get; set; } = new();
}

public sealed class MatchRecord
{
    public string SessionId { get; set; } = "";
    public string ReplayFile { get; set; } = "";
    public DateTime AnalyzedAtUtc { get; set; }
    public DateTime? MatchStartUtc { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Playlist { get; set; }
    public string? GameVersion { get; set; }
    public string? PlayerName { get; set; }
    public string? EpicId { get; set; }
    public bool TrackedByUsername { get; set; }
    /// <summary>Set when none of the configured names played this match (name of the replay owner).</summary>
    public string? NotTrackedOwner { get; set; }
    public bool IsReplayOwner { get; set; }
    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }
    public int? Eliminations { get; set; }
    public int ChestsOpened { get; set; }
    public int AmmoBoxesOpened { get; set; }
    public int OtherContainersOpened { get; set; }
    /// <summary>Non-chest interactions by kind (loot cannons, ammo boxes, food boxes...).</summary>
    public Dictionary<string, int> OtherInteractions { get; set; } = new();
    public int AnalyzerVersion { get; set; } = 1;
    public bool IsBattleRoyale { get; set; } = true;
    public List<ChestRecord> Chests { get; set; } = new();
    public List<TreasureFind> TreasureFinds { get; set; } = new();
}

public sealed class Config
{
    /// <summary>"fr", "en" or "es" (asked on first launch).</summary>
    public string? Language { get; set; }

    /// <summary>Selected profile (Epic id or name of the account the player plays on); null = most recent one.</summary>
    public string? Profile { get; set; }
    public string DemosFolder { get; set; } = "";

    /// <summary>Open the tool automatically when Fortnite starts (invisible standby process started with Windows).</summary>
    public bool StartWithFortnite { get; set; }

    /// <summary>"prompt" (offer new versions), "auto" (install them) or "off" (never check GitHub).</summary>
    public string UpdateMode { get; set; } = "prompt";
    /// <summary>Open the interface page when the tool starts (not when opened with Fortnite or after an update).</summary>
    public bool OpenUiOnStart { get; set; } = true;

    /// <summary>Earlier name of <see cref="OpenUiOnStart"/> in config.json: still read, never written.</summary>
    [JsonInclude, JsonPropertyName("OpenReportOnStart")]
    private bool? OpenUiOnStartOldName
    {
        get => null;
        set
        {
            if (value is bool open)
            {
                OpenUiOnStart = open;
            }
        }
    }
    public bool ShowOverlay { get; set; } = true;
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    /// <summary>Locked overlay = click-through, cannot be moved (Ctrl+Shift+L to toggle).</summary>
    public bool OverlayLocked { get; set; }

    /// <summary>Size of the overlay in percent (100 = normal), on top of the Windows display scaling.</summary>
    public int OverlaySize { get; set; } = 100;

    /// <summary>Opacity of the overlay in percent (100 = opaque).</summary>
    public int OverlayOpacity { get; set; } = 100;

    /// <summary>Folder where a copy of every counted replay is kept (Fortnite deletes its oldest ones). Empty = off.</summary>
    public string ArchiveFolder { get; set; } = "";

    /// <summary>Layout of the overlay: "full" (default) or "compact".</summary>
    public string OverlayStyle { get; set; } = OverlayAppearance.Full;

    /// <summary>Count the chests of the match in progress (re-reads the replay being recorded).</summary>
    public bool LiveUpdate { get; set; } = true;

    /// <summary>Minimum delay between two readings of the replay in progress (seconds, 10 minimum).</summary>
    public int LiveIntervalSeconds { get; set; } = 30;

    /// <summary>Matches the replays do not cover (fortnite.gg / fortnite-api.com), to estimate their chests.</summary>
    public EstimateSettings? Estimate { get; set; }

    /// <summary>Start of the season's treasure hunt (chests opened before cannot have given a treasure).</summary>
    public DateTime TokenSinceUtc { get; set; } = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Treasure aimed at, chosen on the home page and shown on the overlay (odds_* key, or "auto").</summary>
    public string Goal { get; set; } = HudModel.DefaultGoal;

    /// <summary>When the player activated their Pirate Shop Access (14 days of free Item Shop from then); null = not activated.</summary>
    public DateTime? TokenActivatedUtc { get; set; }

    /// <summary>Notification next to the clock 10 minutes before each Treasure Hour.</summary>
    public bool NotifyTreasureHours { get; set; } = true;

    /// <summary>Hide the overlay while a match is being played (shown again in the lobby).</summary>
    public bool OverlayHideInMatch { get; set; }

    /// <summary>The animated 3D island of the home page (off = lighter for small PCs).</summary>
    public bool Scene3D { get; set; } = true;

    /// <summary>Blocks of the overlay the player keeps: the goal (chance + bar), the stats line, the last match line, the website.</summary>
    public bool OverlayGoal { get; set; } = true;
    public bool OverlayStats { get; set; } = true;
    public bool OverlayLast { get; set; } = true;
    public bool OverlaySite { get; set; } = true;
}

public sealed class TrackerData
{
    public int Version { get; set; } = 1;
    public int AnalyzerVersion { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<MatchRecord> Matches { get; set; } = new();

    /// <summary>Key = file name|size|last write ticks. Value = session id, "skipped" or "error: ...".</summary>
    public Dictionary<string, string> ProcessedFiles { get; set; } = new();
}

/// <summary>Reads/writes JSON files atomically in the data folder.</summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Folder { get; }

    public string ConfigPath => Path.Combine(Folder, "config.json");

    public string DataPath => Path.Combine(Folder, "data.json");
    public string OverlayPath => Path.Combine(Folder, "overlay.html");
    public string PrizeNamesPath => Path.Combine(Folder, "treasures.json");
    public string ErrorLogPath => Path.Combine(Folder, "errors.log");

    /// <summary>Known treasure hunt prize ids (week 1), in the chosen language. Unknown ids are added automatically with an empty name.</summary>
    public static Dictionary<string, string> DefaultPrizeNames => new()
    {
        ["DelilahPrize:dpid_item1"] = L.T("Gantelets d'A$AP Rocky en or massif (pioche) ?", "A$AP Rocky solid gold gauntlets (pickaxe) ?", "Guanteletes de oro macizo de A$AP Rocky (pico) ?"),
        ["DelilahPrize:dpid_item2"] = L.T("Plunder in Paradise (écran de chargement)", "Plunder in Paradise (loading screen)", "Plunder in Paradise (pantalla de carga)"),
        ["DelilahPrize:dpid_item3"] = L.T("Mercedes-AMG G 63 (véhicule) ?", "Mercedes-AMG G 63 (vehicle) ?", "Mercedes-AMG G 63 (vehículo) ?"),
        ["DelilahPrize:dpid_item4"] = L.T("No Trespassing (piste musicale)", "No Trespassing (music track)", "No Trespassing (pista musical)"),
    };

    public void LoadPrizeNames()
    {
        var names = Load<Dictionary<string, string>>(PrizeNamesPath);
        if (names == null)
        {
            names = DefaultPrizeNames;
            Save(PrizeNamesPath, names);
        }

        TreasureNames.PrizeNames.Clear();
        foreach (var kv in names)
        {
            TreasureNames.PrizeNames[kv.Key] = kv.Value;
        }
    }

    /// <summary>Adds unknown prize ids to treasures.json so the user can name them. Returns the new ids.</summary>
    public List<string> RegisterPrizeIds(IEnumerable<string> ids)
    {
        var added = ids.Where(id => !TreasureNames.PrizeNames.ContainsKey(id)).Distinct().ToList();
        if (added.Count == 0)
        {
            return added;
        }

        var names = Load<Dictionary<string, string>>(PrizeNamesPath) ?? DefaultPrizeNames;
        foreach (var id in added)
        {
            names[id] = "";
            TreasureNames.PrizeNames[id] = "";
        }
        Save(PrizeNamesPath, names);
        return added;
    }

    /// <summary>Names a prize id in treasures.json (empty name = unnamed again).</summary>
    public void SetPrizeName(string id, string name)
    {
        var names = Load<Dictionary<string, string>>(PrizeNamesPath) ?? DefaultPrizeNames;
        var key = names.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase)) ?? id;
        names[key] = name;
        Save(PrizeNamesPath, names);
        TreasureNames.PrizeNames[key] = name;
    }

    public Store(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        DeleteObsoleteFiles();
    }

    /// <summary>Exports that versions 1.0.x kept up to date and nothing updates any more (everything is in data.json).</summary>
    private void DeleteObsoleteFiles()
    {
        foreach (var name in new[] { "report.html", "report.html.tmp", "matches.csv", "matches.csv.tmp" })
        {
            try
            {
                File.Delete(Path.Combine(Folder, name));
            }
            catch
            {
                // in use: it simply stays there
            }
        }
    }

    public Config? LoadConfig() => Load<Config>(ConfigPath);
    public void SaveConfig(Config config) => Save(ConfigPath, config);
    /// <summary>A new history starts at the current analysis version (nothing to re-analyze).</summary>
    public TrackerData LoadData()
    {
        var data = Load<TrackerData>(DataPath) ?? new TrackerData { AnalyzerVersion = ReplayAnalyzer.CurrentVersion };
        // Older files stored the (UTC) match times without a time zone: they were read as local times.
        foreach (var m in data.Matches)
        {
            if (m.MatchStartUtc is { Kind: DateTimeKind.Unspecified } start)
            {
                m.MatchStartUtc = DateTime.SpecifyKind(start, DateTimeKind.Utc);
            }
            if (m.AnalyzedAtUtc.Kind == DateTimeKind.Unspecified)
            {
                m.AnalyzedAtUtc = DateTime.SpecifyKind(m.AnalyzedAtUtc, DateTimeKind.Utc);
            }
        }
        return data;
    }
    public void SaveData(TrackerData data) => Save(DataPath, data);

    /// <summary>A history file (data.json of this app), or null when it is not one.</summary>
    public static TrackerData? ParseData(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<TrackerData>(json, Json);
            if (data == null || data.Matches == null)
            {
                return null;
            }
            foreach (var m in data.Matches)
            {
                if (m.MatchStartUtc is { Kind: DateTimeKind.Unspecified } start)
                {
                    m.MatchStartUtc = DateTime.SpecifyKind(start, DateTimeKind.Utc);
                }
                if (m.AnalyzedAtUtc.Kind == DateTimeKind.Unspecified)
                {
                    m.AnalyzedAtUtc = DateTime.SpecifyKind(m.AnalyzedAtUtc, DateTimeKind.Utc);
                }
            }
            return data;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            var bak = path + ".bak";
            if (!File.Exists(bak))
            {
                return null;
            }
            try
            {
                File.Copy(bak, path); // put the file back before anything is saved over the backup
                ErrorLog.Write($"Missing file {path}, restored from {bak}", "missing");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // A file that cannot be *read* (locked a moment by an antivirus or a sync tool) is not corrupt: treating it as
        // such would start a new history and overwrite the real one at the next save.
        var text = ReadWithRetry(path);
        try
        {
            if (string.IsNullOrWhiteSpace(text.Replace("\0", "")))
            {
                throw new JsonException("empty file"); // power cut while saving: the backup below takes over
            }
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (JsonException ex)
        {
            // The previous good version (kept by every save) is used before starting over.
            var bak = path + ".bak";
            try
            {
                if (File.Exists(bak) && JsonSerializer.Deserialize<T>(File.ReadAllText(bak), Json) is { } previous)
                {
                    File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
                    ErrorLog.Write($"Unreadable file {path}, previous version restored from {bak}", ex);
                    Activity.Warning(L.T($"Fichier illisible ({ex.Message}) : version précédente restaurée.", $"Unreadable file ({ex.Message}): previous version restored.", $"Archivo ilegible ({ex.Message}): versión anterior restaurada."));
                    return previous;
                }
            }
            catch (Exception bakError) when (bakError is JsonException or IOException or UnauthorizedAccessException)
            {
                // no usable backup: keep a copy of the unreadable file, as before
            }
            // Keep a copy of the unreadable file rather than silently losing the history.
            var backup = path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Copy(path, backup, overwrite: true);
            }
            catch (Exception copyError)
            {
                // Without a backup, starting over would lose the history: better stop here and let the player act.
                ErrorLog.Write($"Unreadable file {path}, and no backup copy possible", copyError);
                throw new IOException(L.T($"Fichier illisible ({ex.Message}) et copie de sauvegarde impossible ({copyError.Message}) : {path}",
                    $"Unreadable file ({ex.Message}) and backup copy impossible ({copyError.Message}): {path}",
                    $"Archivo ilegible ({ex.Message}) y copia de seguridad imposible ({copyError.Message}): {path}"), ex);
            }
            Activity.Warning(L.T($"Fichier illisible ({ex.Message}), copie de sauvegarde : {backup}", $"Unreadable file ({ex.Message}), backup copy: {backup}", $"Archivo ilegible ({ex.Message}), copia de seguridad: {backup}"));
            ErrorLog.Write($"Unreadable file {path}, backup copy: {backup}", ex);

            return null;
        }
    }

    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    /// <summary>
    /// Written to a temporary file flushed to the disk, then swapped in; the previous version becomes ".bak". A power
    /// cut or a crash during a match can no longer leave an empty history.
    /// </summary>
    private static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
                return;
            }
            catch (IOException)
            {
                // e.g. another volume or a file system without Replace: plain move below
            }
        }
        File.Move(tmp, path, overwrite: true);
    }
}
