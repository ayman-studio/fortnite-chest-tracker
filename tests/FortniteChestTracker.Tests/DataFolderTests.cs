namespace FortniteChestTracker.Tests;

/// <summary>
/// The data folder of versions 1.0.x is moved to the new one, in a temp folder standing for %APPDATA%: the player's
/// real data is never touched.
/// </summary>
public sealed class DataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fct-appdata-{Guid.NewGuid():N}");
    private readonly string _savedRoot = DataFolders.Root;

    public DataFolderTests()
    {
        Directory.CreateDirectory(_root);
        DataFolders.Root = _root;
    }

    public void Dispose()
    {
        DataFolders.Root = _savedRoot;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Legacy1 => Path.Combine(_root, Legacy.DataFolderName);
    private string Current => Path.Combine(_root, DataFolders.FolderName);

    /// <summary>A history as 1.0.x left it, with a sub-folder.</summary>
    private void OldHistory()
    {
        Directory.CreateDirectory(Path.Combine(Legacy1, "sub"));
        File.WriteAllText(Path.Combine(Legacy1, "data.json"), """{"Matches":[{"SessionId":"kept","ChestsOpened":9}]}""");
        File.WriteAllText(Path.Combine(Legacy1, "config.json"), """{"Language":"en"}""");
        File.WriteAllText(Path.Combine(Legacy1, "sub", "note.txt"), "x");
    }

    [Fact]
    public void The_old_folder_is_moved_to_the_new_one()
    {
        OldHistory();
        Assert.True(DataFolders.MigrationPending);

        Assert.Equal(Current, DataFolders.Resolve(null));

        Assert.False(Directory.Exists(Legacy1));
        Assert.Equal("kept", Assert.Single(new Store(Current).LoadData().Matches).SessionId);
        Assert.True(File.Exists(Path.Combine(Current, "sub", "note.txt")));
        Assert.False(DataFolders.MigrationPending);
        Assert.Equal(DataFolders.Migration.None, DataFolders.MigrateLegacy()); // once only
    }

    [Fact]
    public void The_old_default_folder_given_with_data_means_the_new_one()
    {
        OldHistory();
        Assert.True(DataFolders.IsDefault(Legacy1 + "\\"));
        Assert.Equal(Current, DataFolders.Resolve(Legacy1));
        Assert.True(File.Exists(Path.Combine(Current, "data.json")));
    }

    [Fact]
    public void A_folder_in_use_is_copied_and_the_old_one_left_as_it_is()
    {
        OldHistory();
        // An older copy still has a file open (without sharing deletes): the folder cannot be renamed.
        using (new FileStream(Path.Combine(Legacy1, "config.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(DataFolders.Migration.Copied, DataFolders.MigrateLegacy());
        }

        Assert.True(File.Exists(Path.Combine(Legacy1, "data.json")));
        Assert.Equal(File.ReadAllText(Path.Combine(Legacy1, "data.json")), File.ReadAllText(Path.Combine(Current, "data.json")));
        Assert.Equal("""{"Language":"en"}""", File.ReadAllText(Path.Combine(Current, "config.json")));
        Assert.True(File.Exists(Path.Combine(Current, "sub", "note.txt")));
        Assert.False(Directory.Exists(Current + ".partial"));
        Assert.Equal(Current, DataFolders.Resolve(null)); // from now on, the new folder
    }

    [Fact]
    public void An_existing_new_folder_is_never_replaced()
    {
        OldHistory();
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "data.json"), "{}");

        Assert.False(DataFolders.MigrationPending);
        Assert.Equal(Current, DataFolders.Resolve(null));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(Current, "data.json")));
        Assert.True(File.Exists(Path.Combine(Legacy1, "data.json")));
    }

    [Fact]
    public void Another_data_folder_is_used_as_is_and_nothing_is_moved()
    {
        OldHistory();
        var dev = Path.Combine(_root, "dev data");

        Assert.False(DataFolders.IsDefault(dev));
        Assert.Equal(dev, DataFolders.Resolve(dev));
        Assert.True(Directory.Exists(Legacy1));
        Assert.False(Directory.Exists(Current));
    }

    [Fact]
    public void A_new_player_has_nothing_to_move()
    {
        Assert.False(DataFolders.MigrationPending);
        Assert.Equal(DataFolders.Migration.None, DataFolders.MigrateLegacy());
        Assert.Equal(Current, DataFolders.Resolve(null));
        Assert.False(Directory.Exists(Legacy1));
    }

    [Fact]
    public void The_default_folder_is_under_appdata()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FortniteChestTracker"),
            Path.Combine(_savedRoot, DataFolders.FolderName));
    }
}
