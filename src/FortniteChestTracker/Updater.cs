using System.Security.Cryptography;
using System.Text.Json;

namespace FortniteChestTracker;

/// <param name="SignatureUrl">The ".sig" file published with the exe: its signature by the release key (<see cref="Updater.ReleaseKey"/>).</param>
public sealed record UpdateInfo(Version Version, string Tag, string AssetUrl, long Size, string Sha256, string PageUrl, string SignatureUrl = "");

/// <summary>
/// Checks the latest GitHub release and replaces the running exe with it. The running exe is renamed (Windows allows
/// that), the new one takes its place and is started; the old file is deleted at the next launch.
/// This is the only network access of the tool, and it can be turned off in the settings.
/// An exe is only installed when it is signed with the release key: the private key lives only in the release job of
/// GitHub Actions (secret UPDATE_SIGNING_KEY) and with the author; a release uploaded any other way (stolen GitHub
/// token, hijacked build step) is refused, even with a correct SHA-256 (GitHub computes that for any file).
/// </summary>
public static class Updater
{
    /// <summary>Public key of the releases (ECDSA P-256, SubjectPublicKeyInfo, base64).</summary>
    internal const string ReleaseKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEkdN5a2OULwXBp2DEaOpZwPWBRSYDMKoU96WwMvZItWYrBdZV+Ft61EZY4veXWAOUtXPYZOGp3uBsq/MxZ1bSJQ==";

    /// <summary>True when <paramref name="signature"/> (base64 of a DER signature, as made by openssl dgst -sign) signs <paramref name="data"/> with this public key.</summary>
    internal static bool SignatureValid(byte[] data, string signature, string publicKey = ReleaseKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(data, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return false;
        }
    }

    // FCT_UPDATE_API (tests) only counts in a local build: a published exe always asks its own repository.
    private static string? TestApi => AppInfo.UpdatesAvailable ? null : Environment.GetEnvironmentVariable("FCT_UPDATE_API");

    private static string ApiUrl => TestApi ?? $"https://api.github.com/repos/{AppInfo.UpdateRepo}/releases/latest";

    /// <summary>
    /// A client fully set up before its first request: an HttpClient refuses any change of its properties once it sent
    /// one (1.3.1 and 1.3.2 changed MaxResponseContentBufferSize between the exe and its signature, and every update
    /// failed with "This instance has already started one or more requests").
    /// </summary>
    private static HttpClient NewClient(TimeSpan timeout, HttpMessageHandler? handler = null, long? maxBuffer = null)
    {
        var http = handler != null ? new HttpClient(handler, disposeHandler: false) { Timeout = timeout } : new HttpClient { Timeout = timeout };
        if (maxBuffer is { } max)
        {
            http.MaxResponseContentBufferSize = max;
        }
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FortniteChestTracker/{AppInfo.VersionText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// The newer release (or null), and whether GitHub answered at all: lets the settings page tell
    /// "you already have the latest version" apart from "GitHub cannot be reached".
    /// </summary>
    public static async Task<(UpdateInfo? Update, bool Reached)> CheckDetailedAsync()
    {
        if (!AppInfo.UpdatesAvailable && TestApi == null)
        {
            return (null, false); // local build: no repository to update from
        }
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync(ApiUrl);
            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }
            return (Parse(await response.Content.ReadAsStringAsync(), AppInfo.Version), true);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>
    /// The release described by the GitHub API answer, if it is newer than <paramref name="current"/> and carries
    /// the exe with its SHA-256 digest (GitHub computes it for every asset): an exe that cannot be verified is never
    /// offered. The exe is the asset <see cref="AppInfo.ExeName"/>, else one under the 1.0.x name.
    /// </summary>
    internal static UpdateInfo? Parse(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()
                || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            {
                return null;
            }
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(current))
            {
                return null;
            }
            var assets = root.GetProperty("assets").EnumerateArray().ToList();
            var index = new[] { AppInfo.ExeName, Legacy.AssetName }
                .Select(name => assets.FindIndex(a => string.Equals(a.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(i => i >= 0, -1);
            if (index >= 0)
            {
                var asset = assets[index];
                if (!asset.TryGetProperty("digest", out var digest) || digest.GetString() is not { } d
                    || !d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || d.Length != 7 + 64)
                {
                    return null;
                }
                var url = asset.GetProperty("browser_download_url").GetString();
                if (url == null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                // A release without the signature of its exe is never offered (see ReleaseKey)
                var sigName = asset.GetProperty("name").GetString() + ".sig";
                var sig = assets.FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), sigName, StringComparison.OrdinalIgnoreCase));
                var sigUrl = sig.ValueKind == JsonValueKind.Object ? sig.GetProperty("browser_download_url").GetString() : null;
                if (sigUrl == null || !sigUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                var pageUrl = root.TryGetProperty("html_url", out var page) ? page.GetString() : null;
                return new UpdateInfo(version, tag, url, asset.GetProperty("size").GetInt64(), d[7..],
                    pageUrl != null && pageUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ? pageUrl : AppInfo.RepoUrl, sigUrl);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Downloads and puts the new exe in place. Returns null when done, else the reason it failed.</summary>
    public static string? Install(UpdateInfo update)
    {
        var exe = AppInfo.ExePath;
        if (exe == null)
        {
            return L.T("mise à jour possible seulement depuis FortniteChestTracker.exe", "update only possible from FortniteChestTracker.exe", "actualización posible solo desde FortniteChestTracker.exe");
        }
        var download = exe + ".download";
        var old = exe + ".old";
        try
        {
            DownloadVerified(update, download);

            // The standby process may run the current exe: stop it (it is restarted from the new exe).
            if (OperatingSystem.IsWindows())
            {
                FortniteWatch.StopStandby();
            }
            if (File.Exists(old))
            {
                try { File.Delete(old); } catch { old = exe + $".{DateTime.Now:yyyyMMddHHmmss}.old"; }
            }
            File.Move(exe, old);
            File.Move(download, exe);
            return null;
        }
        catch (Exception ex)
        {
            try { File.Delete(download); } catch { }
            // The current exe was already renamed: put it back, or the shortcut and the Run key point to nothing.
            // (the "open with Fortnite" watcher is started again by the counter at its next start)
            try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
            return ex.Message;
        }
    }

    /// <summary>
    /// Downloads the exe to <paramref name="download"/> and checks it: size, "MZ" header, SHA-256 and the release
    /// signature (a small ".sig" file, read by its own client with a small buffer). Throws when anything is wrong.
    /// </summary>
    internal static void DownloadVerified(UpdateInfo update, string download, HttpMessageHandler? handler = null, string publicKey = ReleaseKey)
    {
        using (var http = NewClient(TimeSpan.FromMinutes(5), handler))
        using (var source = http.GetStreamAsync(update.AssetUrl).GetAwaiter().GetResult())
        using (var target = File.Create(download))
        {
            // never more than the size announced (a bigger answer is not the release)
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > update.Size)
                {
                    throw new IOException(L.T("téléchargement trop gros", "download too big", "descarga demasiado grande"));
                }
                target.Write(buffer, 0, n);
            }
        }
        string signature;
        using (var http = NewClient(TimeSpan.FromMinutes(1), handler, maxBuffer: 4096))
        {
            signature = http.GetStringAsync(update.SignatureUrl).GetAwaiter().GetResult();
        }

        var info = new FileInfo(download);
        if (info.Length != update.Size)
        {
            throw new IOException(L.T("téléchargement incomplet", "incomplete download", "descarga incompleta"));
        }
        using (var fs = File.OpenRead(download))
        {
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
            {
                throw new IOException(L.T("le fichier téléchargé n'est pas un exe", "the downloaded file is not an exe", "el archivo descargado no es un exe"));
            }
            fs.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(fs));
            if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(L.T("empreinte SHA-256 incorrecte", "wrong SHA-256 checksum", "suma de comprobación SHA-256 incorrecta"));
            }
        }
        if (!SignatureValid(File.ReadAllBytes(download), signature, publicKey))
        {
            throw new IOException(L.T("signature de la mise à jour invalide : elle n'est pas installée", "invalid update signature: it is not installed", "firma de la actualización no válida: no se instala"));
        }
    }

    /// <summary>
    /// Deletes the files left by the previous update (also under the 1.0.x file name), and the installed copy of 1.0.x
    /// once it was renamed.
    /// </summary>
    public static void CleanupOld()
    {
        var exe = AppInfo.ExePath;
        var dir = exe != null ? Path.GetDirectoryName(exe) : null;
        if (dir == null)
        {
            return;
        }
        foreach (var name in new[] { Path.GetFileName(exe!), Legacy.ExeName }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var f in Directory.EnumerateFiles(dir, name + ".*"))
            {
                if (f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(f); } catch { /* still in use: next time */ }
                }
            }
        }
        if (OperatingSystem.IsWindows())
        {
            Installer.DeleteLegacyExe(exe);
        }
    }
}
