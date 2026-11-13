using System.Text.Json;

namespace FortniteChestTracker.Tests;

public class UpdaterTests
{
    private static readonly Version Current = new(1, 4, 0);
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Release(string tag = "v1.5.0", bool prerelease = false, bool draft = false, string assetName = AppInfo.ExeName,
        string? digest = Digest, string url = "https://github.com/me/repo/releases/download/v1.5.0/" + AppInfo.ExeName, string[]? otherAssets = null, bool signed = true)
    {
        Dictionary<string, object?> Asset(string name, string link, long size) => new()
        {
            ["name"] = name,
            ["browser_download_url"] = link,
            ["size"] = size,
            ["digest"] = digest,
        };
        var assets = (otherAssets ?? Array.Empty<string>()).Select(name => Asset(name, "https://github.com/me/repo/releases/download/v1.5.0/" + name, 999)).ToList();
        if (signed)
        {
            assets.Add(Asset(assetName + ".sig", "https://github.com/me/repo/releases/download/v1.5.0/" + assetName + ".sig", 96));
        }
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
        var both = Updater.Parse(Release(assetName: Legacy.AssetName, otherAssets: new[] { AppInfo.ExeName, AppInfo.ExeName + ".sig" }), Current);
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

    [Fact]
    public void Release_without_signature_is_ignored()
    {
        Assert.Null(Updater.Parse(Release(signed: false), Current));
        Assert.EndsWith(".exe.sig", Updater.Parse(Release(), Current)!.SignatureUrl);
    }

    [Fact]
    public void Only_the_release_key_signature_is_accepted()
    {
        using var release = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(release.ExportSubjectPublicKeyInfo());
        var exe = System.Text.Encoding.UTF8.GetBytes("MZ fake exe");
        var good = Convert.ToBase64String(release.SignData(exe, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
        var forged = Convert.ToBase64String(other.SignData(exe, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
        Assert.True(Updater.SignatureValid(exe, good, pub));
        Assert.False(Updater.SignatureValid(exe, forged, pub));
        Assert.False(Updater.SignatureValid(System.Text.Encoding.UTF8.GetBytes("MZ other exe"), good, pub));
        Assert.False(Updater.SignatureValid(exe, "not base64!", pub));
        Assert.False(Updater.SignatureValid(exe, good)); // a test key is not the release key
    }

    [Fact]
    public void A_release_signed_by_the_release_job_is_accepted()
    {
        // FCT_SIG_TEST = folder with test.exe and test.exe.sig made like the release job (openssl dgst -sha256 -sign)
        var dir = Environment.GetEnvironmentVariable("FCT_SIG_TEST");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "FCT_SIG_TEST is not set");
        Assert.True(Updater.SignatureValid(File.ReadAllBytes(Path.Combine(dir!, "test.exe")), File.ReadAllText(Path.Combine(dir!, "test.exe.sig"))));
    }

    private sealed class FakeDownloads(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Asked.Add(url);
            return Task.FromResult(files.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public void An_update_is_downloaded_with_its_signature_and_checked()
    {
        // 1.3.1 and 1.3.2 failed here every time: the client was changed after its first request
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var exe = System.Text.Encoding.ASCII.GetBytes("MZ" + new string('x', 200_000));
        var sig = Convert.ToBase64String(key.SignData(exe, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
        var update = new UpdateInfo(new Version(1, 5, 0), "v1.5.0", "https://example.test/app.exe", exe.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exe)), "https://example.test/", "https://example.test/app.exe.sig");
        var handler = new FakeDownloads(new() { [update.AssetUrl] = exe, [update.SignatureUrl] = System.Text.Encoding.ASCII.GetBytes(sig) });
        var target = Path.Combine(Path.GetTempPath(), "fct-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Updater.DownloadVerified(update, target, handler, pub);
            Assert.Equal(exe, File.ReadAllBytes(target));
            Assert.Equal(new[] { update.AssetUrl, update.SignatureUrl }, handler.Asked);

            // signed by another key: refused
            using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var forged = new FakeDownloads(new()
            {
                [update.AssetUrl] = exe,
                [update.SignatureUrl] = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(other.SignData(exe, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence))),
            });
            L.Set("en");
            Assert.Contains("signature", Assert.Throws<IOException>(() => Updater.DownloadVerified(update, target, forged, pub)).Message);
        }
        finally
        {
            File.Delete(target);
        }
    }
}
