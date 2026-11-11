using System.Text.Json;
using System.Text.Json.Serialization;

namespace FortniteChestTracker;

/// <summary>A boosted period (Treasure Hours): rate multiplier per treasure key (odds_*).</summary>
public sealed class BoostWindow
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public Dictionary<string, double> Boosts { get; set; } = new();

    /// <summary>Announced by Epic (false = expected from the weekly pattern).</summary>
    public bool Confirmed { get; set; }

    public double BoostOf(string key) => Boosts.TryGetValue(key, out var b) && b > 0 ? b : 1;
}

/// <summary>One prize of a week (golden chest rewards).</summary>
public sealed class SeasonPrize
{
    /// <summary>Kind: outfit, pickaxe, backbling, vehicle, jam, loading.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Picture in /img (embedded) or on the website.</summary>
    public string? Img { get; set; }

    /// <summary>Rate it drops at (odds_* key).</summary>
    public string? Odds { get; set; }

    /// <summary>Prize id in the replays ("dpid_item1"), when known: the prize is checked off when found.</summary>
    public string? Id { get; set; }
}

public sealed class SeasonWeek
{
    public DateTime Start { get; set; }
    public string Title { get; set; } = "";
    public string? Art { get; set; }
    public List<SeasonPrize> Prizes { get; set; } = new();
}

/// <summary>The treasure hunt of the season: weeks, prizes and Treasure Hours (season.json).</summary>
public sealed class SeasonData
{
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    /// <summary>Ranked matches give treasure-hunt rewards (false: their chests do not count for the chances).</summary>
    public bool Ranked { get; set; }

    public List<SeasonWeek> Weeks { get; set; } = new();
    public List<BoostWindow> TreasureHours { get; set; } = new();
}

/// <summary>
/// Calendar of the treasure hunt. The copy embedded in the exe is replaced by a newer one from the website
/// (fortnite-chest-tracker.com/season.json, a plain file: nothing is sent), so new Treasure Hours, prizes or boosts reach
/// the app without a new version.
/// </summary>
public static class Season
{
    public const string RemoteUrl = "https://" + AppInfo.SiteName + "/season.json";
    public const string FileName = "season.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static volatile SeasonData _current = Embedded() ?? new SeasonData();

    public static SeasonData Current => _current;

    /// <summary>The calendar as JSON (served to the page at /season.json).</summary>
    public static string Json => JsonSerializer.Serialize(_current, Options);

    public static SeasonData? Parse(string json)
    {
        try
        {
            var d = JsonSerializer.Deserialize<SeasonData>(json, Options);
            if (d == null || d.Start == default || d.End <= d.Start)
            {
                return null;
            }
            d.Start = Utc(d.Start);
            d.End = Utc(d.End);
            foreach (var w in d.Weeks)
            {
                w.Start = Utc(w.Start);
            }
            foreach (var h in d.TreasureHours)
            {
                h.Start = Utc(h.Start);
                h.End = Utc(h.End);
            }
            d.Weeks.Sort((a, b) => a.Start.CompareTo(b.Start));
            d.TreasureHours.Sort((a, b) => a.Start.CompareTo(b.Start));
            return d;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime Utc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    private static SeasonData? Embedded()
    {
        using var stream = typeof(Season).Assembly.GetManifestResourceStream("FortniteChestTracker.wwwroot.season.json");
        if (stream == null)
        {
            return null;
        }
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Uses this calendar if it is at least as recent as the current one (tests, saved copy, download).</summary>
    public static bool Use(SeasonData? data)
    {
        if (data == null || data.Version < _current.Version)
        {
            return false;
        }
        _current = data;
        return true;
    }

    /// <summary>The copy downloaded earlier, kept in the data folder.</summary>
    public static void LoadSaved(string dataFolder)
    {
        try
        {
            var path = Path.Combine(dataFolder, FileName);
            if (File.Exists(path))
            {
                Use(Parse(File.ReadAllText(path)));
            }
        }
        catch
        {
            // the embedded calendar stays
        }
    }

    /// <summary>Downloads the calendar of the website; keeps it when newer. Never throws.</summary>
    public static async Task<bool> RefreshAsync(string dataFolder)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"FortniteChestTracker/{AppInfo.VersionText}");
            var json = await http.GetStringAsync(RemoteUrl).ConfigureAwait(false);
            var data = Parse(json);
            if (data == null || data.Version <= _current.Version)
            {
                return false;
            }
            Use(data);
            var path = Path.Combine(dataFolder, FileName);
            await File.WriteAllTextAsync(path + ".tmp", json).ConfigureAwait(false);
            File.Move(path + ".tmp", path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- Questions about time ---------------------------------------------------------------------

    /// <summary>Treasure Hours window going on at this time (null = none).</summary>
    public static BoostWindow? ActiveAt(DateTime utc) => _current.TreasureHours.FirstOrDefault(h => utc >= h.Start && utc < h.End);

    /// <summary>Next Treasure Hours window starting after this time (null = none left).</summary>
    public static BoostWindow? NextAfter(DateTime utc) => _current.TreasureHours.FirstOrDefault(h => h.Start > utc);

    /// <summary>Rate multiplier of a treasure at this time (1 outside Treasure Hours).</summary>
    public static double Boost(string key, DateTime utc) => ActiveAt(utc)?.BoostOf(key) ?? 1;

    /// <summary>Week of the season this time falls in (0-based), or -1 outside the season.</summary>
    public static int WeekAt(DateTime utc)
    {
        var d = _current;
        if (utc < d.Start || utc >= d.End || d.Weeks.Count == 0)
        {
            return -1;
        }
        var i = d.Weeks.FindLastIndex(w => w.Start <= utc);
        return Math.Max(0, i);
    }

    /// <summary>Start and end of a week (the end is the next week's start, or the end of the season).</summary>
    public static (DateTime Start, DateTime End) WeekBounds(int index)
    {
        var d = _current;
        var start = d.Weeks[index].Start;
        var end = index + 1 < d.Weeks.Count ? d.Weeks[index + 1].Start : d.End;
        return (start, end);
    }

    public static bool IsOn(DateTime utc) => utc >= _current.Start && utc < _current.End;
}
