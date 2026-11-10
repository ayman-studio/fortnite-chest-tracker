using System.IO.Compression;

namespace FortniteChestTracker;

/// <summary>Minimal PNG writer and reader (no dependency): overlay pictures and previews.</summary>
public static class Png
{
    private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>Writes top-down 32-bit BGRA pixels as an opaque RGB PNG.</summary>
    public static void Write(string path, int width, int height, byte[] bgra) => File.WriteAllBytes(path, Encode(width, height, bgra, alpha: false));

    /// <summary>
    /// Encodes top-down 32-bit BGRA pixels (straight, not premultiplied, alpha) as a PNG, with its transparency when
    /// <paramref name="alpha"/> is set.
    /// </summary>
    public static byte[] Encode(int width, int height, byte[] bgra, bool alpha)
    {
        var channels = alpha ? 4 : 3;
        var raw = new byte[height * (1 + width * channels)];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                raw[o++] = bgra[i + 2];
                raw[o++] = bgra[i + 1];
                raw[o++] = bgra[i];
                if (alpha) raw[o++] = bgra[i + 3];
            }
        }
        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
        {
            zl.Write(raw);
        }
        var fs = new MemoryStream();
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)width);
        BE(ihdr, 4, (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = (byte)(alpha ? 6 : 2); // RGBA or RGB
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", z.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
        return fs.ToArray();
    }

    /// <summary>
    /// Reads an 8-bit RGB or RGBA, non-interlaced PNG (the pictures embedded in the exe) as top-down RGBA bytes.
    /// Null for anything else.
    /// </summary>
    public static (int Width, int Height, byte[] Rgba)? Decode(byte[] png)
    {
        try
        {
            if (png.Length < 33 || png[0] != 137 || png[1] != 80)
            {
                return null;
            }
            int width = 0, height = 0, type = 0;
            var idat = new MemoryStream();
            var p = 8;
            while (p + 8 <= png.Length)
            {
                var len = (int)RBE(png, p);
                var kind = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
                var data = p + 8;
                if (kind == "IHDR")
                {
                    width = (int)RBE(png, data);
                    height = (int)RBE(png, data + 4);
                    type = png[data + 9];
                    if (png[data + 8] != 8 || (type != 2 && type != 6) || png[data + 12] != 0)
                    {
                        return null;
                    }
                }
                else if (kind == "IDAT")
                {
                    idat.Write(png, data, len);
                }
                else if (kind == "IEND")
                {
                    break;
                }
                p = data + len + 4;
            }
            var bpp = type == 6 ? 4 : 3;
            var stride = width * bpp;
            var raw = new byte[height * (stride + 1)];
            idat.Position = 0;
            using (var zl = new ZLibStream(idat, CompressionMode.Decompress))
            {
                zl.ReadExactly(raw);
            }
            var img = new byte[height * stride];
            for (var y = 0; y < height; y++)
            {
                var filter = raw[y * (stride + 1)];
                var src = y * (stride + 1) + 1;
                var row = y * stride;
                for (var x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? img[row + x - bpp] : 0;
                    int b = y > 0 ? img[row - stride + x] : 0;
                    int c = x >= bpp && y > 0 ? img[row - stride + x - bpp] : 0;
                    int v = raw[src + x];
                    v += filter switch
                    {
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => Paeth(a, b, c),
                        _ => 0,
                    };
                    img[row + x] = (byte)v;
                }
            }
            if (bpp == 4)
            {
                return (width, height, img);
            }
            var rgba = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                rgba[i * 4] = img[i * 3];
                rgba[i * 4 + 1] = img[i * 3 + 1];
                rgba[i * 4 + 2] = img[i * 3 + 2];
                rgba[i * 4 + 3] = 255;
            }
            return (width, height, rgba);
        }
        catch
        {
            return null;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static uint RBE(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    private static void BE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BE(head, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        var c = 0xFFFFFFFFu;
        for (var i = 4; i < 8; i++) c = Crc[(c ^ head[i]) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        var crc = new byte[4];
        BE(crc, 0, c ^ 0xFFFFFFFF);
        s.Write(crc);
    }
}
