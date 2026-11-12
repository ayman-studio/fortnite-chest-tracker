using System.Runtime.Versioning;
using Microsoft.Win32;

namespace FortniteChestTracker;

/// <summary>
/// Friend invite links: https://fortnite-chest-tracker.com/ami/&lt;Epic name&gt; (site/ami.html) offers to open the app with
/// fctracker://ami/&lt;Epic name&gt;. Windows starts the exe with that address; the interface then opens on the Friends tab
/// with the friend request ready (#ami=&lt;name&gt; in the page address), the player confirms it.
/// </summary>
public static class Invite
{
    public const string Scheme = "fctracker";

    /// <summary>The link a player shares: their Epic name on the website.</summary>
    public static string LinkFor(string epicName) => $"https://{AppInfo.SiteName}/ami/{Uri.EscapeDataString(epicName)}";

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
            if (name.Length is >= 3 and <= 32 && !name.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '#' or '/' or '\\'))
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>Address fragment of the interface that opens the Friends tab with the request ready.</summary>
    public static string Fragment(string name) => "#ami=" + Uri.EscapeDataString(name);

    private const string Key = @"Software\Classes\" + Scheme;

    /// <summary>Makes fctracker:// links open this exe (per user, nothing needs admin rights). Writes nothing when already right.</summary>
    [SupportedOSPlatform("windows")]
    public static void Register(string exe)
    {
        var command = $"\"{exe}\" \"%1\"";
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
