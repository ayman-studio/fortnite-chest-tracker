namespace FortniteChestTracker.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fct-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void History_round_trips()
    {
        var store = new Store(_dir);
        var data = new TrackerData();
        data.Matches.Add(new MatchRecord { SessionId = "abc", ChestsOpened = 7 });
        store.SaveData(data);

        var loaded = new Store(_dir).LoadData();
        Assert.Equal("abc", Assert.Single(loaded.Matches).SessionId);
        Assert.Equal(7, loaded.Matches[0].ChestsOpened);
    }

    [Fact]
    public void Previous_version_is_used_when_the_history_is_broken_or_missing()
    {
        var store = new Store(_dir);
        var data = new TrackerData();
        data.Matches.Add(new MatchRecord { SessionId = "first", ChestsOpened = 3 });
        store.SaveData(data);
        data.Matches.Add(new MatchRecord { SessionId = "second", ChestsOpened = 5 });
        store.SaveData(data); // the first version is now data.json.bak
        Assert.True(File.Exists(store.DataPath + ".bak"));

        File.WriteAllBytes(store.DataPath, new byte[64]); // power cut: zeros
        Assert.Equal("first", Assert.Single(new Store(_dir).LoadData().Matches).SessionId);

        File.Delete(store.DataPath); // crash between the two renames of a save
        Assert.Equal("first", Assert.Single(new Store(_dir).LoadData().Matches).SessionId);
        Assert.True(File.Exists(store.DataPath)); // put back before anything is saved over the backup
    }

    [Fact]
    public void Corrupt_history_is_kept_aside_and_a_new_one_starts()
    {
        var store = new Store(_dir);
        File.WriteAllText(store.DataPath, "{ this is not json");

        var data = store.LoadData();

        Assert.Empty(data.Matches);
        var backup = Assert.Single(Directory.GetFiles(_dir, "data.json.corrupt-*"));
        Assert.Equal("{ this is not json", File.ReadAllText(backup));
    }

    [Fact]
    public void Locked_history_is_not_mistaken_for_a_corrupt_one()
    {
        var store = new Store(_dir);
        var data = new TrackerData();
        data.Matches.Add(new MatchRecord { SessionId = "keep-me" });
        store.SaveData(data);

        using (new FileStream(store.DataPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Before: the read error counted as corruption, and the history was replaced by an empty one.
            Assert.ThrowsAny<IOException>(() => store.LoadData());
        }

        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
        Assert.Equal("keep-me", Assert.Single(store.LoadData().Matches).SessionId);
    }

    [Fact]
    public void Briefly_locked_history_is_read_after_a_retry()
    {
        var store = new Store(_dir);
        var data = new TrackerData();
        data.Matches.Add(new MatchRecord { SessionId = "x" });
        store.SaveData(data);

        var locker = new FileStream(store.DataPath, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Delay(300, TestContext.Current.CancellationToken).ContinueWith(_ => locker.Dispose(), TestContext.Current.CancellationToken);

        Assert.Equal("x", Assert.Single(store.LoadData().Matches).SessionId);
    }


    [Fact]
    public void The_open_at_start_setting_keeps_its_value_under_its_earlier_name()
    {
        var store = new Store(_dir);
        File.WriteAllText(store.ConfigPath, """{"Language":"en","OpenReportOnStart":false}""");

        var config = store.LoadConfig()!;
        Assert.False(config.OpenUiOnStart);

        store.SaveConfig(config);
        var saved = File.ReadAllText(store.ConfigPath);
        Assert.Contains("\"OpenUiOnStart\": false", saved);
        Assert.DoesNotContain("OpenReportOnStart", saved);
        Assert.True(new Config().OpenUiOnStart);
    }

    [Fact]
    public void Exports_nothing_updates_any_more_are_removed()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in new[] { "report.html", "matches.csv", "overlay.html", "data.json" })
        {
            File.WriteAllText(Path.Combine(_dir, name), "x");
        }

        _ = new Store(_dir);

        Assert.False(File.Exists(Path.Combine(_dir, "report.html")));
        Assert.False(File.Exists(Path.Combine(_dir, "matches.csv")));
        Assert.True(File.Exists(Path.Combine(_dir, "overlay.html"))); // the OBS page is still written
        Assert.True(File.Exists(Path.Combine(_dir, "data.json")));
    }
}
