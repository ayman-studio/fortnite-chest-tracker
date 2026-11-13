namespace FortniteChestTracker;

/// <summary>Version, GitHub repository and names shared by the tool's processes.</summary>
public static class AppInfo
{
    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";

    /// <summary>File name of the published exe (also the name of the release asset).</summary>
    public const string ExeName = "FortniteChestTracker.exe";

    /// <summary>The project's own repository: links (GitHub, bug reports) when the build has no update repository.</summary>
    public const string ProjectRepo = "ayman-studio/fortnite-chest-tracker";

    /// <summary>Address of the tool's website, shown at the bottom of the overlays. Empty = not shown.</summary>
    public const string SiteName = "fortnite-chest-tracker.com";

    /// <summary>The author, credited in About (link opened by the "author" command).</summary>
    public const string AuthorUrl = "https://x.com/ayman_studioo";

    /// <summary>The app's own X account (news, Treasure Hour reminders), linked in About (opened by the "x" command).</summary>
    public const string XUrl = "https://x.com/chest_tracker";

    /// <summary>
    /// GitHub repository ("owner/name") whose releases update this exe, set at build time with
    /// <c>-p:TrackerRepo=owner/name</c> (the GitHub workflow passes its own repository). Null for a local build:
    /// updates are then unavailable, so that a modified build is never replaced by someone else's release.
    /// </summary>
    public static string? UpdateRepo { get; } = ReadUpdateRepo();

    public static bool UpdatesAvailable => UpdateRepo != null;

    internal static string? ReadUpdateRepo() =>
        typeof(AppInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepo")?.Value is { } repo && IsRepoName(repo.Trim()) ? repo.Trim() : null;

    /// <summary>"owner/name" made of GitHub-safe characters (anything else would end up in the API URL).</summary>
    internal static bool IsRepoName(string repo) =>
        System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$");

    public static string Repo => UpdateRepo ?? ProjectRepo;
    public static string RepoUrl => "https://github.com/" + Repo;
    public static string IssuesUrl => RepoUrl + "/issues";

    /// <summary>
    /// Path of the running exe when it is the published single-file exe, whatever its file name (an older updater
    /// kept the previous name). Null for a normal build run through its dll (Debug, tests, "dotnet x.dll").
    /// </summary>
    public static string? ExePath { get; } = IsSingleFile ? Environment.ProcessPath : null;

    // Inside a single-file exe the assemblies have no file of their own: their Location is empty.
#pragma warning disable IL3000
    private static bool IsSingleFile => string.IsNullOrEmpty(typeof(AppInfo).Assembly.Location);
#pragma warning restore IL3000

    // Names of the per-user kernel objects that let the processes find each other.
    /// <summary>One instance per data folder (the normal folder, or a test folder given with --data).</summary>
    public static string InstanceMutexFor(string dataDir) => InstanceMutexFor(dataDir, @"Local\FortniteChestTracker.Instance.");

    internal static string InstanceMutexFor(string dataDir, string prefix)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToLowerInvariant()));
        return prefix + Convert.ToHexString(hash, 0, 8);
    }
    public const string StandbyMutex = @"Local\FortniteChestTracker.Standby";
    public const string StandbyStopEvent = @"Local\FortniteChestTracker.StandbyStop";
}
