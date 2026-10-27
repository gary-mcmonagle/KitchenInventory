using System.IO.Compression;

namespace CupboardTracker.Infrastructure.ModelTesting;

/// <summary>
/// The photo check's test image (#121), drawn in code so nothing real is in the repo: a plain made-up tin with a cream
/// label and large dark text, "TOMATO SOUP" and "400G", and no brand or logo. A small PNG (a few KB). The suite stores it
/// in the Function app's storage account the first time it's needed; Gary can replace that blob with his own.
/// </summary>
public static class TestPackImage
{
    public const int Width = 640;
    public const int Height = 400;

    private const int Scale = 9;

    // 5 x 7 glyphs, one string per row, '#' set. Only the characters the label uses.
    private static readonly Dictionary<char, string[]> Font = new()
    {
        ['T'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
        ['O'] = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['M'] = ["#...#", "##.##", "#.#.#", "#...#", "#...#", "#...#", "#...#"],
        ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['S'] = [".####", "#....", "#....", ".###.", "....#", "....#", "####."],
        ['U'] = ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['P'] = ["####.", "#...#", "#...#", "####.", "#....", "#....", "#...."],
        ['G'] = [".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###."],
        ['0'] = [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        [' '] = [".....", ".....", ".....", ".....", ".....", ".....", "....."],
    };

    private static readonly byte[] Background = [0xF4, 0xF1, 0xE8];
    private static readonly byte[] Band = [0xC8, 0x3A, 0x2E];
    private static readonly byte[] Ink = [0x24, 0x24, 0x28];

    public static byte[] Create()
    {
        var pixels = new byte[Width * Height * 3];
        Fill(pixels, 0, 0, Width, Height, Background);
        Fill(pixels, 0, 0, Width, 56, Band);
        Fill(pixels, 0, Height - 56, Width, 56, Band);
        Text(pixels, "TOMATO SOUP", 110);
        Text(pixels, "400G", 230);
        return Encode(pixels);
    }

    private static void Text(byte[] pixels, string text, int top)
    {
        var advance = 6 * Scale;
        var left = (Width - (text.Length * advance - Scale)) / 2;
        for (var i = 0; i < text.Length; i++)
        {
            var glyph = Font[text[i]];
            for (var row = 0; row < 7; row++)
            {
                for (var col = 0; col < 5; col++)
                {
                    if (glyph[row][col] == '#')
                    {
                        Fill(pixels, left + i * advance + col * Scale, top + row * Scale, Scale, Scale, Ink);
                    }
                }
            }
        }
    }

    private static void Fill(byte[] pixels, int x, int y, int width, int height, byte[] colour)
    {
        for (var row = y; row < y + height && row < Height; row++)
        {
            for (var col = x; col < x + width && col < Width; col++)
            {
                var at = (row * Width + col) * 3;
                pixels[at] = colour[0];
                pixels[at + 1] = colour[1];
                pixels[at + 2] = colour[2];
            }
        }
    }

    private static byte[] Encode(byte[] pixels)
    {
        // Each scanline starts with filter type 0 (none).
        var raw = new byte[Height * (Width * 3 + 1)];
        for (var row = 0; row < Height; row++)
        {
            Buffer.BlockCopy(pixels, row * Width * 3, raw, row * (Width * 3 + 1) + 1, Width * 3);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteInt(header, 0, Width);
        WriteInt(header, 4, Height);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolour
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        var body = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            body[i] = (byte)type[i];
        }

        Buffer.BlockCopy(data, 0, body, 4, data.Length);
        stream.Write(body);
        var crc = new byte[4];
        WriteInt(crc, 0, unchecked((int)Crc32(body)));
        stream.Write(crc);
    }

    private static void WriteInt(byte[] target, int at, int value)
    {
        target[at] = (byte)(value >> 24);
        target[at + 1] = (byte)(value >> 16);
        target[at + 2] = (byte)(value >> 8);
        target[at + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
