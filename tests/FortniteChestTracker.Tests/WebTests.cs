using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FortniteChestTracker.Web;

namespace FortniteChestTracker.Tests;

public class UiTextsTests
{
    private static string Asset(string name)
    {
        using var s = typeof(WebServer).Assembly.GetManifestResourceStream("FortniteChestTracker.wwwroot." + name)
            ?? throw new InvalidOperationException(name + " not embedded");
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void Interface_files_are_embedded_in_the_exe()
    {
        Assert.Contains("{{TOKEN}}", Asset("index.html"));
        Assert.Contains("EventSource", Asset("app.js"));
        Assert.Contains(".card", Asset("app.css"));
        Assert.NotNull(AppIcon.Bytes());
    }

    [Fact]
    public void Every_text_used_by_the_page_exists_in_every_language()
    {
        var js = Asset("app.js");
        var used = new HashSet<string>();
        foreach (Match m in Regex.Matches(js, @"\bt\('([A-Za-z0-9_]+)'"))
        {
            used.Add(m.Groups[1].Value);
        }
        foreach (Match m in Regex.Matches(js, @"count\([^,()]+(?:\([^)]*\))?[^,()]*, '([a-z]+)'\)"))
        {
            used.Add(m.Groups[1].Value + "One");
            used.Add(m.Groups[1].Value + "Many");
        }
        foreach (Match m in Regex.Matches(Asset("index.html"), "data-t=\"([A-Za-z]+)\""))
        {
            used.Add(m.Groups[1].Value);
        }
        foreach (var mode in new[] { "prompt", "auto", "off" })
        {
            used.Add("upd_" + mode); // built as 'upd_' + mode
        }
        used.Remove("upd_");
        foreach (var tab in Regex.Match(js, @"const PAGES = \[([^\]]+)\]").Groups[1].Value.Split(',').Select(x => x.Trim().Trim('\'')))
        {
            used.Add("page_" + tab); // built as 'page_' + key
        }
        used.Remove("page_");
        foreach (var days in new[] { 7, 30, 90 }) used.Add("days" + days); // built as 'days' + range
        used.Remove("days");
        foreach (var o in TreasureOdds.All.Where(o => o.Key != "odds_gizmo")) used.Add("short_" + o.Key); // goal chips: 'short_' + key
        used.Remove("short_");
        foreach (var style in new[] { OverlayAppearance.Full, OverlayAppearance.Compact }) used.Add("overlayStyle_" + style); // 'overlayStyle_' + style
        used.Remove("overlayStyle_");
        foreach (var prize in Season.Current.Weeks.SelectMany(w => w.Prizes).Select(p => p.Key).Append("outfit")) used.Add("prize_" + prize); // season prizes: 'prize_' + kind
        used.Remove("prize_");
        for (var c = 0; c < 4; c++) used.Add("corner_" + c); // overlay corners: 'corner_' + index
        used.Remove("corner_");

        Assert.True(used.Count > 100, $"only {used.Count} keys found: the regex no longer matches the page");
        foreach (var lang in new[] { "fr", "en", "es" })
        {
            var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(UiTexts.Json(lang)!)!;
            var missing = used.Where(k => !texts.ContainsKey(k)).ToList();
            Assert.True(missing.Count == 0, $"{lang}: missing {string.Join(", ", missing)}");
            Assert.DoesNotContain(texts, kv => string.IsNullOrWhiteSpace(kv.Value));
        }
    }

    [Fact]
    public void Unknown_language_has_no_texts()
    {
        Assert.Null(UiTexts.Json("de"));
        Assert.Equal(UiTexts.Keys.Count(), UiTexts.Keys.Distinct().Count());
    }
}

public sealed class WebServerTests : IDisposable
{
    private readonly List<(string Name, JsonElement Args)> _commands = new();
    private long _version = 1;
    private string _json = """{"hello":"world"}""";
    private readonly WebServer _server;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public WebServerTests()
    {
        _server = new WebServer(() => (Interlocked.Read(ref _version), Volatile.Read(ref _json)),
            (name, args) =>
            {
                lock (_commands) _commands.Add((name, args));
                return Task.FromResult(new CommandResult(true, Data: name));
            },
            UiTexts.Json);
        // Several random places: Windows (and the GitHub runners) reserve whole blocks of ports for Hyper-V.
        var started = false;
        for (var i = 0; i < 12 && !started; i++)
        {
            started = _server.Start(20000 + Random.Shared.Next(40000));
        }
        Assert.True(started);
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
    }

    private HttpRequestMessage Cmd(string name, string? token, string body = "{}", string? origin = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _server.Url + "api/cmd/" + name) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token != null) req.Headers.Add("X-Tracker-Token", token);
        if (origin != null) req.Headers.Add("Origin", origin);
        return req;
    }

    [Fact]
    public async Task Page_carries_the_token_and_a_strict_policy()
    {
        var res = await _http.GetAsync(_server.Url, TestContext.Current.CancellationToken);
        var html = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains($"content=\"{_server.Token}\"", html);
        Assert.Contains("script-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public void Taken_port_falls_back_to_the_next_one()
    {
        // Another counter (another data folder) already on the port: the second one takes the next free port.
        using var second = new WebServer(() => (1, "{}"), (_, _) => Task.FromResult(CommandResult.Done), UiTexts.Json);
        Assert.True(second.Start(_server.Port));
        Assert.NotEqual(_server.Port, second.Port);
        Assert.InRange(second.Port, _server.Port + 1, _server.Port + 19);
    }

    [Fact]
    public async Task State_is_served()
    {
        Assert.Equal(_json, await _http.GetStringAsync(_server.Url + "api/state", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Command_with_the_token_runs()
    {
        var res = await _http.SendAsync(Cmd("scan", _server.Token, """{"x":1}"""), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"ok\":true", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var (name, args) = Assert.Single(_commands);
        Assert.Equal("scan", name);
        Assert.Equal(1, args.GetProperty("x").GetInt32());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("wrong-token", null)]
    [InlineData("TOKEN", "http://evil.example")]
    public async Task Commands_from_other_sites_are_refused(string? token, string? origin)
    {
        var res = await _http.SendAsync(Cmd("quit", token == "TOKEN" ? _server.Token : token, origin: origin), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task Same_origin_is_accepted()
    {
        var res = await _http.SendAsync(Cmd("scan", _server.Token, origin: $"http://127.0.0.1:{_server.Port}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Foreign_host_name_is_refused()
    {
        // DNS rebinding: a site pointing its own name at 127.0.0.1 must not be able to read the data.
        var req = new HttpRequestMessage(HttpMethod.Get, _server.Url + "api/state");
        req.Headers.Host = $"evil.example:{_server.Port}";
        var res = await _http.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Bad_json_is_refused()
    {
        var res = await _http.SendAsync(Cmd("scan", _server.Token, "{nope"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task Events_push_the_state_and_its_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var res = await _http.GetAsync(_server.Url + "api/events", HttpCompletionOption.ResponseHeadersRead, ct);
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));

        async Task<string> NextState()
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line != null && line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    return line["data: ".Length..];
                }
            }
        }

        Assert.Equal("""{"hello":"world"}""", await NextState());
        Volatile.Write(ref _json, """{"hello":"again"}""");
        Interlocked.Increment(ref _version);
        _server.Notify();
        Assert.Equal("""{"hello":"again"}""", await NextState());
    }

    [Fact]
    public async Task Texts_and_icon_are_served()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Contains("Waiting for matches", await _http.GetStringAsync(_server.Url + "api/texts?lang=en", ct));
        var icon = await _http.GetByteArrayAsync(_server.Url + "favicon.ico", ct);
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, icon[..4]);

        // 3D scene: the scene script and three.js (MIT) are embedded, so the page works offline.
        Assert.Contains("fct-scene", await _http.GetStringAsync(_server.Url + "scene.js", ct));
        Assert.Contains("REVISION", await _http.GetStringAsync(_server.Url + "vendor/three/three.core.js", ct));
        Assert.Contains("./three.core.js", await _http.GetStringAsync(_server.Url + "vendor/three/three.module.js", ct));
        Assert.Contains("MIT License", await _http.GetStringAsync(_server.Url + "vendor/three/LICENSE.txt", ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_server.Url + "vendor/three/other.js", ct)).StatusCode);

        // 3D models (Kenney, CC0) and the glTF loader, embedded; only plain .glb names are served.
        var model = await _http.GetAsync(_server.Url + "models/miner.glb", ct);
        Assert.Equal(HttpStatusCode.OK, model.StatusCode);
        Assert.Equal("model/gltf-binary", model.Content.Headers.ContentType?.MediaType);
        Assert.Equal("glTF"u8.ToArray(), (await model.Content.ReadAsByteArrayAsync(ct))[..4]);
        // Checked again every time (ETag + no-cache): a new exe never shows an old model.
        Assert.True(model.Headers.CacheControl?.NoCache);
        var again = new HttpRequestMessage(HttpMethod.Get, _server.Url + "models/miner.glb");
        again.Headers.TryAddWithoutValidation("If-None-Match", model.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotModified, (await _http.SendAsync(again, ct)).StatusCode);
        Assert.Contains("Creative Commons Zero", await _http.GetStringAsync(_server.Url + "models/LICENSE-Kenney.txt", ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_server.Url + "models/..%2Fapp.js", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_server.Url + "models/missing.glb", ct)).StatusCode);
        Assert.Contains("./three.module.js", await _http.GetStringAsync(_server.Url + "vendor/three/GLTFLoader.js", ct));

        // Pictures and the page's favicon
        var img = await _http.GetAsync(_server.Url + "img/token.webp", ct);
        Assert.Equal(HttpStatusCode.OK, img.StatusCode);
        Assert.Equal("image/webp", img.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(_server.Url + "img/favicon.ico", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_server.Url + "img/other.webp", ct)).StatusCode);

        // Mouse cursors (Kenney, CC0)
        var cursor = await _http.GetAsync(_server.Url + "cursors/hand.png", ct);
        Assert.Equal(HttpStatusCode.OK, cursor.StatusCode);
        Assert.Equal("image/png", cursor.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_server.Url + "cursors/other.png", ct)).StatusCode);

        // Title font (SIL OFL), embedded with its licence; nothing else is served from /fonts/.
        var font = await _http.GetAsync(_server.Url + "fonts/Anton-Regular.ttf", ct);
        Assert.Equal(HttpStatusCode.OK, font.StatusCode);
        Assert.Equal("font/ttf", font.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Open Font License", await _http.GetStringAsync(_server.Url + "fonts/OFL.txt", ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_server.Url + "fonts/..%2Fapp.js", ct)).StatusCode);

        // Licence notices of the third-party code and assets (linked from the About card), as plain text.
        var notices = await _http.GetAsync(_server.Url + "notices", ct);
        Assert.Equal(HttpStatusCode.OK, notices.StatusCode);
        Assert.Equal("text/plain", notices.Content.Headers.ContentType?.MediaType);
        var text = await notices.Content.ReadAsStringAsync(ct);
        Assert.Contains("Permission is hereby granted", text);
        Assert.Contains("three.js", text);
        Assert.Contains("Open Font License", text);
        Assert.Contains("Kenney", text);
    }
}

public class UiStateTests
{
    private static MatchRecord Match(DateTime start, int chests, string playlist = "Playlist_NoBuildBR_Duo", int treasures = 0) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        MatchStartUtc = start.ToUniversalTime(),
        Playlist = playlist,
        ChestsOpened = chests,
        Chests = Enumerable.Range(0, chests).Select(i => new ChestRecord { Kind = i % 3 == 0 ? ChestKinds.Rare : ChestKinds.Normal }).ToList(),
        TreasureFinds = Enumerable.Range(0, treasures).Select(_ => new TreasureFind { Item = "Trésor" }).ToList(),
    };

    [Fact]
    public void Stats_records_and_breakdowns()
    {
        L.Set("en");
        var today = DateTime.Now.Date.AddHours(12);
        var data = new TrackerData
        {
            Matches =
            {
                Match(today, 10),
                Match(today.AddHours(-1), 30, treasures: 2),
                Match(today.AddDays(-3), 5, "Playlist_NoBuildBR_Solo"),
                Match(today.AddDays(-40), 50), // outside the 30-day chart
            },
        };

        var stats = UiStateBuilder.Stats(data);
        Assert.Equal(95, stats.Total);
        Assert.Equal(4, stats.Matches);
        Assert.Equal(23.8, stats.Average);
        Assert.Equal(50, stats.Best);
        Assert.Equal(50, stats.BestDay);
        Assert.Equal(2, stats.Treasures);
        Assert.Equal(10, stats.LastChests);

        var days = UiStateBuilder.PerDay(data);
        Assert.Equal(30, days.Count);
        Assert.Equal(40, days[^1].Chests);
        Assert.Equal(2, days[^1].Matches);
        Assert.Equal(5, days[^4].Chests);
        Assert.Equal(45, days.Sum(d => d.Chests));

        var modes = UiStateBuilder.ByMode(data);
        Assert.Equal(2, modes.Count);
        Assert.Equal(3, modes[0].Matches);

        var kinds = UiStateBuilder.ByKind(data);
        Assert.Equal(95, kinds.Sum(k => k.Count));
        Assert.Contains(kinds, k => k.Name == "Rare chest");

        var matches = UiStateBuilder.Matches(data);
        Assert.Equal(10, matches[0].Chests);
        Assert.Equal(2, matches[1].Treasures.Count);
    }

    [Fact]
    public void Placement_is_compared_per_team_so_modes_compare()
    {
        var squad = new MatchRecord { Playlist = "Playlist_NoBuildBR_Squad", Placement = 4, TotalPlayers = 80 };
        var solo = new MatchRecord { Playlist = "Playlist_NoBuildBR_Solo", Placement = 4, TotalPlayers = 80 };
        Assert.Equal(0.2, UiStateBuilder.TopShare(squad)); // 4th of 20 squads
        Assert.Equal(0.05, UiStateBuilder.TopShare(solo));
        Assert.Null(UiStateBuilder.TopShare(new MatchRecord { Placement = 3 }));
        Assert.Equal(1, UiStateBuilder.TopShare(new MatchRecord { Playlist = "Playlist_DefaultSquad", Placement = 30, TotalPlayers = 100 }));
    }

    [Fact]
    public void Matches_grouped_by_chests_opened()
    {
        var start = DateTime.Now.Date;
        MatchRecord Ranked(int chests, int place, int? kills = 2)
        {
            var m = Match(start, chests, "Playlist_NoBuildBR_Solo");
            m.Placement = place;
            m.TotalPlayers = 100;
            m.Eliminations = kills;
            return m;
        }
        var data = new TrackerData
        {
            Matches =
            {
                Ranked(5, 50), Ranked(6, 50), Ranked(9, 50, null),
                Ranked(15, 20), Ranked(12, 20), Ranked(19, 20),
                Ranked(35, 1, 6), Ranked(41, 1, 6), Ranked(33, 4, 3),
                Ranked(25, 10), // alone in 20–29: left out
                Match(start, 60), // placement unknown: not counted anywhere
            },
        };

        var buckets = UiStateBuilder.ChestsVsPlacement(data);
        Assert.Equal(new (int, int?)[] { (0, 9), (10, 19), (30, null) }, buckets.Select(b => (b.From, b.To)));
        Assert.All(buckets, b => Assert.Equal(3, b.Matches));
        Assert.Equal(0.5, buckets[0].AverageTop);
        Assert.Equal(2, buckets[0].AverageKills); // the match without eliminations is ignored for the average
        Assert.Equal(0.2, buckets[1].AverageTop);
        Assert.Equal(0.02, buckets[2].AverageTop);
        Assert.Equal(0.667, buckets[2].WinRate);
        Assert.Equal(5, buckets[2].AverageKills);

        // Too few matches: nothing to compare
        Assert.Empty(UiStateBuilder.ChestsVsPlacement(new TrackerData { Matches = { Ranked(5, 50), Ranked(30, 1) } }));
    }

    [Fact]
    public void Chest_times_only_when_the_record_has_them()
    {
        var m = Match(DateTime.Now, 3);
        Assert.Null(UiStateBuilder.ChestTimes(m)); // records without times (all 0)
        m.Chests[0].TimeSeconds = 301.6f;
        m.Chests[1].TimeSeconds = 74.2f;
        m.Chests[2].TimeSeconds = 80f;
        Assert.Equal([74, 80, 302], UiStateBuilder.ChestTimes(m)!);
        Assert.Equal([74, 80, 302], UiStateBuilder.Matches(new TrackerData { Matches = { m } })[0].ChestTimes!);
    }

    [Fact]
    public void Empty_history()
    {
        var stats = UiStateBuilder.Stats(new TrackerData());
        Assert.Equal(0, stats.Total);
        Assert.Null(stats.LastChests);
        Assert.Null(stats.BestDayDate);
        Assert.All(UiStateBuilder.PerDay(new TrackerData()), d => Assert.Equal(0, d.Chests));
    }
}

public class ActivityTests
{
    [Fact]
    public void Keeps_the_latest_entries_with_increasing_ids()
    {
        for (var i = 0; i < 250; i++)
        {
            Activity.Info("entry " + i);
        }
        var all = Activity.Snapshot();
        Assert.Equal(200, all.Count);
        Assert.Equal("entry 249", all[^1].Text);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.Second.Id > p.First.Id));
    }
}

public sealed class PrizeNameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fct-prize-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Prize_is_named_and_saved()
    {
        L.Set("fr");
        var store = new Store(_dir);
        store.LoadPrizeNames();
        store.RegisterPrizeIds(new[] { "DelilahPrize:dpid_item9" });
        Assert.Contains(UiStateBuilder.Prizes(), p => p.Id == "DelilahPrize:dpid_item9" && p.Unnamed);

        store.SetPrizeName("delilahprize:DPID_ITEM9", "Pioche dorée");

        var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(store.PrizeNamesPath))!;
        Assert.Equal("Pioche dorée", saved["DelilahPrize:dpid_item9"]); // same key, whatever the case typed
        Assert.Equal("Pioche dorée", TreasureNames.Friendly("DelilahPrize:dpid_item9"));
        Assert.Contains(UiStateBuilder.Prizes(), p => p.Id == "DelilahPrize:dpid_item9" && !p.Unnamed);
    }
}
