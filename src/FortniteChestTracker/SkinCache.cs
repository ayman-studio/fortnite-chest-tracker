using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace FortniteChestTracker;

/// <summary>
/// Outfit icons of the players (the squad overlay and the Friends tab), kept in the data folder ("skins\&lt;id&gt;.png").
/// Missing ones are downloaded once from the tool's website (/api/skin/&lt;id&gt;.png, which caches the public icons of
/// fortnite-api.com): the app never talks to another site, and only the outfit id is sent.
/// </summary>
public static class SkinCache
{
    private static readonly Regex ValidId = new("^[A-Za-z0-9_-]{1,120}$", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, Img?> Images = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> Failed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Downloading = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = CreateClient();
    private static string? _folder;
    private static string _baseUrl = "";

    /// <summary>Raised (on a worker thread) when an icon was downloaded.</summary>
    public static event Action? Downloaded;

    /// <summary>Number of icons downloaded since the start (the page reloads its pictures when it changes).</summary>
    public static int Version { get; private set; }

    public static void Init(string dataFolder, string baseUrl)
    {
        _folder = Path.Combine(dataFolder, "skins");
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public static bool IsValid(string? id) => id != null && ValidId.IsMatch(id);

    private static string? FileOf(string id) => _folder == null ? null : Path.Combine(_folder, id.ToLowerInvariant() + ".png");

    /// <summary>The icon file when it is on disk (null otherwise: it is then downloaded in the background).</summary>
    public static string? PathOf(string? id)
    {
        if (!IsValid(id) || FileOf(id!) is not { } file)
        {
            return null;
        }
        if (File.Exists(file))
        {
            return file;
        }
        Want(id);
        return null;
    }

    /// <summary>Downloads the icon in the background when it is missing (failures wait an hour before another try).</summary>
    public static void Want(string? id)
    {
        if (!IsValid(id) || FileOf(id!) is not { } file || File.Exists(file) || _baseUrl.Length == 0)
        {
            return;
        }
        if (Failed.TryGetValue(id!, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1))
        {
            return;
        }
        if (!Downloading.TryAdd(id!, 0))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync($"{_baseUrl}/api/skin/{Uri.EscapeDataString(id!)}.png").ConfigureAwait(false);
                if (bytes.Length > 400_000 || Png.Decode(bytes) is not { Width: > 0 and <= 512, Height: > 0 and <= 512 })
                {
                    throw new InvalidDataException("not a small PNG");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var tmp = file + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                File.Move(tmp, file, overwrite: true);
                Images.TryRemove(id!, out _);
                Version++;
                Downloaded?.Invoke();
            }
            catch
            {
                Failed[id!] = DateTime.UtcNow; // unknown outfit, offline…: the page shows a placeholder
            }
            finally
            {
                Downloading.TryRemove(id!, out _);
            }
        });
    }

    /// <summary>The icon as a picture for the overlay (null while it is not on disk).</summary>
    internal static Img? Image(string? id)
    {
        if (!IsValid(id))
        {
            return null;
        }
        if (Images.TryGetValue(id!, out var cached))
        {
            return cached;
        }
        var file = PathOf(id);
        if (file == null)
        {
            return null; // not cached as null: it may arrive soon
        }
        Img? img = null;
        try
        {
            img = Img.FromPng(File.ReadAllBytes(file));
        }
        catch
        {
            // unreadable file: placeholder
        }
        Images[id!] = img;
        return img;
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FortniteChestTracker/{AppInfo.VersionText}");
        return http;
    }
}
