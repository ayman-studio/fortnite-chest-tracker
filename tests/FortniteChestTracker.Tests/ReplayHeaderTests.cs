using System.Text;

namespace FortniteChestTracker.Tests;

public class ReplayHeaderTests
{
    /// <summary>Builds the start of a replay file, as Fortnite writes it.</summary>
    private static MemoryStream Header(bool live, bool encrypted = true, byte[]? key = null, uint fileVersion = 7,
        string name = "Unsaved Replay", bool utf16Name = false, int customVersions = 2, uint magic = 0x1CA2E27F, bool truncateAfterLive = false)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(magic);
        w.Write(fileVersion);
        if (fileVersion >= 7)
        {
            w.Write(customVersions);
            for (var i = 0; i < customVersions; i++)
            {
                w.Write(new byte[16]); // guid
                w.Write(i);            // version
            }
        }
        w.Write(600_000u); // LengthInMS
        w.Write(19u);      // NetworkVersion
        w.Write(12345u);   // Changelist
        if (utf16Name)
        {
            var bytes = Encoding.Unicode.GetBytes(name + "\0");
            w.Write(-(bytes.Length / 2));
            w.Write(bytes);
        }
        else
        {
            var bytes = Encoding.ASCII.GetBytes(name + "\0");
            w.Write(bytes.Length);
            w.Write(bytes);
        }
        w.Write(live ? 1u : 0u);
        if (!truncateAfterLive)
        {
            w.Write(638_000_000_000_000_000L); // timestamp
            w.Write(1u);                         // compressed
            w.Write(encrypted ? 1u : 0u);
            if (encrypted)
            {
                key ??= new byte[32];
                w.Write(key.Length);
                w.Write(key);
            }
        }
        w.Flush();
        ms.Position = 0;
        return ms;
    }

    private static byte[] RealKey() => Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Match_in_progress_is_live_without_key()
    {
        Assert.Equal(new ReplayHeaderInfo(IsLive: true, KeyMissing: true), ReplayHeaderProbe.Read(Header(live: true)));
    }

    [Fact]
    public void Finished_match_has_its_key()
    {
        Assert.Equal(new ReplayHeaderInfo(IsLive: false, KeyMissing: false), ReplayHeaderProbe.Read(Header(live: false, key: RealKey())));
    }

    [Fact]
    public void Unencrypted_replay_needs_no_key()
    {
        Assert.Equal(new ReplayHeaderInfo(IsLive: false, KeyMissing: false), ReplayHeaderProbe.Read(Header(live: false, encrypted: false)));
    }

    [Fact]
    public void Utf16_name_and_old_file_version_are_skipped_correctly()
    {
        Assert.Equal(new ReplayHeaderInfo(IsLive: true, KeyMissing: false),
            ReplayHeaderProbe.Read(Header(live: true, key: RealKey(), fileVersion: 6, name: "Partie éclair", utf16Name: true)));
    }

    [Fact]
    public void Not_a_replay()
    {
        Assert.Null(ReplayHeaderProbe.Read(Header(live: false, magic: 0xDEADBEEF)));
        Assert.Null(ReplayHeaderProbe.Read(new MemoryStream(new byte[] { 1, 2 })));
    }

    [Fact]
    public void Absurd_custom_version_count_is_rejected()
    {
        Assert.Null(ReplayHeaderProbe.Read(Header(live: false, customVersions: 1000)));
    }

    [Fact]
    public void Header_cut_after_the_live_flag_counts_as_key_missing()
    {
        Assert.Equal(new ReplayHeaderInfo(IsLive: true, KeyMissing: true), ReplayHeaderProbe.Read(Header(live: true, truncateAfterLive: true)));
    }

    [Fact]
    public void Truncated_key_counts_as_missing()
    {
        var full = Header(live: false, key: RealKey()).ToArray();
        Assert.Equal(new ReplayHeaderInfo(IsLive: false, KeyMissing: true), ReplayHeaderProbe.Read(new MemoryStream(full[..^10])));
    }

    [Fact]
    public void File_helpers_agree_with_the_stream_reader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fct-test-{Guid.NewGuid():N}.replay");
        try
        {
            File.WriteAllBytes(path, Header(live: true).ToArray());
            Assert.True(ReplayHeaderProbe.IsLive(path));
            Assert.True(ReplayHeaderProbe.KeyMissing(path));

            File.WriteAllBytes(path, Header(live: false, key: RealKey()).ToArray());
            Assert.False(ReplayHeaderProbe.IsLive(path));
            Assert.False(ReplayHeaderProbe.KeyMissing(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Null(ReplayHeaderProbe.IsLive(path));       // missing file
        Assert.True(ReplayHeaderProbe.KeyMissing(path));
    }
}
