using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FortniteChestTracker;

/// <summary>
/// Matches played that the replays do not cover (before replays were turned on, or deleted by Fortnite), entered by
/// the player (copied from fortnite.gg) or fetched from fortnite-api.com with the player's own key. Saved in config.json.
/// </summary>
public sealed class EstimateSettings
{
    /// <summary>"manual" (typed in) or "api" (fortnite-api.com).</summary>
    public string Source { get; set; } = "manual";

    /// <summary>Matches played per mode: "solo", "duo", "trio", "squad", "other"; or only "total".</summary>
    public Dictionary<string, int> Matches { get; set; } = new();

    /// <summary>
    /// Minutes played in the matches entered (replays included), from fortnite-api.com or typed in; null = unknown.
    /// Only their average per match is used: Epic counts less time per match than the replays do (about 10 min
    /// against 12.5 on the same account, likely the time alive), so the counted matches keep their replay time.
    /// </summary>
    public int? Minutes { get; set; }

    /// <summary>Epic name the numbers belong to.</summary>
    public string? Player { get; set; }

    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Add the estimate to the total shown (interface and overlay), marked "≈".</summary>
    public bool Include { get; set; }

    /// <summary>The player's own fortnite-api.com key (optional; only sent to fortnite-api.com).</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Result of the estimate for one mode (or for all of them).</summary>
public sealed record EstimatePart(string Bucket, int Played, int Tracked, int Untracked, double Average, int Estimated);

/// <param name="Low">Low end of a 95 % range (uncertainty on the average only).</param>
/// <param name="Matches">Matches in total: the counted ones + the ones without replay.</param>
/// <param name="Minutes">Minutes played in total: the counted matches + the ones without replay.</param>
/// <param name="MinutesFromEpic">The minutes come from Epic's stats (else the average length of the counted matches).</param>
public sealed record EstimateResult(int Played, int Tracked, int Untracked, int Estimated, int Low, int High, IReadOnlyList<EstimatePart> Parts,
    int Matches = 0, int Minutes = 0, bool MinutesFromEpic = false);

public static class Estimates
{
    public static readonly string[] Buckets = { "solo", "duo", "trio", "squad", "other" };

    /// <summary>Mode family of a playlist, as Epic's stats split them.</summary>
    public static string Bucket(string? playlist)
    {
        var p = (playlist ?? "").ToLowerInvariant();
        return p.Contains("solo") ? "solo"
            : p.Contains("duo") ? "duo"
            : p.Contains("trio") ? "trio"
            : p.Contains("squad") || p.Contains("quad") ? "squad"
            : "other";
    }

    /// <summary>
    /// Chests of the matches the replays do not cover = (matches played − matches counted) × average chests per match,
    /// mode by mode when the numbers are per mode (the average of the mode when at least 5 matches were counted in it,
    /// else the overall one). Null when nothing usable was entered.
    /// </summary>
    public static EstimateResult? Compute(EstimateSettings? settings, IReadOnlyList<MatchRecord> tracked)
    {
        if (settings == null || settings.Matches.Count == 0 || tracked.Count == 0)
        {
            return null;
        }
        var all = tracked.Select(m => (double)m.ChestsOpened).ToList();
        var mean = all.Average();
        var sd = Math.Sqrt(all.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, all.Count - 1));

        var parts = new List<EstimatePart>();
        if (settings.Matches.TryGetValue("total", out var total) && Buckets.All(b => !settings.Matches.ContainsKey(b)))
        {
            var untracked = Math.Max(0, total - tracked.Count);
            parts.Add(new EstimatePart("total", total, tracked.Count, untracked, Math.Round(mean, 1), (int)Math.Round(untracked * mean)));
        }
        else
        {
            var byBucket = tracked.GroupBy(m => Bucket(m.Playlist)).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var bucket in Buckets)
            {
                if (!settings.Matches.TryGetValue(bucket, out var played) || played <= 0)
                {
                    continue;
                }
                var mine = byBucket.TryGetValue(bucket, out var list) ? list : new List<MatchRecord>();
                var avg = mine.Count >= 5 ? mine.Average(m => m.ChestsOpened) : mean;
                var untracked = Math.Max(0, played - mine.Count);
                parts.Add(new EstimatePart(bucket, played, mine.Count, untracked, Math.Round(avg, 1), (int)Math.Round(untracked * avg)));
            }
        }
        if (parts.Count == 0)
        {
            return null;
        }
        var totalUntracked = parts.Sum(p => p.Untracked);
        var estimated = parts.Sum(p => p.Estimated);
        var margin = (int)Math.Round(1.96 * sd / Math.Sqrt(all.Count) * totalUntracked);

        // Time played: the counted matches with their replay time, the ones without replay at Epic's average minutes
        // per match when known, else at the average length of the counted ones.
        var trackedMinutes = tracked.Sum(m => m.DurationSeconds ?? 0) / 60;
        var timed = tracked.Where(m => m.DurationSeconds > 0).ToList();
        var entered = settings.Matches.Values.Where(v => v > 0).Sum() - (settings.Matches.ContainsKey("total") && settings.Matches.Count > 1 ? settings.Matches["total"] : 0);
        var fromEpic = settings.Minutes is > 0 && entered > 0;
        double? perMatch = fromEpic ? (double)settings.Minutes!.Value / entered
            : timed.Count > 0 ? timed.Average(m => m.DurationSeconds!.Value) / 60.0 : null;
        var minutes = trackedMinutes + (perMatch is { } pm ? (int)Math.Round(totalUntracked * pm) : 0);

        return new EstimateResult(parts.Sum(p => p.Played), parts.Sum(p => p.Tracked), totalUntracked, estimated,
            Math.Max(0, estimated - margin), estimated + margin, parts, tracked.Count + totalUntracked, minutes, fromEpic);
    }

    /// <summary>The player's page on fortnite.gg (opened in the browser: the site does not allow automated access).</summary>
    public static string FortniteGgUrl(string player) => "https://fortnite.gg/stats/br?player=" + Uri.EscapeDataString(player);

    /// <summary>
    /// Lifetime matches per mode and minutes played from fortnite-api.com (the player's own key, Authorization
    /// header). Returns them, or an error message for the player.
    /// </summary>
    public static async Task<(Dictionary<string, int>? Matches, int? Minutes, string? Error)> FetchAsync(string player, string apiKey, HttpMessageHandler? handler = null)
    {
        try
        {
            using var http = handler != null ? new HttpClient(handler) : new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"FortniteChestTracker/{AppInfo.VersionText}");
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://fortnite-api.com/v2/stats/br/v2?timeWindow=lifetime&name=" + Uri.EscapeDataString(player));
            req.Headers.TryAddWithoutValidation("Authorization", apiKey.Trim());
            using var res = await http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (res.StatusCode != HttpStatusCode.OK)
            {
                var message = TryMessage(body);
                return (null, null, res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => L.T("clé fortnite-api.com refusée", "fortnite-api.com key refused", "clave de fortnite-api.com rechazada"),
                    HttpStatusCode.Forbidden => L.T("statistiques privées : dans Fortnite, rends tes statistiques publiques", "private stats: make your stats public in Fortnite", "estadísticas privadas: hazlas públicas en Fortnite"),
                    HttpStatusCode.NotFound => L.T($"joueur « {player} » introuvable", $"player \"{player}\" not found", $"jugador «{player}» no encontrado"),
                    _ => $"HTTP {(int)res.StatusCode}",
                } + (message != null ? $" ({message})" : ""));
            }
            var matches = Parse(body);
            return matches.Count > 0 ? (matches, ParseMinutes(body), null) : (null, null, L.T("réponse sans nombre de parties", "answer without match counts", "respuesta sin número de partidas"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, null, L.T("fortnite-api.com injoignable", "fortnite-api.com unreachable", "no se puede acceder a fortnite-api.com") + $" ({ex.Message})");
        }
    }

    /// <summary>data.stats.all.{solo,duo,trio,squad,ltm}.matches (null modes skipped); data.stats.all.overall.matches as a fallback.</summary>
    internal static Dictionary<string, int> Parse(string json)
    {
        var result = new Dictionary<string, int>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("stats", out var stats)
            || !stats.TryGetProperty("all", out var all) || all.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        int? Matches(string mode) => all.TryGetProperty(mode, out var m) && m.ValueKind == JsonValueKind.Object
            && m.TryGetProperty("matches", out var n) && n.TryGetInt32(out var v) ? v : null;

        foreach (var (mode, bucket) in new[] { ("solo", "solo"), ("duo", "duo"), ("trio", "trio"), ("squad", "squad"), ("ltm", "other") })
        {
            if (Matches(mode) is { } v && v > 0)
            {
                result[bucket] = v;
            }
        }
        // The overall count also covers modes not listed (trios are sent as null): keep the difference as "other".
        if (Matches("overall") is { } overall && overall > result.Values.Sum())
        {
            result["other"] = (result.TryGetValue("other", out var o) ? o : 0) + overall - result.Values.Sum();
        }
        return result;
    }

    /// <summary>data.stats.all.overall.minutesPlayed (lifetime, all modes), null when missing or 0.</summary>
    internal static int? ParseMinutes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object
            && stats.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Object
            && all.TryGetProperty("overall", out var overall) && overall.ValueKind == JsonValueKind.Object
            && overall.TryGetProperty("minutesPlayed", out var n) && n.TryGetInt32(out var v) && v > 0 ? v : null;
    }

    private static string? TryMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
