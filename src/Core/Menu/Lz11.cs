namespace PPlusTracklistEditor.Core.Menu;

/// <summary>
/// Nintendo's LZ77 "type 0x11" compression, used for whole files like <c>pf/menu2/mu_menumain.pac</c>.
///
/// Format: byte 0x11, the decompressed size (3 bytes little-endian; or 0 and then 4 bytes if it's 16 MB or more), then
/// blocks of one flag byte + 8 items. A 0 flag bit is a literal byte; a 1 bit is a back-reference "copy N bytes from D
/// bytes back" in 2, 3 or 4 bytes depending on N (3–16, 17–272, 273–65808). D is at most 4096.
/// </summary>
public static class Lz11
{
    private const int MaxDistance = 0x1000, MinMatch = 3, MaxMatch = 0x10110;

    /// <summary>Largest decompressed size accepted (real menu files are a few MB); stops a bad header asking for GBs.</summary>
    private const int MaxOutput = 256 * 1024 * 1024;

    public static bool LooksCompressed(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0x11;

    public static byte[] Decompress(ReadOnlySpan<byte> src)
    {
        if (!LooksCompressed(src)) throw new InvalidDataException("This file isn't LZ11-compressed.");
        int size = src[1] | (src[2] << 8) | (src[3] << 16), p = 4;
        if (size == 0) { if (src.Length < 8) return []; size = BitConverter.ToInt32(src[4..8]); p = 8; }
        if (size < 0 || size > MaxOutput) throw new InvalidDataException("The compressed data claims an impossible size, so the file is damaged.");
        var output = new byte[size];
        int o = 0;
        try
        {
            while (o < size)
            {
                int flags = src[p++];
                for (int bit = 7; bit >= 0 && o < size; bit--)
                {
                    if ((flags & (1 << bit)) == 0) { output[o++] = src[p++]; continue; }
                    int b1 = src[p++], len, dist;
                    switch (b1 >> 4)
                    {
                        case 0:
                        {
                            int b2 = src[p++], b3 = src[p++];
                            len = (((b1 & 0xF) << 4) | (b2 >> 4)) + 0x11;
                            dist = (((b2 & 0xF) << 8) | b3) + 1;
                            break;
                        }
                        case 1:
                        {
                            int b2 = src[p++], b3 = src[p++], b4 = src[p++];
                            len = (((b1 & 0xF) << 12) | (b2 << 4) | (b3 >> 4)) + 0x111;
                            dist = (((b3 & 0xF) << 8) | b4) + 1;
                            break;
                        }
                        default:
                        {
                            int b2 = src[p++];
                            len = (b1 >> 4) + 1;
                            dist = (((b1 & 0xF) << 8) | b2) + 1;
                            break;
                        }
                    }
                    if (dist > o) throw new InvalidDataException("The compressed data is damaged (it points before the start).");
                    for (int i = 0; i < len && o < size; i++, o++) output[o] = output[o - dist];
                }
            }
        }
        catch (IndexOutOfRangeException) { throw new InvalidDataException("The compressed data ends too early."); }
        return output;
    }

    /// <summary>
    /// Compresses with a hash-chain match finder and one-step lazy matching (take a literal if the next byte starts a
    /// longer match). Output decompresses to exactly <paramref name="data"/>; it won't be byte-identical to Nintendo's
    /// tools, but it's about as small.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> source, int chainLimit = 256)
    {
        byte[] data = source.ToArray();
        int n = data.Length;
        var output = new List<byte>(n / 2 + 16) { 0x11 };
        if (n < 1 << 24) { output.Add((byte)n); output.Add((byte)(n >> 8)); output.Add((byte)(n >> 16)); }
        else { output.AddRange([0, 0, 0]); output.AddRange(BitConverter.GetBytes(n)); }

        const int HashBits = 16;
        var head = new int[1 << HashBits];
        Array.Fill(head, -1);
        var prev = new int[MaxDistance]; // chain links, indexed by position % window
        int Hash(int i) => (int)(((uint)(data[i] << 16 | data[i + 1] << 8 | data[i + 2]) * 0x9E3779B1u) >> (32 - HashBits));

        int inserted = 0;
        void InsertUpTo(int pos)
        {
            for (; inserted < pos && inserted + MinMatch <= n; inserted++)
            {
                int h = Hash(inserted);
                prev[inserted % MaxDistance] = head[h];
                head[h] = inserted;
            }
        }

        (int Len, int Dist) Find(int pos)
        {
            byte[] d = data;
            if (pos + MinMatch > n) return (0, 0);
            InsertUpTo(pos);
            int best = 0, bestDist = 0, limit = Math.Min(MaxMatch, n - pos);
            int cand = head[Hash(pos)];
            for (int steps = 0; cand >= 0 && pos - cand <= MaxDistance && steps < chainLimit; steps++)
            {
                if (cand < pos && d[cand + best] == d[pos + best])
                {
                    int len = 0;
                    while (len < limit && d[cand + len] == d[pos + len]) len++;
                    if (len > best) { best = len; bestDist = pos - cand; if (len == limit) break; }
                }
                int next = prev[cand % MaxDistance];
                if (next >= cand) break; // the slot was reused by a newer position: the chain has left the window
                cand = next;
            }
            return best >= MinMatch ? (best, bestDist) : (0, 0);
        }

        int i = 0;
        while (i < n)
        {
            int flagAt = output.Count;
            output.Add(0);
            byte flags = 0;
            for (int bit = 7; bit >= 0 && i < n; bit--)
            {
                var (len, dist) = Find(i);
                if (len > 0 && len < 64 && i + 1 < n)
                {
                    var (nextLen, _) = Find(i + 1);
                    if (nextLen > len + 1) len = 0; // a literal now buys a longer match next
                }
                if (len == 0) { output.Add(data[i++]); continue; }

                flags |= (byte)(1 << bit);
                int d = dist - 1;
                if (len <= 0x10)
                {
                    output.Add((byte)(((len - 1) << 4) | (d >> 8)));
                    output.Add((byte)d);
                }
                else if (len <= 0x110)
                {
                    int l = len - 0x11;
                    output.Add((byte)(l >> 4));
                    output.Add((byte)(((l & 0xF) << 4) | (d >> 8)));
                    output.Add((byte)d);
                }
                else
                {
                    int l = len - 0x111;
                    output.Add((byte)(0x10 | (l >> 12)));
                    output.Add((byte)(l >> 4));
                    output.Add((byte)(((l & 0xF) << 4) | (d >> 8)));
                    output.Add((byte)d);
                }
                i += len;
            }
            output[flagAt] = flags;
        }
        while (output.Count % 4 != 0) output.Add(0); // Nintendo's files are padded to 4 bytes
        return [.. output];
    }
}
