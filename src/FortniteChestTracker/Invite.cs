using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FortniteChestTracker;

/// <summary>
/// Friend invite links: https://fortnite-chest-tracker.com/ami/&lt;Epic name&gt; (site/ami.html) offers to open the app with
/// fctracker://ami/&lt;Epic name&gt;. Windows starts the exe with "--invite" and that address, and nothing else is read from
/// the command line then (a crafted link must not add options). The name waits in the app (a counter already running
/// gets it through <see cref="PendingFile"/> and a signal); the Friends tab shows it with an "Add" button: the request
/// is only sent by the player's click, never by the page address (another website could set that).
/// </summary>
public static class Invite
{
    public const string Scheme = "fctracker";

    /// <summary>The link a player shares: their Epic name on the website.</summary>
    public static string LinkFor(string epicName) => $"https://{AppInfo.SiteName}/ami/{Uri.EscapeDataString(epicName)}";

    /// <summary>The arguments to use: only the invite link when there is one (everything else ignored).</summary>
    public static string[] Sanitize(string[] args)
    {
        var link = args.FirstOrDefault(a => a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));
        return link != null || args.Contains("--invite") ? (link != null ? new[] { link } : Array.Empty<string>()) : args;
    }

    /// <summary>An Epic name as sent in a link: 3 to 32 characters, nothing that could break out of a text.</summary>
    public static string? CleanName(string? name)
    {
        name = name?.Trim();
        return name != null && name.Length is >= 3 and <= 32 && !name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format
            || c is '<' or '>' or '"' or '#' or '/' or '\\') ? name : null;
    }

    /// <summary>A name received while the counter was already running (written by the second launch).</summary>
    public static string PendingFile(string dataFolder) => Path.Combine(dataFolder, "invite.txt");

    /// <summary>Signal of the second launch to the running counter (per data folder).</summary>
    public static string SignalName(string dataFolder) => AppInfo.InstanceMutexFor(dataFolder, @"Local\FortniteChestTracker.Invite.");

    /// <summary>The Epic name of a fctracker://ami/&lt;name&gt; argument (null when there is none or it is not a name).</summary>
    public static string? NameFromArgs(IEnumerable<string> args)
    {
        foreach (var a in args)
        {
            if (!a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rest = a[(Scheme.Length + 1)..].TrimStart('/');
            if (!rest.StartsWith("ami/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string name;
            try
            {
                name = Uri.UnescapeDataString(rest[4..].TrimEnd('/')).Trim();
            }
            catch (UriFormatException)
            {
                continue;
            }
            if (CleanName(name) is { } clean)
            {
                return clean;
            }
        }
        return null;
    }

    private const string Key = @"Software\Classes\" + Scheme;

    /// <summary>Makes fctracker:// links open this exe (per user, nothing needs admin rights). Writes nothing when already right.</summary>
    [SupportedOSPlatform("windows")]
    public static void Register(string exe)
    {
        var command = $"\"{exe}\" --invite \"%1\"";
        using (var existing = Registry.CurrentUser.OpenSubKey(Key + @"\shell\open\command"))
        {
            if (existing?.GetValue(null) as string == command)
            {
                return;
            }
        }
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        key.SetValue(null, "URL:Fortnite - Chest Tracker");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon", writable: true))
        {
            icon.SetValue(null, $"\"{exe}\",0");
        }
        using var cmd = key.CreateSubKey(@"shell\open\command", writable: true);
        cmd.SetValue(null, command);
    }

    /// <summary>Removed with the app (uninstall), only when it points to <paramref name="exe"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static void Unregister(string exe)
    {
        try
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(Key + @"\shell\open\command"))
            {
                if (existing?.GetValue(null) is not string command || !command.Contains(exe, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
        }
        catch
        {
            // nothing to clean
        }
    }
}
