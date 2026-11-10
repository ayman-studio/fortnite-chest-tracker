using System.Text.Json;

namespace FortniteChestTracker.Tests;

public class UpdaterTests
{
    private static readonly Version Current = new(1, 4, 0);
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Release(string tag = "v1.5.0", bool prerelease = false, bool draft = false, string assetName = AppInfo.ExeName,
        string? digest = Digest, string url = "https://github.com/me/repo/releases/download/v1.5.0/" + AppInfo.ExeName, string[]? otherAssets = null)
    {
        Dictionary<string, object?> Asset(string name, string link, long size) => new()
        {
            ["name"] = name,
            ["browser_download_url"] = link,
            ["size"] = size,
            ["digest"] = digest,
        };
        var assets = (otherAssets ?? Array.Empty<string>()).Select(name => Asset(name, "https://github.com/me/repo/releases/download/v1.5.0/" + name, 999)).ToList();
        assets.Insert(0, Asset(assetName, url, 12345));
        if (digest == null)
        {
            assets.ForEach(a => a.Remove("digest"));
        }
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["html_url"] = "https://github.com/me/repo/releases/tag/" + tag,
            ["assets"] = assets,
        });
    }

    [Fact]
    public void Newer_release_is_offered()
    {
        var update = Updater.Parse(Release(), Current);
        Assert.NotNull(update);
        Assert.Equal(new Version(1, 5, 0), update.Version);
        Assert.Equal(Digest[7..], update.Sha256);
        Assert.Equal(12345, update.Size);
    }

    [Theory]
    [InlineData("v1.4.0")]
    [InlineData("v1.3.9")]
    [InlineData("1.4")]
    [InlineData("not-a-version")]
    public void Same_or_older_release_is_ignored(string tag)
    {
        Assert.Null(Updater.Parse(Release(tag), Current));
    }

    [Fact]
    public void Versions_are_compared_as_numbers()
    {
        Assert.NotNull(Updater.Parse(Release("v1.10.0"), new Version(1, 9, 0)));
    }

    [Fact]
    public void Prereleases_and_drafts_are_ignored()
    {
        Assert.Null(Updater.Parse(Release(prerelease: true), Current));
        Assert.Null(Updater.Parse(Release(draft: true), Current));
    }

    [Fact]
    public void Release_without_the_exe_is_ignored()
    {
        Assert.Null(Updater.Parse(Release(assetName: "FortniteChestTracker.zip"), Current));
    }

    [Fact]
    public void Exe_under_the_name_of_versions_1_0_is_accepted_but_the_current_name_comes_first()
    {
        var legacy = Updater.Parse(Release(assetName: Legacy.AssetName, url: "https://github.com/me/repo/releases/download/v1.5.0/old.exe"), Current);
        Assert.NotNull(legacy);
        Assert.EndsWith("/old.exe", legacy.AssetUrl);

        // Both in the release (listed in any order): the current name is the one downloaded.
        var both = Updater.Parse(Release(assetName: Legacy.AssetName, otherAssets: new[] { AppInfo.ExeName }), Current);
        Assert.NotNull(both);
        Assert.EndsWith("/" + AppInfo.ExeName, both.AssetUrl);
        Assert.Equal(999, both.Size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("md5:abc")]
    [InlineData("sha256:tooshort")]
    public void Exe_that_cannot_be_verified_is_never_offered(string? digest)
    {
        Assert.Null(Updater.Parse(Release(digest: digest), Current));
    }

    [Fact]
    public void Download_must_use_https()
    {
        Assert.Null(Updater.Parse(Release(url: "http://example.com/FortniteChestTracker.exe"), Current));
    }

    [Fact]
    public void Garbage_answer_is_ignored()
    {
        Assert.Null(Updater.Parse("{not json", Current));
        Assert.Null(Updater.Parse("{}", Current));
    }

    [Theory]
    [InlineData("ayman-studio/fortnite-chest-tracker", true)]
    [InlineData("me/my.repo_2", true)]
    [InlineData("", false)]
    [InlineData("noslash", false)]
    [InlineData("a/b/c", false)]
    [InlineData("me/repo?x=1", false)]
    [InlineData("../evil/x", false)]
    public void Repository_names_are_validated(string repo, bool valid)
    {
        Assert.Equal(valid, AppInfo.IsRepoName(repo));
    }

    [Fact]
    public void Local_build_has_no_update_repository()
    {
        // The test build is a local build: it must never update itself from someone else's releases.
        Assert.False(AppInfo.UpdatesAvailable);
        Assert.Equal(AppInfo.ProjectRepo, AppInfo.Repo); // links (GitHub, bug reports) still go to the project
        // A build run through its dll (tests, Debug) is not the published exe: no update, install or logon entry from it.
        Assert.Null(AppInfo.ExePath);
    }
}
