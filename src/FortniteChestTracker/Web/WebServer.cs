using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FortniteChestTracker.Web;

/// <summary>Answer of a command sent by a button of the web interface.</summary>
public sealed record CommandResult(bool Ok, string? Error = null, object? Data = null)
{
    public static CommandResult Done { get; } = new(true);
    public static CommandResult Fail(string error) => new(false, error);
}

/// <summary>
/// The local web server of the interface (http.sys through HttpListener: no admin rights needed for localhost).
/// Only this PC can reach it, and since any web page open in the browser can send requests to localhost:
/// - the Host header must be our own address (blocks DNS rebinding: another site cannot read the data);
/// - every command needs the secret token written into the page, which other sites cannot read.
/// The state is pushed to the page with Server-Sent Events: the page updates by itself.
/// </summary>
public sealed class WebServer : IDisposable
{
    public const int DefaultPort = 5317;
    private const int MaxBody = 64 * 1024;

    private readonly Func<(long Version, string Json)> _state;
    private readonly Func<string, JsonElement, Task<CommandResult>> _command;
    private readonly Func<string, string?> _texts;
    private HttpListener _listener = new();
    private readonly List<SemaphoreSlim> _clients = new();
    private readonly CancellationTokenSource _stop = new();
    private static readonly JsonSerializerOptions JsonOptions = UiJson.Options;

    public int Port { get; private set; }

    /// <summary>Address opened in the browser.</summary>
    public string Url => $"http://127.0.0.1:{Port}/";

    /// <summary>Secret of this run, written into the page: required by every command.</summary>
    /// <summary>The overlay as a PNG (GET /hud.png, shown as a preview by the page). Null = none.</summary>
    public Func<byte[]?>? HudPng { get; init; }

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <param name="state">Latest state (version + JSON), read from any thread.</param>
    /// <param name="command">Runs a command (name, JSON arguments).</param>
    /// <param name="texts">Texts of the interface for a language (JSON), or null.</param>
    public WebServer(Func<(long, string)> state, Func<string, JsonElement, Task<CommandResult>> command, Func<string, string?> texts)
    {
        _state = state;
        _command = command;
        _texts = texts;
    }

    /// <summary>Starts on <paramref name="preferredPort"/>, or the next free one. Returns false when none is free.</summary>
    public bool Start(int preferredPort = DefaultPort)
    {
        for (var port = preferredPort; port < preferredPort + 20; port++)
        {
            // A listener whose Start failed is disposed by .NET: a new one for every attempt.
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                _listener = listener;
                Port = port;
                _ = Task.Run(AcceptLoop);
                return true;
            }
            catch (HttpListenerException)
            {
                // port taken (another program, or another counter with its own data folder): try the next one
                try { listener.Close(); } catch { }
            }
        }
        return false;
    }

    /// <summary>Tells the open pages that the state changed (they receive it at once).</summary>
    public void Notify()
    {
        lock (_clients)
        {
            foreach (var c in _clients)
            {
                if (c.CurrentCount == 0)
                {
                    c.Release();
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        Notify();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return; // stopped
            }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private bool HostAllowed(HttpListenerRequest req)
    {
        var host = req.Headers["Host"] ?? "";
        return host == $"127.0.0.1:{Port}" || host == $"localhost:{Port}";
    }

    private bool OriginAllowed(HttpListenerRequest req)
    {
        var origin = req.Headers["Origin"];
        return origin == null || origin == $"http://127.0.0.1:{Port}" || origin == $"http://localhost:{Port}";
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Referrer-Policy"] = "no-referrer";
            if (!HostAllowed(req))
            {
                await Text(res, 403, "text/plain", "Forbidden");
                return;
            }
            var path = req.Url?.AbsolutePath ?? "/";
            switch (req.HttpMethod, path)
            {
                case ("GET", "/"):
                case ("GET", "/index.html"):
                {
                    var html = Asset("index.html")?.Replace("{{TOKEN}}", Token).Replace("{{VERSION}}", AppInfo.VersionText);
                    res.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self' blob:; frame-ancestors 'none'";
                    res.Headers["X-Frame-Options"] = "DENY";
                    await Text(res, html != null ? 200 : 500, "text/html; charset=utf-8", html ?? "Missing interface");
                    return;
                }
                case ("GET", "/app.js"):
                    await Text(res, 200, "text/javascript; charset=utf-8", Asset("app.js") ?? "");
                    return;
                case ("GET", "/scene.js"):
                    await Text(res, 200, "text/javascript; charset=utf-8", Asset("scene.js") ?? "");
                    return;
                case ("GET", _) when path.StartsWith("/vendor/three/", StringComparison.Ordinal):
                {
                    // three.js (MIT, see /vendor/three/LICENSE.txt), embedded: the page works without Internet.
                    var file = path["/vendor/three/".Length..];
                    var bytes = file is "three.module.js" or "three.core.js" or "GLTFLoader.js" or "BufferGeometryUtils.js" or "SkeletonUtils.js" or "LICENSE.txt"
                        ? AssetBytes("vendor.three." + file) : null;
                    if (bytes == null)
                    {
                        await Text(res, 404, "text/plain", "Not found");
                        return;
                    }
                    await Binary(req, res, bytes, file.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript; charset=utf-8" : "text/plain; charset=utf-8");
                    return;
                }
                case ("GET", _) when path.StartsWith("/models/", StringComparison.Ordinal):
                {
                    // 3D models of the scene (Kenney, CC0, see /models/LICENSE-Kenney.txt), embedded.
                    var file = path["/models/".Length..];
                    var valid = file.Length is > 0 and < 40 && file.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.')
                        && (file.EndsWith(".glb", StringComparison.Ordinal) || file == "LICENSE-Kenney.txt") && !file.Contains("..", StringComparison.Ordinal);
                    var bytes = valid ? AssetBytes("models." + file) : null;
                    if (bytes == null)
                    {
                        await Text(res, 404, "text/plain", "Not found");
                        return;
                    }
                    await Binary(req, res, bytes, file.EndsWith(".glb", StringComparison.Ordinal) ? "model/gltf-binary" : "text/plain; charset=utf-8");
                    return;
                }
                case ("GET", _) when path.StartsWith("/img/", StringComparison.Ordinal):
                {
                    // Pictures of the interface (favicon, chests, pirate token), embedded.
                    var file = path["/img/".Length..];
                    var bytes = file is "favicon.ico" or "relic-chest.webp" or "token.webp" or "chest-pirate.webp" or "chest-pirate-gold.webp" or "overlay-bg.webp"
                        || (file.StartsWith("season-", StringComparison.Ordinal) && file.EndsWith(".webp", StringComparison.Ordinal) && !file.Contains('/') && !file.Contains(".."))
                        ? AssetBytes("img." + file) : null;
                    if (bytes == null)
                    {
                        await Text(res, 404, "text/plain", "Not found");
                        return;
                    }
                    await Binary(req, res, bytes, file.EndsWith(".ico", StringComparison.Ordinal) ? "image/x-icon" : "image/webp");
                    return;
                }
                case ("GET", _) when path.StartsWith("/cursors/", StringComparison.Ordinal):
                {
                    // Mouse cursors (Kenney Cursor Pack, CC0, see /cursors/LICENSE-Kenney.txt), embedded.
                    var file = path["/cursors/".Length..];
                    var bytes = file is "pointer.png" or "hand.png" or "pickaxe.png" or "LICENSE-Kenney.txt" ? AssetBytes("cursors." + file) : null;
                    if (bytes == null)
                    {
                        await Text(res, 404, "text/plain", "Not found");
                        return;
                    }
                    await Binary(req, res, bytes, file.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "text/plain; charset=utf-8");
                    return;
                }
                case ("GET", _) when path.StartsWith("/fonts/", StringComparison.Ordinal):
                {
                    // Anton (SIL Open Font License, see /fonts/OFL.txt), embedded for titles and numbers.
                    var file = path["/fonts/".Length..];
                    var bytes = file is "Anton-Regular.ttf" or "OFL.txt" ? AssetBytes("fonts." + file) : null;
                    if (bytes == null)
                    {
                        await Text(res, 404, "text/plain", "Not found");
                        return;
                    }
                    await Binary(req, res, bytes, file.EndsWith(".ttf", StringComparison.Ordinal) ? "font/ttf" : "text/plain; charset=utf-8");
                    return;
                }
                case ("GET", "/season.json"):
                    await Text(res, 200, "application/json; charset=utf-8", Season.Json);
                    return;
                case ("GET", "/notices"):
                {
                    // Licences of the code and assets this tool is built with (THIRD-PARTY-NOTICES.txt), linked from the About card.
                    using var stream = typeof(WebServer).Assembly.GetManifestResourceStream("FortniteChestTracker.notices.txt");
                    using var reader = stream != null ? new StreamReader(stream, Encoding.UTF8) : null;
                    var notices = reader?.ReadToEnd();
                    await Text(res, notices != null ? 200 : 404, "text/plain; charset=utf-8", notices ?? "Not found");
                    return;
                }
                case ("GET", "/app.css"):
                    await Text(res, 200, "text/css; charset=utf-8", Asset("app.css") ?? "");
                    return;
                case ("GET", "/favicon.ico"):
                {
                    var icon = AppIcon.Bytes();
                    if (icon == null)
                    {
                        res.StatusCode = 404;
                        res.Close();
                        return;
                    }
                    res.ContentType = "image/x-icon";
                    res.Headers["Cache-Control"] = "max-age=86400";
                    res.ContentLength64 = icon.Length;
                    await res.OutputStream.WriteAsync(icon);
                    res.Close();
                    return;
                }
                case ("GET", "/hud.png"):
                {
                    var png = HudPng?.Invoke();
                    if (png == null)
                    {
                        await Text(res, 404, "text/plain", "Not found");
                        return;
                    }
                    res.ContentType = "image/png";
                    res.Headers["Cache-Control"] = "private, max-age=600"; // the page asks /hud.png?v=<what is drawn>
                    res.ContentLength64 = png.Length;
                    await res.OutputStream.WriteAsync(png);
                    res.Close();
                    return;
                }
                case ("GET", "/api/state"):
                    await Text(res, 200, "application/json; charset=utf-8", _state().Json);
                    return;
                case ("GET", "/api/texts"):
                {
                    var json = _texts(req.QueryString["lang"] ?? "");
                    await Text(res, json != null ? 200 : 404, "application/json; charset=utf-8", json ?? "{}");
                    return;
                }
                case ("GET", "/api/events"):
                    await Events(res);
                    return;
                case ("POST", _) when path.StartsWith("/api/cmd/", StringComparison.Ordinal):
                    await Command(req, res, path["/api/cmd/".Length..]);
                    return;
                default:
                    await Text(res, 404, "text/plain", "Not found");
                    return;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // the page was closed meanwhile
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Web interface: {req.HttpMethod} {req.Url?.AbsolutePath}", ex);
            try { await Text(res, 500, "text/plain", "Error"); } catch { }
        }
    }

    private async Task Command(HttpListenerRequest req, HttpListenerResponse res, string name)
    {
        if (!OriginAllowed(req) || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(req.Headers["X-Tracker-Token"] ?? ""), Encoding.UTF8.GetBytes(Token)))
        {
            await Json(res, 403, CommandResult.Fail("forbidden"));
            return;
        }
        if (req.ContentLength64 > MaxBody)
        {
            await Json(res, 413, CommandResult.Fail("too large"));
            return;
        }

        JsonElement args;
        try
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var buffer = new char[MaxBody + 1];
            var read = 0;
            int n;
            while (read <= MaxBody && (n = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read))) > 0)
            {
                read += n;
            }
            if (read > MaxBody)
            {
                await Json(res, 413, CommandResult.Fail("too large"));
                return;
            }
            var body = new string(buffer, 0, read);
            if (string.IsNullOrWhiteSpace(body))
            {
                args = default;
            }
            else
            {
                using var doc = JsonDocument.Parse(body);
                args = doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            await Json(res, 400, CommandResult.Fail("bad json"));
            return;
        }

        var result = await _command(name, args);
        await Json(res, 200, result);
    }

    /// <summary>Server-Sent Events: the state at once, then every time it changes (a comment every 20 s keeps it alive).</summary>
    private async Task Events(HttpListenerResponse res)
    {
        res.StatusCode = 200;
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers["Cache-Control"] = "no-store";
        res.SendChunked = true;
        var signal = new SemaphoreSlim(0);
        lock (_clients)
        {
            _clients.Add(signal);
        }
        try
        {
            var stream = res.OutputStream;
            long sent = -1;
            await stream.WriteAsync(Encoding.UTF8.GetBytes("retry: 1500\n\n"));
            while (!_stop.IsCancellationRequested)
            {
                var (version, json) = _state();
                if (version != sent)
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"event: state\ndata: {json}\n\n"));
                    sent = version;
                }
                else
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(": ping\n\n"));
                }
                await stream.FlushAsync();
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(20), _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            lock (_clients)
            {
                _clients.Remove(signal);
            }
            try { res.Close(); } catch { }
        }
    }

    private static async Task Text(HttpListenerResponse res, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = type;
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static Task Json(HttpListenerResponse res, int status, CommandResult result) =>
        Text(res, status, "application/json; charset=utf-8", JsonSerializer.Serialize(result, JsonOptions));

    /// <summary>
    /// Sends an embedded file with an ETag (hash of its content) and "no-cache": the browser checks it every time and
    /// gets a 304 when it did not change, so a new version of the exe never shows old models or scripts.
    /// </summary>
    private static async Task Binary(HttpListenerRequest req, HttpListenerResponse res, byte[] bytes, string type)
    {
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes), 0, 12) + "\""; // about a millisecond for the biggest file
        res.Headers["Cache-Control"] = "no-cache";
        res.Headers["ETag"] = etag;
        if (req.Headers["If-None-Match"] == etag)
        {
            res.StatusCode = 304;
            res.Close();
            return;
        }
        res.ContentType = type;
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    /// <summary>A binary file of the interface (fonts), embedded in the exe.</summary>
    private static byte[]? AssetBytes(string name)
    {
        using var stream = typeof(WebServer).Assembly.GetManifestResourceStream("FortniteChestTracker.wwwroot." + name);
        if (stream == null)
        {
            return null;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>A file of the interface (Web/wwwroot, embedded in the exe).</summary>
    private static string? Asset(string name)
    {
        using var stream = typeof(WebServer).Assembly.GetManifestResourceStream("FortniteChestTracker.wwwroot." + name);
        if (stream == null)
        {
            return null;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public static class UiJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
