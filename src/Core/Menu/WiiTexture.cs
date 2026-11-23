using System.Buffers.Binary;

namespace PPlusTracklistEditor.Core.Menu;

public enum WiiTextureFormat { I4 = 0, I8 = 1, IA4 = 2, IA8 = 3, RGB565 = 4, RGB5A3 = 5, RGBA8 = 6, CI4 = 8, CI8 = 9, CMPR = 14 }

public enum WiiPaletteFormat { IA8 = 0, RGB565 = 1, RGB5A3 = 2 }

/// <summary>A decoded image: <see cref="Pixels"/> is 32-bit BGRA (not premultiplied), rows top to bottom.</summary>
public sealed record WiiImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Decodes Wii (GX) texture data. Pixels are stored in tiles ("blocks") left to right, top to bottom; inside a block,
/// row by row. Block size depends on the format: 8x8 (I4, CI4, CMPR), 8x4 (I8, IA4, CI8) or 4x4 (the 16- and 32-bit ones).
/// All values are big-endian.
/// </summary>
public static class WiiTexture
{
    public static WiiImage Decode(WiiTextureFormat format, int width, int height, ReadOnlySpan<byte> data,
                                  ushort[]? palette = null, WiiPaletteFormat paletteFormat = WiiPaletteFormat.RGB5A3)
    {
        var px = new byte[width * height * 4];
        (int bw, int bh) = format switch
        {
            WiiTextureFormat.I4 or WiiTextureFormat.CI4 or WiiTextureFormat.CMPR => (8, 8),
            WiiTextureFormat.I8 or WiiTextureFormat.IA4 or WiiTextureFormat.CI8 => (8, 4),
            WiiTextureFormat.IA8 or WiiTextureFormat.RGB565 or WiiTextureFormat.RGB5A3 or WiiTextureFormat.RGBA8 => (4, 4),
            _ => throw new InvalidDataException($"Unsupported texture format {format}."),
        };
        if (format is WiiTextureFormat.CI4 or WiiTextureFormat.CI8 && palette is null)
            throw new InvalidDataException("A palette texture needs its palette.");

        int at = 0;
        for (int by = 0; by < height; by += bh)
            for (int bx = 0; bx < width; bx += bw)
            {
                switch (format)
                {
                    case WiiTextureFormat.CMPR:
                        for (int sub = 0; sub < 4; sub++, at += 8)
                            DecodeDxt1(data.Slice(at, 8), px, width, height, bx + (sub & 1) * 4, by + (sub >> 1) * 4);
                        break;
                    case WiiTextureFormat.RGBA8:
                        for (int i = 0; i < 16; i++)
                        {
                            byte a = data[at + i * 2], r = data[at + i * 2 + 1], g = data[at + 32 + i * 2], b = data[at + 32 + i * 2 + 1];
                            Put(px, width, height, bx + i % 4, by + i / 4, r, g, b, a);
                        }
                        at += 64;
                        break;
                    default:
                        for (int y = 0; y < bh; y++)
                            for (int x = 0; x < bw; x++)
                            {
                                uint c = format switch
                                {
                                    WiiTextureFormat.I4 => Gray(Nibble(data, at, y * bw + x) * 0x11, 255),
                                    WiiTextureFormat.I8 => Gray(data[at + y * bw + x], 255),
                                    WiiTextureFormat.IA4 => Gray((data[at + y * bw + x] & 0xF) * 0x11, (data[at + y * bw + x] >> 4) * 0x11),
                                    WiiTextureFormat.IA8 => FromIA8(U16(data, at + (y * bw + x) * 2)),
                                    WiiTextureFormat.RGB565 => FromRGB565(U16(data, at + (y * bw + x) * 2)),
                                    WiiTextureFormat.RGB5A3 => FromRGB5A3(U16(data, at + (y * bw + x) * 2)),
                                    WiiTextureFormat.CI4 => FromPalette(palette!, paletteFormat, Nibble(data, at, y * bw + x)),
                                    _ => FromPalette(palette!, paletteFormat, data[at + y * bw + x]),             // CI8
                                };
                                Put(px, width, height, bx + x, by + y, (byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
                            }
                        at += 32;                                                                           // every block is 32 bytes
                        break;
                }
            }
        return new WiiImage(width, height, px);
    }

    private static int Nibble(ReadOnlySpan<byte> data, int at, int index) =>
        (index & 1) == 0 ? data[at + index / 2] >> 4 : data[at + index / 2] & 0xF;

    private static ushort U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    // Colors are packed as 0xAARRGGBB.
    private static uint Argb(int a, int r, int g, int b) => (uint)(a << 24 | r << 16 | g << 8 | b);
    private static uint Gray(int i, int a) => Argb(a, i, i, i);
    private static uint FromIA8(ushort v) => Gray(v & 0xFF, v >> 8);

    private static uint FromRGB565(ushort v) =>
        Argb(255, Expand5(v >> 11), Expand6((v >> 5) & 0x3F), Expand5(v & 0x1F));

    /// <summary>Top bit set: opaque RGB555. Clear: 3-bit alpha + RGB444.</summary>
    private static uint FromRGB5A3(ushort v) => (v & 0x8000) != 0
        ? Argb(255, Expand5((v >> 10) & 0x1F), Expand5((v >> 5) & 0x1F), Expand5(v & 0x1F))
        : Argb(Expand3((v >> 12) & 7), ((v >> 8) & 0xF) * 0x11, ((v >> 4) & 0xF) * 0x11, (v & 0xF) * 0x11);

    private static uint FromPalette(ushort[] palette, WiiPaletteFormat format, int index)
    {
        if (index >= palette.Length) return 0;
        ushort v = palette[index];
        return format switch
        {
            WiiPaletteFormat.IA8 => FromIA8(v),
            WiiPaletteFormat.RGB565 => FromRGB565(v),
            _ => FromRGB5A3(v),
        };
    }

    private static int Expand3(int v) => v << 5 | v << 2 | v >> 1;
    private static int Expand5(int v) => v << 3 | v >> 2;
    private static int Expand6(int v) => v << 2 | v >> 4;

    /// <summary>One 4x4 DXT1 tile, GameCube style: big-endian colors, and the 2-bit indexes with the first pixel in the top bits.</summary>
    private static void DecodeDxt1(ReadOnlySpan<byte> block, byte[] px, int width, int height, int x0, int y0)
    {
        ushort c0 = U16(block, 0), c1 = U16(block, 2);
        Span<uint> colors = stackalloc uint[4];
        colors[0] = FromRGB565(c0);
        colors[1] = FromRGB565(c1);
        int r0 = (byte)(colors[0] >> 16), g0 = (byte)(colors[0] >> 8), b0 = (byte)colors[0];
        int r1 = (byte)(colors[1] >> 16), g1 = (byte)(colors[1] >> 8), b1 = (byte)colors[1];
        if (c0 > c1)
        {
            colors[2] = Argb(255, (2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3);
            colors[3] = Argb(255, (r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3);
        }
        else
        {
            colors[2] = Argb(255, (r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);
            colors[3] = 0;
        }
        for (int y = 0; y < 4; y++)
        {
            byte bits = block[4 + y];
            for (int x = 0; x < 4; x++)
            {
                uint c = colors[(bits >> (6 - x * 2)) & 3];
                Put(px, width, height, x0 + x, y0 + y, (byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
            }
        }
    }

    private static void Put(byte[] px, int width, int height, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (x >= width || y >= height) return;                         // blocks overhang images that aren't a multiple of the block size
        int i = (y * width + x) * 4;
        px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a;
    }
}
