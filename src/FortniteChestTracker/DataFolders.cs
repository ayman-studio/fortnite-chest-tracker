namespace FortniteChestTracker;

/// <summary>
/// Where the history and the settings live: %APPDATA%\FortniteChestTracker, or a folder given with --data (development,
/// tests). The folder of versions 1.0.x is moved there the first time (history, settings and treasure names are kept).
/// </summary>
public static class DataFolders
{
    public const string FolderName = "FortniteChestTracker";

    /// <summary>Parent of the default folders (%APPDATA%). Another folder in tests.</summary>
    internal static string Root { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string Default => Path.Combine(Root, FolderName);

    internal static string LegacyDefault => Path.Combine(Root, Legacy.DataFolderName);

    public enum Migration
    {
        /// <summary>Nothing to move (already done, or a new player).</summary>
        None,
        /// <summary>The old folder was renamed.</summary>
        Moved,
        /// <summary>A file of the old folder was in use: everything was copied, the old folder is left as it was.</summary>
        Copied,
        /// <summary>Neither worked: the old folder stays the data folder for this run.</summary>
        Failed,
    }

    /// <summary>The folder of versions 1.0.x is still to be moved.</summary>
    internal static bool MigrationPending => !Directory.Exists(Default) && Directory.Exists(LegacyDefault);

    /// <summary>True when <paramref name="requested"/> (the --data value, or null) means the default data folder.</summary>
    public static bool IsDefault(string? requested) =>
        requested == null || SamePath(requested, Default) || SamePath(requested, LegacyDefault);

    /// <summary>
    /// The data folder to use. The default one (no --data, or --data naming the old or the new default folder) is
    /// migrated first; a folder given with --data is used as is and nothing else is touched.
    /// </summary>
    public static string Resolve(string? requested)
    {
        if (!IsDefault(requested))
        {
            return Path.GetFullPath(requested!);
        }
        return MigrateLegacy() == Migration.Failed ? LegacyDefault : Default;
    }

    /// <summary>Moves (else copies) the folder of versions 1.0.x to the new default folder, when the latter does not exist yet.</summary>
    internal static Migration MigrateLegacy()
    {
        var target = Default;
        var source = LegacyDefault;
        if (Directory.Exists(target) || !Directory.Exists(source))
        {
            return Migration.None;
        }
        try
        {
            Directory.Move(source, target);
            return Migration.Moved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file is still open (an older copy closing): copy everything instead.
        }

        // Copied next to it first, then renamed: a copy cut halfway is never taken for the data folder.
        var partial = target + ".partial";
        try
        {
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }
            CopyTree(source, partial);
            Directory.Move(partial, target);
            return Migration.Copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(partial, recursive: true); } catch { }
            return Migration.Failed;
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            // Shared read: a file another process is writing can still be copied.
            using var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var to = new FileStream(Path.Combine(target, Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write);
            from.CopyTo(to);
            to.Flush(flushToDisk: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
