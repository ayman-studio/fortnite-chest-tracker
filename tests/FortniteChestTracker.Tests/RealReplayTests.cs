namespace FortniteChestTracker.Tests;

/// <summary>
/// Runs on real replays when FCT_TEST_REPLAYS points to a folder of finished .replay files
/// (for example a copy of %LOCALAPPDATA%\FortniteGame\Saved\Demos). Skipped otherwise (CI has no replays).
/// Regression check: with FCT_REPLAY_SUMMARY set to a file path, the whole folder is analyzed like the tool does and
/// a JSON summary of every match is written there, to compare two versions of the analysis.
/// </summary>
public class RealReplayTests
{
    private static FileInfo? SmallestFinishedReplay()
    {
        var dir = Environment.GetEnvironmentVariable("FCT_TEST_REPLAYS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }
        return new DirectoryInfo(dir).EnumerateFiles("*.replay")
            .Where(f => ReplayHeaderProbe.Read(f.FullName) is { IsLive: false, KeyMissing: false })
            .OrderBy(f => f.Length)
            .FirstOrDefault(f => f.Length > 1_000_000);
    }

    private static MemoryStream Load(FileInfo f) => new(File.ReadAllBytes(f.FullName), writable: false);

    [Fact]
    public void A_real_replay_is_analyzed()
    {
        var file = SmallestFinishedReplay();
        Assert.SkipWhen(file == null, "FCT_TEST_REPLAYS is not set");

        var record = new ReplayAnalyzer().Analyze(Load(file!), file!.Name, "");

        Assert.False(string.IsNullOrEmpty(record.SessionId));
        Assert.True(record.ChestsOpened >= 0);
        Assert.Equal(ReplayAnalyzer.CurrentVersion, record.AnalyzerVersion);
    }

    [Fact]
    public void A_cancelled_analysis_stops()
    {
        var file = SmallestFinishedReplay();
        Assert.SkipWhen(file == null, "FCT_TEST_REPLAYS is not set");

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var analyzer = new ReplayAnalyzer { Cancellation = cancel.Token };

        Assert.ThrowsAny<OperationCanceledException>(() => analyzer.Analyze(Load(file!), file!.Name, ""));
    }

    [Fact]
    public void An_analysis_cancelled_midway_stops_quickly()
    {
        var file = SmallestFinishedReplay();
        Assert.SkipWhen(file == null, "FCT_TEST_REPLAYS is not set");

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var analyzer = new ReplayAnalyzer { Cancellation = cancel.Token };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => analyzer.Analyze(Load(file!), file!.Name, ""));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Every_replay_of_the_folder_is_analyzed()
    {
        var dir = Environment.GetEnvironmentVariable("FCT_TEST_REPLAYS");
        Assert.SkipWhen(string.IsNullOrEmpty(dir) || !Directory.Exists(dir), "FCT_TEST_REPLAYS is not set");

        // Same path as the tool (readiness checks, timeouts, Battle Royale only, one record per match), in a temp data folder.
        var dataDir = Path.Combine(Path.GetTempPath(), $"fct-replays-{Guid.NewGuid():N}");
        try
        {
            L.Set("en");
            var store = new Store(dataDir);
            var data = store.LoadData();
            var tracker = new Tracker(store, new Config { DemosFolder = dir!, ArchiveFolder = "" }, data);
            tracker.Scan(ignoreQuietPeriod: true);

            var errors = data.ProcessedFiles.Where(kv => kv.Value.StartsWith("error", StringComparison.Ordinal))
                .Select(kv => $"{kv.Key.Split('|')[0]}: {kv.Value}").ToList();
            if (Environment.GetEnvironmentVariable("FCT_REPLAY_SUMMARY") is { Length: > 0 } summaryPath)
            {
                var summary = new
                {
                    Matches = data.Matches.OrderBy(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).Select(m => new
                    {
                        m.SessionId,
                        Squad = m.SquadChests?.Count,
                        m.ReplayFile,
                        m.MatchStartUtc,
                        m.Playlist,
                        m.Placement,
                        m.TotalPlayers,
                        m.ChestsOpened,
                        m.AmmoBoxesOpened,
                        Treasures = m.TreasureFinds.Count,
                    }),
                    TotalChests = data.Matches.Sum(m => m.ChestsOpened),
                    TotalTreasures = data.Matches.Sum(m => m.TreasureFinds.Count),
                    Skipped = data.ProcessedFiles.Values.Count(v => !v.StartsWith("error", StringComparison.Ordinal) && data.Matches.All(m => m.SessionId != v)),
                    Errors = errors,
                };
                File.WriteAllText(summaryPath, System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            Assert.NotEmpty(data.Matches);
            Assert.True(errors.Count == 0, "unreadable replays: " + string.Join(" · ", errors));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }
}
