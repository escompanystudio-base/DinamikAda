using System.Text;

namespace DinamikAda.Modules.Productivity;

/// <summary>
/// Pure ISO/IEC 18004 Byte-mode QR Code matrix generator (Versions 1..10, Error Correction L/M).
/// Produces a crisp boolean module grid (true = dark module) including a 2-module quiet zone option.
/// </summary>
public static class QrCodeEncoder
{
    private readonly record struct VersionSpec(
        int Version,
        int TotalCodewords,
        int EcCodewordsPerBlock,
        int Group1Blocks,
        int Group1DataCw,
        int Group2Blocks,
        int Group2DataCw,
        int[] AlignmentCenters);

    // ISO 18004 Table 9 — EC Level L (up to ~271 UTF-8 bytes, ample for LAN drop URLs & short text)
    private static readonly VersionSpec[] SpecsL =
    [
        new(1,  26,  7,  1, 19, 0, 0, []),
        new(2,  44,  10, 1, 34, 0, 0, [6, 18]),
        new(3,  70,  15, 1, 55, 0, 0, [6, 22]),
        new(4,  100, 20, 1, 80, 0, 0, [6, 26]),
        new(5,  134, 26, 1, 108, 0, 0, [6, 30]),
        new(6,  172, 18, 2, 68, 0, 0, [6, 34]),
        new(7,  196, 20, 2, 78, 0, 0, [6, 22, 38]),
        new(8,  242, 24, 2, 97, 0, 0, [6, 24, 42]),
        new(9,  292, 30, 2, 116, 0, 0, [6, 26, 46]),
        new(10, 346, 18, 2, 68, 2, 69, [6, 28, 50]),
    ];

    private static readonly byte[] GfExp = BuildGfExp();
    private static readonly byte[] GfLog = BuildGfLog();

    public static bool[,] Encode(string text, int quietZoneModules = 4)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (quietZoneModules is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(quietZoneModules));
        byte[] utf8 = Encoding.UTF8.GetBytes(text);

        VersionSpec? chosen = null;
        foreach (var spec in SpecsL)
        {
            int totalDataCw = spec.Group1Blocks * spec.Group1DataCw + spec.Group2Blocks * spec.Group2DataCw;
            int headerBits = spec.Version >= 10 ? 20 : 12; // 4-bit mode (0100) + 8/16-bit char count
            if ((headerBits + utf8.Length * 8 + 7) / 8 <= totalDataCw)
            {
                chosen = spec;
                break;
            }
        }

        if (chosen is null)
        {
            throw new ArgumentException("Text exceeds the supported QR code capacity.", nameof(text));
        }

        var v = chosen.Value;
        int size = v.Version * 4 + 17;
        byte[] dataCodewords = BuildDataCodewords(utf8, v);
        byte[] finalStream = InterleaveWithEcc(dataCodewords, v);

        var modules = new bool[size, size];
        var isFunction = new bool[size, size];

        DrawFunctionPatterns(modules, isFunction, v, size);
        PlaceCodewords(modules, isFunction, finalStream, size);

        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, isFunction, size, mask);
            WriteFormatBits(modules, size, mask);
            int p = ComputePenalty(modules, size);
            if (p < bestPenalty)
            {
                bestPenalty = p;
                bestMask = mask;
            }
            ApplyMask(modules, isFunction, size, mask);
        }

        ApplyMask(modules, isFunction, size, bestMask);
        WriteFormatBits(modules, size, bestMask);

        if (quietZoneModules <= 0) return modules;

        int fullSize = size + quietZoneModules * 2;
        var padded = new bool[fullSize, fullSize];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                padded[y + quietZoneModules, x + quietZoneModules] = modules[y, x];
            }
        }
        return padded;
    }

    private static byte[] BuildDataCodewords(byte[] bytes, VersionSpec spec)
    {
        int totalDataCw = spec.Group1Blocks * spec.Group1DataCw + spec.Group2Blocks * spec.Group2DataCw;
        var bits = new List<bool>(totalDataCw * 8);

        AppendBits(bits, 0b0100, 4); // Byte mode
        AppendBits(bits, bytes.Length, spec.Version >= 10 ? 16 : 8);
        foreach (byte b in bytes) AppendBits(bits, b, 8);

        int maxBits = totalDataCw * 8;
        int termLen = Math.Min(4, maxBits - bits.Count);
        AppendBits(bits, 0, termLen);
        while (bits.Count % 8 != 0) bits.Add(false);

        var result = new byte[totalDataCw];
        int cwIdx = 0;
        for (int i = 0; i < bits.Count; i += 8)
        {
            byte val = 0;
            for (int b = 0; b < 8; b++)
            {
                if (bits[i + b]) val |= (byte)(1 << (7 - b));
            }
            result[cwIdx++] = val;
        }

        byte[] pads = [0xEC, 0x11];
        for (int p = 0; cwIdx < totalDataCw; p++)
        {
            result[cwIdx++] = pads[p & 1];
        }
        return result;
    }

    private static byte[] InterleaveWithEcc(byte[] data, VersionSpec spec)
    {
        int blockCount = spec.Group1Blocks + spec.Group2Blocks;
        var dataBlocks = new byte[blockCount][];
        var ecBlocks = new byte[blockCount][];
        byte[] gen = ComputeGeneratorPoly(spec.EcCodewordsPerBlock);

        int offset = 0;
        for (int b = 0; b < blockCount; b++)
        {
            int len = b < spec.Group1Blocks ? spec.Group1DataCw : spec.Group2DataCw;
            var block = new byte[len];
            Array.Copy(data, offset, block, 0, len);
            offset += len;
            dataBlocks[b] = block;
            ecBlocks[b] = ComputeRemainder(block, gen);
        }

        var interleaved = new List<byte>(spec.TotalCodewords);
        int maxDataLen = Math.Max(spec.Group1DataCw, spec.Group2DataCw);
        for (int i = 0; i < maxDataLen; i++)
        {
            for (int b = 0; b < blockCount; b++)
            {
                if (i < dataBlocks[b].Length) interleaved.Add(dataBlocks[b][i]);
            }
        }
        for (int i = 0; i < spec.EcCodewordsPerBlock; i++)
        {
            for (int b = 0; b < blockCount; b++)
            {
                interleaved.Add(ecBlocks[b][i]);
            }
        }
        return interleaved.ToArray();
    }

    private static void DrawFunctionPatterns(bool[,] m, bool[,] f, VersionSpec spec, int size)
    {
        DrawFinderPattern(m, f, 0, 0, size);
        DrawFinderPattern(m, f, size - 7, 0, size);
        DrawFinderPattern(m, f, 0, size - 7, size);

        for (int i = 0; i < size; i++)
        {
            bool dark = (i & 1) == 0;
            if (!f[i, 6]) SetFunction(m, f, 6, i, dark);
            if (!f[6, i]) SetFunction(m, f, i, 6, dark);
        }

        int[] align = spec.AlignmentCenters;
        for (int ay = 0; ay < align.Length; ay++)
        {
            for (int ax = 0; ax < align.Length; ax++)
            {
                if ((ax == 0 && ay == 0) || (ax == 0 && ay == align.Length - 1) || (ax == align.Length - 1 && ay == 0))
                    continue;
                DrawAlignmentPattern(m, f, align[ax], align[ay]);
            }
        }

        // Format info reserves (overwritten with final BCH bits later)
        for (int i = 0; i <= 8; i++)
        {
            if (i != 6)
            {
                f[8, i] = true;
                f[i, 8] = true;
            }
            if (i < 8) f[8, size - 1 - i] = true;
            if (i < 7) f[size - 1 - i, 8] = true;
        }
        SetFunction(m, f, 8, size - 8, true); // Dark module

        if (spec.Version >= 7)
        {
            int vBits = ComputeVersionInfoBch(spec.Version);
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((vBits >> i) & 1) != 0;
                int a = size - 11 + (i % 3);
                int b = i / 3;
                SetFunction(m, f, b, a, bit);
                SetFunction(m, f, a, b, bit);
            }
        }
    }

    private static void DrawFinderPattern(bool[,] m, bool[,] f, int left, int top, int size)
    {
        for (int dy = -1; dy <= 7; dy++)
        {
            for (int dx = -1; dx <= 7; dx++)
            {
                int x = left + dx;
                int y = top + dy;
                if (x < 0 || y < 0 || x >= size || y >= size) continue;
                int dist = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                bool dark = dist != 2 && dist != 4;
                SetFunction(m, f, x, y, dark);
            }
        }
    }

    private static void DrawAlignmentPattern(bool[,] m, bool[,] f, int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(m, f, cx + dx, cy + dy, dist != 1);
            }
        }
    }

    private static void SetFunction(bool[,] m, bool[,] f, int x, int y, bool dark)
    {
        m[y, x] = dark;
        f[y, x] = true;
    }

    private static void PlaceCodewords(bool[,] m, bool[,] f, byte[] stream, int size)
    {
        int bitIdx = 0;
        int totalBits = stream.Length * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            bool upward = ((right + 1) & 2) == 0;
            for (int step = 0; step < size; step++)
            {
                int y = upward ? size - 1 - step : step;
                for (int col = 0; col < 2; col++)
                {
                    int x = right - col;
                    if (f[y, x]) continue;
                    bool dark = false;
                    if (bitIdx < totalBits)
                    {
                        dark = ((stream[bitIdx >> 3] >> (7 - (bitIdx & 7))) & 1) != 0;
                        bitIdx++;
                    }
                    m[y, x] = dark;
                }
            }
        }
    }

    private static void ApplyMask(bool[,] m, bool[,] f, int size, int mask)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (f[y, x]) continue;
                bool invert = mask switch
                {
                    0 => ((x + y) & 1) == 0,
                    1 => (y & 1) == 0,
                    2 => (x % 3) == 0,
                    3 => ((x + y) % 3) == 0,
                    4 => (((y / 2) + (x / 3)) & 1) == 0,
                    5 => ((x * y) % 2 + (x * y) % 3) == 0,
                    6 => (((x * y) % 2 + (x * y) % 3) & 1) == 0,
                    _ => (((x + y) % 2 + (x * y) % 3) & 1) == 0
                };
                if (invert) m[y, x] = !m[y, x];
            }
        }
    }

    private static void WriteFormatBits(bool[,] m, int size, int mask)
    {
        // EC Level L = 0b01 -> 5-bit data = (0b01 << 3) | mask; bit 14 is MSB, bit 0 is LSB.
        int data = (0b01 << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;

        // Copy 1 (top-left): LSB (bit 0) at (x=8, y=0) down to (8,8), then left to MSB (bit 14) at (x=0, y=8).
        for (int i = 0; i <= 5; i++) m[i, 8] = ((bits >> i) & 1) != 0;
        m[7, 8] = ((bits >> 6) & 1) != 0;
        m[8, 8] = ((bits >> 7) & 1) != 0;
        m[8, 7] = ((bits >> 8) & 1) != 0;
        for (int i = 9; i < 15; i++) m[8, 14 - i] = ((bits >> i) & 1) != 0;

        // Copy 2: MSB (bit 14..8) on bottom-left vertical (x=8, y=size-1..size-7);
        // LSB (bit 0..7) on top-right horizontal (x=size-1..size-8, y=8).
        for (int i = 0; i < 8; i++) m[8, size - 1 - i] = ((bits >> i) & 1) != 0;
        for (int i = 8; i < 15; i++) m[size - 15 + i, 8] = ((bits >> i) & 1) != 0;
    }

    private static int ComputeVersionInfoBch(int version)
    {
        int rem = version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ (((rem >> 11) & 1) * 0x1F25);
        return (version << 12) | rem;
    }

    private static int ComputePenalty(bool[,] m, int size)
    {
        int penalty = 0;
        for (int y = 0; y < size; y++)
        {
            int runX = 0, runY = 0;
            bool lastX = false, lastY = false;
            for (int x = 0; x < size; x++)
            {
                bool curX = m[y, x];
                runX = curX == lastX ? runX + 1 : 1;
                if (runX == 5) penalty += 3;
                else if (runX > 5) penalty += 1;
                lastX = curX;

                bool curY = m[x, y];
                runY = curY == lastY ? runY + 1 : 1;
                if (runY == 5) penalty += 3;
                else if (runY > 5) penalty += 1;
                lastY = curY;

                if (x + 1 < size && y + 1 < size)
                {
                    bool b0 = m[y, x];
                    if (m[y, x + 1] == b0 && m[y + 1, x] == b0 && m[y + 1, x + 1] == b0) penalty += 3;
                }
            }
        }

        int darkCount = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (m[y, x]) darkCount++;
        int total = size * size;
        int pct = darkCount * 100 / total;
        penalty += Math.Abs(pct - 50) / 5 * 10;
        return penalty;
    }

    private static void AppendBits(List<bool> bits, int val, int len)
    {
        for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0);
    }

    private static byte[] ComputeGeneratorPoly(int degree)
    {
        var poly = new byte[degree];
        poly[^1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                poly[j] = GfMul(poly[j], (byte)root);
                if (j + 1 < degree) poly[j] ^= poly[j + 1];
            }
            root = GfMul((byte)root, 2);
        }
        return poly;
    }

    private static byte[] ComputeRemainder(byte[] data, byte[] gen)
    {
        var rem = new byte[gen.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, rem.Length - 1);
            rem[^1] = 0;
            for (int i = 0; i < gen.Length; i++)
            {
                rem[i] ^= GfMul(gen[i], factor);
            }
        }
        return rem;
    }

    private static byte GfMul(byte x, byte y)
    {
        if (x == 0 || y == 0) return 0;
        return GfExp[(GfLog[x] + GfLog[y]) % 255];
    }

    private static byte[] BuildGfExp()
    {
        var exp = new byte[512];
        int v = 1;
        for (int i = 0; i < 255; i++)
        {
            exp[i] = (byte)v;
            exp[i + 255] = (byte)v;
            v <<= 1;
            if ((v & 0x100) != 0) v ^= 0x11D;
        }
        return exp;
    }

    private static byte[] BuildGfLog()
    {
        var log = new byte[256];
        var exp = BuildGfExp();
        for (int i = 0; i < 255; i++) log[exp[i]] = (byte)i;
        return log;
    }
}
