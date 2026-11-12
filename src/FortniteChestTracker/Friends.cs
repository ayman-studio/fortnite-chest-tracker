using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FortniteChestTracker;

/// <summary>
/// Client of the friends server (the tool's website, /api/friends/*, see worker/friends.js): the player's chests are
/// shared with their friends who have the app, squads see each other's chests a few seconds after each match.
/// Identity = the Epic account id read in the replays + a random key made on this PC (<see cref="Config.FriendKeys"/>).
/// Thread-safe: only plain HTTP calls, the app decides when to call and what to send.
/// </summary>
public static class FriendsClient
{
    private static readonly Regex EpicId = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);
    private static readonly HttpClient Http = CreateClient();

    /// <summary>The website (FCT_FRIENDS_URL replaces it, e.g. http://127.0.0.1:8787 for a local test server).</summary>
    public static string BaseUrl { get; } = Environment.GetEnvironmentVariable("FCT_FRIENDS_URL") is { Length: > 0 } url
        ? url.TrimEnd('/')
        : "https://" + AppInfo.SiteName;

    public static bool IsEpicId(string? id) => id != null && EpicId.IsMatch(id);

    /// <summary>A new secret key for this PC (64 hex characters).</summary>
    public static string NewKey() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static bool IsKey(string? key) => key != null && Regex.IsMatch(key, "^[0-9a-fA-F]{64}$");

    public sealed record Identity(string Id, string Key, string Name, string? Skin);

    /// <summary>Answer of the server: its state for this player, or an error code ("taken", "not_found"…, "offline").</summary>
    public sealed record Result(JsonElement? State, string? Error, HttpStatusCode? Status);

    public static async Task<Result> CallAsync(string op, Identity me, IDictionary<string, object?>? extra = null, CancellationToken cancel = default)
    {
        var body = new Dictionary<string, object?> { ["id"] = me.Id.ToLowerInvariant(), ["key"] = me.Key.ToLowerInvariant(), ["name"] = me.Name, ["skin"] = me.Skin };
        if (extra != null)
        {
            foreach (var (k, v) in extra)
            {
                body[k] = v;
            }
        }
        try
        {
            using var res = await Http.PostAsJsonAsync($"{BaseUrl}/api/friends/{op}", body, cancel).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            JsonElement? json = null;
            try
            {
                json = JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException)
            {
                // not JSON (proxy page, server down): offline
            }
            if (res.IsSuccessStatusCode && json is { ValueKind: JsonValueKind.Object })
            {
                return new Result(json, null, res.StatusCode);
            }
            var error = json is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            return new Result(null, error ?? "offline", res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new Result(null, "offline", null);
        }
    }

    /// <summary>Error code of the server in the player's language.</summary>
    public static string Explain(string? error) => error switch
    {
        "not_found" => L.T("personne avec ce pseudo n'a l'app (ou n'a encore joué aucune partie avec)", "nobody with this name has the app (or played a match with it yet)", "nadie con ese nombre tiene la app (o aún no jugó ninguna partida con ella)"),
        "self" => L.T("c'est toi !", "that's you!", "¡eres tú!"),
        "name" => L.T("pseudo trop court", "name too short", "nombre demasiado corto"),
        "taken" => L.T("ce compte Epic est déjà lié à un autre PC : colle sa clé dans « Lier ce PC »", "this Epic account is already linked to another PC: paste its key in “Link this PC”", "esta cuenta de Epic ya está vinculada a otro PC: pega su clave en «Vincular este PC»"),
        "not_friends" => L.T("vous devez d'abord être potes", "you must be friends first", "primero tenéis que ser amigos"),
        "squad_full" => L.T("l'escouade est pleine (4 joueurs)", "the squad is full (4 players)", "el escuadrón está lleno (4 jugadores)"),
        "too_many" => L.T("100 potes au maximum", "100 friends at most", "100 amigos como máximo"),
        "no_invite" or "no_request" => L.T("cette invitation n'existe plus", "this invitation no longer exists", "esta invitación ya no existe"),
        "offline" => L.T("serveur injoignable, réessaie dans un moment", "server unreachable, try again in a moment", "servidor inaccesible, inténtalo en un momento"),
        _ => error ?? "?",
    };

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FortniteChestTracker/{AppInfo.VersionText}");
        return http;
    }
}
