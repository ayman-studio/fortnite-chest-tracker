using Microsoft.Win32;

namespace FortniteChestTracker.Tests;

/// <summary>
/// Install / uninstall in temp folders and a test registry key (HKCU\Software\FortniteChestTrackerTests\…): the real install
/// folder, shortcuts, "Installed apps" entry and logon entry of the player's copy are never touched.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class InstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fct-install-{Guid.NewGuid():N}");
    private readonly string _testKey = $@"Software\FortniteChestTrackerTests\{Guid.NewGuid():N}";
    private readonly (string, string, string, string, string, string, string) _saved =
        (Installer.InstallFolder, Installer.StartMenuFolder, Installer.DesktopFolder, Installer.UninstallKey, FortniteWatch.RunKey, FortniteWatch.RunValue, FortniteWatch.LegacyRunValue);

    public InstallerTests()
    {
        Installer.InstallFolder = Path.Combine(_root, "Programs", "Fortnite Chest Tracker");
        Installer.StartMenuFolder = Path.Combine(_root, "StartMenu");
        Installer.DesktopFolder = Path.Combine(_root, "Desktop");
        Installer.UninstallKey = _testKey + @"\Uninstall\FortniteChestTracker";
        FortniteWatch.RunKey = _testKey + @"\Run";
        FortniteWatch.RunValue = "FortniteChestTrackerTest";
        FortniteWatch.LegacyRunValue = "FortniteChestTrackerTestLegacy";
        L.Set("en");
    }

    public void Dispose()
    {
        (Installer.InstallFolder, Installer.StartMenuFolder, Installer.DesktopFolder, Installer.UninstallKey, FortniteWatch.RunKey, FortniteWatch.RunValue, FortniteWatch.LegacyRunValue) = _saved;
        try { Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false); } catch { }
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\FortniteChestTrackerTests", writable: true);
            if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\FortniteChestTrackerTests", throwOnMissingSubKey: false);
            }
        }
        catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string FakeExe(string content = "build-1")
    {
        var dir = Path.Combine(_root, "Downloads");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, AppInfo.ExeName);
        // Not a real program (the shell refuses to make a shortcut to a broken "MZ" file): only its bytes matter here.
        File.WriteAllText(path, "fake exe " + content + new string('x', 4096));
        return path;
    }

    private RegistryKey? Entry() => Registry.CurrentUser.OpenSubKey(Installer.UninstallKey);

    private IEnumerable<string> Links => new[] { Path.Combine(Installer.StartMenuFolder, "Fortnite - Chest Tracker.lnk"), Path.Combine(Installer.DesktopFolder, "Fortnite - Chest Tracker.lnk") };

    [Fact]
    public void Install_copies_the_exe_adds_the_shortcuts_and_the_installed_apps_entry()
    {
        var source = FakeExe();
        Assert.False(Installer.InstalledExeExists);

        Assert.Null(Installer.Install(source, "1.2.3", "https://github.com/me/repo"));

        Assert.True(Installer.InstalledExeExists);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Installer.InstalledExe));
        foreach (var link in Links)
        {
            Assert.True(File.Exists(link), link);
            Assert.True(Installer.SamePath(Installer.InstalledExe, Installer.ShortcutTarget(link)!));
        }

        using var key = Entry();
        Assert.NotNull(key);
        Assert.Equal("Fortnite - Chest Tracker", key.GetValue("DisplayName"));
        Assert.Equal("1.2.3", key.GetValue("DisplayVersion"));
        Assert.Equal("ayman-studio", key.GetValue("Publisher"));
        Assert.Equal(Installer.InstalledExe, key.GetValue("DisplayIcon"));
        Assert.Equal(Installer.InstallFolder, key.GetValue("InstallLocation"));
        Assert.Equal($"\"{Installer.InstalledExe}\" --uninstall", key.GetValue("UninstallString"));
        Assert.Equal(1, key.GetValue("NoModify"));
        Assert.Equal(1, key.GetValue("NoRepair"));
        Assert.Equal((int)(new FileInfo(source).Length / 1024), key.GetValue("EstimatedSize"));
        Assert.Equal("https://github.com/me/repo", key.GetValue("URLInfoAbout"));
    }

    [Fact]
    public void The_installed_exe_is_recognized()
    {
        var source = FakeExe();
        Assert.Null(Installer.Install(source, "1.0.0", "https://github.com/me/repo"));
        Assert.True(Installer.IsInstalledPath(Installer.InstalledExe.ToUpperInvariant()));
        Assert.False(Installer.IsInstalledPath(source));
        Assert.False(Installer.IsInstalledPath(null));
    }

    [Fact]
    public void Installing_again_replaces_a_different_build_and_leaves_the_same_one_alone()
    {
        Assert.Null(Installer.Install(FakeExe("build-1"), "1.0.0", "https://github.com/me/repo"));
        var old = Installer.InstalledExe + ".old";

        Assert.Null(Installer.Install(FakeExe("build-1"), "1.0.0", "https://github.com/me/repo"));
        Assert.False(File.Exists(old)); // identical: not copied again

        var newer = FakeExe("build-2");
        Assert.Null(Installer.Install(newer, "1.1.0", "https://github.com/me/repo"));
        Assert.Equal(File.ReadAllBytes(newer), File.ReadAllBytes(Installer.InstalledExe));
        Assert.True(File.Exists(old)); // renamed, not overwritten (it may be running); deleted at its next start
        using var key = Entry();
        Assert.Equal("1.1.0", key!.GetValue("DisplayVersion"));
    }

    [Fact]
    public void The_installed_apps_entry_follows_an_update()
    {
        Assert.Null(Installer.Install(FakeExe(), "1.0.0", "https://github.com/me/repo"));
        Assert.False(Installer.RefreshRegistration(Installer.InstalledExe, "1.0.0", "https://github.com/me/repo"));

        Assert.True(Installer.RefreshRegistration(Installer.InstalledExe, "1.1.0", "https://github.com/me/repo"));
        using (var key = Entry())
        {
            Assert.Equal("1.1.0", key!.GetValue("DisplayVersion"));
        }
        Assert.False(Installer.RefreshRegistration(Installer.InstalledExe, "1.1.0", "https://github.com/me/repo"));
    }

    [Fact]
    public void Uninstall_removes_what_the_install_added_and_keeps_the_rest()
    {
        Assert.Null(Installer.Install(FakeExe(), "1.0.0", "https://github.com/me/repo"));
        File.WriteAllText(Installer.InstalledExe + ".old", "left by an update");
        // A shortcut with the same name to another copy, and a logon entry for the installed copy.
        var otherLink = Path.Combine(_root, "Elsewhere", "Fortnite - Chest Tracker.lnk");
        Installer.CreateShortcut(otherLink, FakeExe(), "other copy");
        using (var run = Registry.CurrentUser.CreateSubKey(FortniteWatch.RunKey))
        {
            run.SetValue(FortniteWatch.RunValue, $"\"{Installer.InstalledExe}\" --standby --data \"C:\\data\"");
        }
        var dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "data.json"), "{}");

        Installer.Uninstall(dataDir);
        Installer.DeleteFolderLater();

        foreach (var link in Links)
        {
            Assert.False(File.Exists(link), link);
        }
        Assert.True(File.Exists(otherLink));
        Assert.Null(Entry());
        using (var run = Registry.CurrentUser.OpenSubKey(FortniteWatch.RunKey))
        {
            Assert.Null(run?.GetValue(FortniteWatch.RunValue));
        }
        // The program folder goes a couple of seconds later (a running exe cannot delete itself).
        for (var i = 0; i < 100 && Directory.Exists(Installer.InstallFolder); i++)
        {
            Thread.Sleep(100);
        }
        Assert.False(Directory.Exists(Installer.InstallFolder));
        Assert.True(File.Exists(Path.Combine(dataDir, "data.json"))); // the player's data is kept
    }

    [Fact]
    public void Uninstall_never_deletes_files_it_did_not_put_there()
    {
        Assert.Null(Installer.Install(FakeExe(), "1.0.0", "https://github.com/me/repo"));
        var mine = Path.Combine(Installer.InstallFolder, "notes.txt");
        File.WriteAllText(mine, "the player's own file");

        Installer.Uninstall(Path.Combine(_root, "data"));
        Installer.DeleteFolderLater();

        for (var i = 0; i < 100 && File.Exists(Installer.InstalledExe); i++)
        {
            Thread.Sleep(100);
        }
        Assert.False(File.Exists(Installer.InstalledExe));
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void The_folder_goes_even_if_the_exe_is_busy_a_little_longer()
    {
        Assert.Null(Installer.Install(FakeExe(), "1.0.0", "https://github.com/me/repo"));
        Installer.Uninstall(Path.Combine(_root, "data"));
        using (new FileStream(Installer.InstalledExe, FileMode.Open, FileAccess.Read, FileShare.Read)) // like a process still closing
        {
            Installer.DeleteFolderLater();
            Thread.Sleep(3500); // past the first try
            Assert.True(File.Exists(Installer.InstalledExe));
        }
        for (var i = 0; i < 150 && Directory.Exists(Installer.InstallFolder); i++)
        {
            Thread.Sleep(100);
        }
        Assert.False(Directory.Exists(Installer.InstallFolder));
    }

    [Fact]
    public void A_logon_entry_for_another_copy_is_left_alone()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(FortniteWatch.RunKey))
        {
            run.SetValue(FortniteWatch.RunValue, "\"D:\\Games\\FortniteChestTracker.exe\" --standby");
        }
        Assert.False(FortniteWatch.RemoveRunEntryIn(Installer.InstallFolder));
        using var key = Registry.CurrentUser.OpenSubKey(FortniteWatch.RunKey);
        Assert.NotNull(key!.GetValue(FortniteWatch.RunValue));
    }

    [Fact]
    public void An_installed_copy_is_compared_by_version_then_by_content()
    {
        // A real program with a version stands for the exe.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var real = Path.Combine(system, "notepad.exe");
        var downloaded = Path.Combine(_root, "old", AppInfo.ExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(downloaded)!);
        File.Copy(real, downloaded);
        Assert.NotNull(Installer.ExeVersion(real));
        Assert.Null(Installer.ExeVersion(FakeExe()));

        Directory.CreateDirectory(Installer.InstallFolder);
        File.Copy(real, Installer.InstalledExe);
        Assert.False(Installer.NeedsCopy(downloaded, Installer.InstalledExe)); // same version, same bytes
        File.AppendAllText(downloaded, "different build");
        Assert.True(Installer.NeedsCopy(downloaded, Installer.InstalledExe)); // same version, other bytes
    }

    [Theory]
    [InlineData("\"C:\\A B\\FortniteChestTracker.exe\" --standby --data \"C:\\x\"", "C:\\A B\\FortniteChestTracker.exe")]
    [InlineData("C:\\x\\FortniteChestTracker.exe --standby", "C:\\x\\FortniteChestTracker.exe")]
    [InlineData("C:\\x\\FortniteChestTracker.exe", "C:\\x\\FortniteChestTracker.exe")]
    [InlineData("\"unterminated", null)]
    public void The_program_of_a_command_line_is_found(string command, string? exe)
    {
        Assert.Equal(exe, FortniteWatch.ExeOf(command));
    }

    [Fact]
    public void Paths_inside_a_folder()
    {
        Assert.True(Installer.IsInside(@"C:\Programs\FCT\FortniteChestTracker.exe", @"C:\Programs\FCT"));
        Assert.True(Installer.IsInside(@"c:\programs\fct\FortniteChestTracker.exe", @"C:\Programs\FCT\"));
        Assert.False(Installer.IsInside(@"C:\Programs\FCT2\FortniteChestTracker.exe", @"C:\Programs\FCT"));
        Assert.False(Installer.IsInside(@"C:\Programs\FCT", @"C:\Programs\FCT"));
    }

    // ---- Installed copy of versions 1.0.x (their updater left the new version under the old file name) ----------

    /// <summary>The installed copy as 1.0.x left it: old exe name, its shortcuts, "Installed apps" entry and logon entry.</summary>
    private string LegacyInstall()
    {
        var legacy = Installer.LegacyInstalledExe;
        Directory.CreateDirectory(Installer.InstallFolder);
        File.WriteAllText(legacy, "fake exe new version under the old name" + new string('x', 4096));
        foreach (var link in Links)
        {
            Installer.CreateShortcut(link, legacy, "old");
        }
        Installer.Register(legacy, "1.0.3", "https://github.com/me/repo");
        using var run = Registry.CurrentUser.CreateSubKey(FortniteWatch.RunKey);
        run.SetValue(FortniteWatch.LegacyRunValue, $"\"{legacy}\" --standby --data \"C:\\old data\"");
        return legacy;
    }

    [Fact]
    public void The_installed_copy_under_its_old_name_is_renamed_and_everything_follows()
    {
        var legacy = LegacyInstall();
        var otherLink = Path.Combine(_root, "Elsewhere", "Fortnite - Chest Tracker.lnk");
        var otherExe = FakeExe();
        Installer.CreateShortcut(otherLink, otherExe, "another copy");
        var dataDir = Path.Combine(_root, "data");

        var renamed = Installer.RenameLegacyInstalledExe(legacy, "1.1.0", "https://github.com/me/repo", dataDir);

        Assert.Equal(Installer.InstalledExe, renamed);
        Assert.Equal(File.ReadAllBytes(legacy), File.ReadAllBytes(Installer.InstalledExe));
        Assert.True(File.Exists(legacy)); // never moved: it is the running exe, and the copy is not started yet
        foreach (var link in Links)
        {
            Assert.True(Installer.SamePath(Installer.InstalledExe, Installer.ShortcutTarget(link)!), link);
        }
        Assert.True(Installer.SamePath(otherExe, Installer.ShortcutTarget(otherLink)!)); // not one of the install's shortcuts
        using (var key = Entry())
        {
            Assert.Equal($"\"{Installer.InstalledExe}\" --uninstall", key!.GetValue("UninstallString"));
            Assert.Equal(Installer.InstalledExe, key.GetValue("DisplayIcon"));
            Assert.Equal("1.1.0", key.GetValue("DisplayVersion"));
        }
        using (var run = Registry.CurrentUser.OpenSubKey(FortniteWatch.RunKey))
        {
            Assert.Null(run!.GetValue(FortniteWatch.LegacyRunValue));
            Assert.Equal($"\"{Installer.InstalledExe}\" --standby --data \"{dataDir}\"", run.GetValue(FortniteWatch.RunValue));
        }

        // Done again (the old exe started once more before the new one ran): same result, nothing breaks.
        Assert.Equal(Installer.InstalledExe, Installer.RenameLegacyInstalledExe(legacy, "1.1.0", "https://github.com/me/repo", dataDir));
    }

    [Fact]
    public void The_old_file_goes_only_once_the_renamed_copy_runs_and_nothing_starts_it_any_more()
    {
        var legacy = LegacyInstall();
        Assert.Equal(Installer.InstalledExe, Installer.RenameLegacyInstalledExe(legacy, "1.1.0", "https://github.com/me/repo", Path.Combine(_root, "data")));

        Assert.False(Installer.DeleteLegacyExe(legacy)); // the old file itself is running
        Assert.False(Installer.DeleteLegacyExe(null));
        Assert.True(File.Exists(legacy));

        var desktop = Path.Combine(Installer.DesktopFolder, "Fortnite - Chest Tracker.lnk");
        Installer.CreateShortcut(desktop, legacy, "put back by the player");
        Assert.False(Installer.DeleteLegacyExe(Installer.InstalledExe)); // a shortcut still starts it
        Assert.True(File.Exists(legacy));

        Installer.CreateShortcut(desktop, Installer.InstalledExe, "fixed");
        using (var run = Registry.CurrentUser.CreateSubKey(FortniteWatch.RunKey))
        {
            run.SetValue(FortniteWatch.LegacyRunValue, $"\"{legacy}\" --standby");
        }
        Assert.False(Installer.DeleteLegacyExe(Installer.InstalledExe)); // the logon entry still starts it
        using (var run = Registry.CurrentUser.CreateSubKey(FortniteWatch.RunKey))
        {
            run.DeleteValue(FortniteWatch.LegacyRunValue);
        }

        Assert.True(Installer.DeleteLegacyExe(Installer.InstalledExe));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(Installer.InstalledExe));
    }

    [Fact]
    public void Only_the_installed_copy_under_the_old_name_is_renamed()
    {
        // The old name elsewhere (a copy kept in Downloads): left as it is, it updates itself under that name.
        var elsewhere = Path.Combine(_root, "Downloads", Legacy.ExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, "fake exe");
        Assert.Null(Installer.RenameLegacyInstalledExe(elsewhere, "1.1.0", "https://github.com/me/repo", _root));

        // Already the current name.
        Assert.Null(Installer.Install(FakeExe(), "1.1.0", "https://github.com/me/repo"));
        Assert.Null(Installer.RenameLegacyInstalledExe(Installer.InstalledExe, "1.1.0", "https://github.com/me/repo", _root));
        Assert.False(File.Exists(Installer.LegacyInstalledExe));
        Assert.True(Installer.IsInstalledPath(Installer.LegacyInstalledExe)); // still recognized as the installed copy
    }

    [Fact]
    public void Uninstall_also_removes_the_old_logon_entry_and_the_old_exe()
    {
        var legacy = LegacyInstall();
        Installer.Uninstall(Path.Combine(_root, "data"));
        Installer.DeleteFolderLater();

        using (var run = Registry.CurrentUser.OpenSubKey(FortniteWatch.RunKey))
        {
            Assert.Null(run?.GetValue(FortniteWatch.LegacyRunValue));
        }
        for (var i = 0; i < 100 && Directory.Exists(Installer.InstallFolder); i++)
        {
            Thread.Sleep(100);
        }
        Assert.False(File.Exists(legacy));
        Assert.False(Directory.Exists(Installer.InstallFolder));
    }
}
