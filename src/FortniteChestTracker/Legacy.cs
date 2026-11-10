namespace FortniteChestTracker;

/// <summary>
/// Names used by versions 1.0.x. Still recognized so that an existing history, installed copy, "open with Fortnite"
/// entry and update channel carry over to the current names. Nothing else in the code uses them.
/// </summary>
internal static class Legacy
{
    /// <summary>Data folder under %APPDATA% (moved to <see cref="DataFolders.FolderName"/>).</summary>
    public const string DataFolderName = "ChestCounter";

    /// <summary>File name of the exe, installed copy included (renamed to <see cref="AppInfo.ExeName"/>).</summary>
    public const string ExeName = "ChestCounter.exe";

    /// <summary>Name of the release asset those versions download (the same file name).</summary>
    public const string AssetName = ExeName;

    /// <summary>Value name of the "open with Fortnite" entry under HKCU\…\Run.</summary>
    public const string RunValue = "ChestCounter";

    /// <summary>Kernel objects of a running copy: its main instance (+ hash of the data folder) and its standby.</summary>
    public const string InstanceMutexPrefix = @"Local\ChestCounter.Instance.";
    public const string StandbyStopEvent = @"Local\ChestCounter.StandbyStop";
}
