using System.Text;

namespace FortniteChestTracker;

/// <summary>errors.log in the data folder: full details of unreadable replays and crashes, to attach to a bug report.</summary>
public static class ErrorLog
{
    public static string? FilePath { get; set; }
    private static readonly object Gate = new();

    public static void Write(string what, object error)
    {
        if (FilePath == null)
        {
            return;
        }
        lock (Gate)
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · Fortnite - Chest Tracker {AppInfo.VersionText} · {Environment.OSVersion.VersionString}");
            sb.AppendLine(what);
            sb.AppendLine(error.ToString());
            sb.AppendLine();
            File.AppendAllText(FilePath, Anonymize(sb.ToString()), new UTF8Encoding(false));

            // Keep the file small: only the most recent part.
            var info = new FileInfo(FilePath);
            if (info.Length > 1_000_000)
            {
                var text = File.ReadAllText(FilePath);
                File.WriteAllText(FilePath, text.Length > 500_000 ? text[^500_000..] : text[(text.Length / 2)..], new UTF8Encoding(false));
            }
        }
        catch
        {
            // logging must never stop the tool
        }
    }

    /// <summary>The file is meant to be attached to a public bug report: hide the Windows user name in paths.</summary>
    private static string Anonymize(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3)
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        var user = Environment.UserName;
        if (user.Length >= 3)
        {
            text = text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
