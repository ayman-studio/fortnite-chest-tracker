namespace FortniteChestTracker;

public enum ActivityLevel
{
    Info,
    Muted,
    Success,
    Warning,
    Error,
    Treasure,
}

/// <param name="Id">Increasing number (lets the web page show only the new entries as notifications).</param>
public sealed record ActivityEntry(long Id, DateTime Time, ActivityLevel Level, string Text);

/// <summary>
/// What the tool tells the player ("match counted: 23 chests", "overlay locked"…), shown in the web interface.
/// Thread-safe: the overlay thread writes here too.
/// </summary>
public static class Activity
{
    private const int Keep = 200;
    private static readonly object Gate = new();
    private static readonly LinkedList<ActivityEntry> Entries = new();
    private static long _nextId = 1;

    /// <summary>Raised after each new entry (on the writer's thread).</summary>
    public static event Action? Changed;

    public static void Log(ActivityLevel level, string text)
    {
        lock (Gate)
        {
            Entries.AddLast(new ActivityEntry(_nextId++, DateTime.Now, level, text));
            while (Entries.Count > Keep)
            {
                Entries.RemoveFirst();
            }
        }
        Changed?.Invoke();
    }

    public static void Info(string text) => Log(ActivityLevel.Info, text);
    public static void Muted(string text) => Log(ActivityLevel.Muted, text);
    public static void Success(string text) => Log(ActivityLevel.Success, text);
    public static void Warning(string text) => Log(ActivityLevel.Warning, text);
    public static void Error(string text) => Log(ActivityLevel.Error, text);
    public static void Treasure(string text) => Log(ActivityLevel.Treasure, text);

    public static IReadOnlyList<ActivityEntry> Snapshot()
    {
        lock (Gate)
        {
            return Entries.ToList();
        }
    }
}
