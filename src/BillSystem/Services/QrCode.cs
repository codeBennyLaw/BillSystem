namespace BillSystem.Services;

/// <summary>纠错等级。四档能纠的比例大致是 7% / 15% / 25% / 30%。</summary>
internal enum QrEcc
{
    Low = 0,
    Medium = 1,
    Quartile = 2,
    High = 3,
}

/// <summary>
/// QR 码编码器（Model 2，字节模式）。
///
/// 学校那边的接口只回一串 <c>qrCode</c> 文本，二维码是网页自己画的，所以这儿得自己编一个。
/// 项目不引第三方包，就照 ISO/IEC 18004 实现最小可用的一套：字节模式 + 自动选版本 +
/// Reed-Solomon 纠错 + 8 种掩码里挑罚分最低的那个。
///
/// 用法：<c>bool[,] m = QrCode.Encode(text, QrEcc.High).Modules;</c>，true 是黑格。
/// </summary>
internal sealed class QrCode
{
    public const int MinVersion = 1;
    public const int MaxVersion = 40;

    /// <summary>版本号 1..40。</summary>
    public int Version { get; }

    /// <summary>边长（模块数），等于 21 + 4 * (版本 - 1)。</summary>
    public int Size { get; }

    public QrEcc Ecc { get; }

    /// <summary><c>[y, x]</c>，true = 黑。</summary>
    public bool[,] Modules { get; }

    private readonly bool[,] _isFunction;

    private QrCode(int version, QrEcc ecc, byte[] dataCodewords, int mask)
    {
        if (version is < MinVersion or > MaxVersion) throw new ArgumentOutOfRangeException(nameof(version));

        Version = version;
        Ecc = ecc;
        Size = version * 4 + 17;
        Modules = new bool[Size, Size];
        _isFunction = new bool[Size, Size];

        DrawFunctionPatterns();
        DrawCodewords(AddEccAndInterleave(dataCodewords));

        // 八种掩码都试一遍，挑罚分最低的（掩码是 XOR，试完再 XOR 回来就复原了）
        if (mask < 0)
        {
            long best = long.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                ApplyMask(i);
                DrawFormatBits(i);
                long score = PenaltyScore();
                if (score < best) { best = score; mask = i; }
                ApplyMask(i);
            }
        }

        ApplyMask(mask);
        DrawFormatBits(mask);
    }

    /// <summary>把文本按字节模式（UTF-8）编成二维码，版本自动取够用的最小值。</summary>
    public static QrCode Encode(string text, QrEcc ecc = QrEcc.Medium)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text ?? "");
        var bits = new BitBuffer();

        for (int ver = MinVersion; ver <= MaxVersion; ver++)
        {
            int capacity = NumDataCodewords(ver, ecc) * 8;
            // 字节模式的段头：模式 0100 + 长度（版本 1~9 是 8 位，10 以上 16 位）
            int lenBits = ver <= 9 ? 8 : 16;
            int need = 4 + lenBits + bytes.Length * 8;
            if (need > capacity) continue;
            if (bytes.Length >= 1 << lenBits) continue;

            bits.Clear();
            bits.Append(0b0100, 4);
            bits.Append(bytes.Length, lenBits);
            foreach (byte b in bytes) bits.Append(b, 8);

            // 结束符 + 补到整字节 + 交替填充 0xEC 0x11
            bits.Append(0, Math.Min(4, capacity - bits.Length));
            bits.Append(0, (8 - bits.Length % 8) % 8);
            for (int pad = 0xEC; bits.Length < capacity; pad ^= 0xEC ^ 0x11)
                bits.Append(pad, 8);

            return new QrCode(ver, ecc, bits.ToBytes(), -1);
        }

        throw new ArgumentException($"内容太长，装不进二维码（{bytes.Length} 字节）。", nameof(text));
    }

    // ---------- 功能图形 ----------

    private void DrawFunctionPatterns()
    {
        // 定位用的两条虚线
        for (int i = 0; i < Size; i++)
        {
            SetFunction(6, i, i % 2 == 0);
            SetFunction(i, 6, i % 2 == 0);
        }

        // 三个回字（连同外面那一圈留白）
        DrawFinder(3, 3);
        DrawFinder(Size - 4, 3);
        DrawFinder(3, Size - 4);

        int[] pos = AlignmentPositions();
        for (int i = 0; i < pos.Length; i++)
            for (int j = 0; j < pos.Length; j++)
            {
                // 三个角上是回字，不画校正图形
                bool corner = (i == 0 && j == 0)
                              || (i == 0 && j == pos.Length - 1)
                              || (i == pos.Length - 1 && j == 0);
                if (!corner) DrawAlignment(pos[i], pos[j]);
            }

        DrawFormatBits(0);   // 先占位，掩码定了再覆盖一次
        DrawVersion();
    }

    private void DrawFinder(int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || x >= Size || y < 0 || y >= Size) continue;
                int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(x, y, d != 2 && d != 4);
            }
    }

    private void DrawAlignment(int cx, int cy)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunction(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private void DrawFormatBits(int mask)
    {
        int[] formatBits = { 1, 0, 3, 2 };   // L M Q H 的编码值，顺序跟枚举不一样
        int data = formatBits[(int)Ecc] << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = rem << 1 ^ (rem >> 9) * 0x537;
        int bits = (data << 10 | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++) SetFunction(8, i, Bit(bits, i));
        SetFunction(8, 7, Bit(bits, 6));
        SetFunction(8, 8, Bit(bits, 7));
        SetFunction(7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunction(14 - i, 8, Bit(bits, i));

        for (int i = 0; i < 8; i++) SetFunction(Size - 1 - i, 8, Bit(bits, i));
        for (int i = 8; i < 15; i++) SetFunction(8, Size - 15 + i, Bit(bits, i));
        SetFunction(8, Size - 8, true);   // 这一格永远是黑的
    }

    private void DrawVersion()
    {
        if (Version < 7) return;

        int rem = Version;
        for (int i = 0; i < 12; i++) rem = rem << 1 ^ (rem >> 11) * 0x1F25;
        int bits = Version << 12 | rem;

        for (int i = 0; i < 18; i++)
        {
            bool bit = Bit(bits, i);
            int a = Size - 11 + i % 3, b = i / 3;
            SetFunction(a, b, bit);
            SetFunction(b, a, bit);
        }
    }

    private int[] AlignmentPositions()
    {
        if (Version == 1) return Array.Empty<int>();

        int n = Version / 7 + 2;
        int step = (Version * 8 + n * 3 + 5) / (n * 4 - 4) * 2;
        var result = new int[n];
        result[0] = 6;
        for (int i = 1; i < n; i++) result[i] = Size - 7 - (n - 1 - i) * step;
        return result;
    }

    private void SetFunction(int x, int y, bool dark)
    {
        Modules[y, x] = dark;
        _isFunction[y, x] = true;
    }

    private static bool Bit(int v, int i) => (v >> i & 1) != 0;

    // ---------- 纠错与排布 ----------

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int numBlocks = EccBlocks[(int)Ecc][Version];
        int blockEccLen = EccCodewordsPerBlock[(int)Ecc][Version];
        int rawCodewords = NumRawDataModules(Version) / 8;
        int numShort = numBlocks - rawCodewords % numBlocks;
        int shortLen = rawCodewords / numBlocks;

        byte[] divisor = RsDivisor(blockEccLen);
        var blocks = new byte[numBlocks][];
        int k = 0;
        for (int i = 0; i < numBlocks; i++)
        {
            int datLen = shortLen - blockEccLen + (i < numShort ? 0 : 1);
            var dat = new byte[datLen];
            Array.Copy(data, k, dat, 0, datLen);
            k += datLen;

            byte[] ecc = RsRemainder(dat, divisor);
            // 短块也按长块的长度存，中间空一格，交错时跳过
            var blk = new byte[shortLen + 1];
            Array.Copy(dat, blk, datLen);
            Array.Copy(ecc, 0, blk, shortLen + 1 - blockEccLen, blockEccLen);
            blocks[i] = blk;
        }

        // 各块按列交错拼成一条
        var result = new byte[rawCodewords];
        int at = 0;
        for (int i = 0; i <= shortLen; i++)
            for (int j = 0; j < numBlocks; j++)
                if (i != shortLen - blockEccLen || j >= numShort)
                    result[at++] = blocks[j][i];
        return result;
    }

    /// <summary>码字按"两列一组、上下折返"的蛇形填进非功能格。</summary>
    private void DrawCodewords(byte[] data)
    {
        int i = 0;
        // 从右下角起，每两列一组往左走；组内上下折返
        for (int col = Size - 1; col >= 1; col -= 2)
        {
            int right = col <= 6 ? col - 1 : col;   // 第 6 列是定位虚线，整组左移一格
            for (int vert = 0; vert < Size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y, x] && i < data.Length * 8)
                    {
                        Modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                    // 余下的零散位（0~7 个）留白就行
                }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (_isFunction[y, x]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    _ => throw new ArgumentOutOfRangeException(nameof(mask)),
                };
                Modules[y, x] ^= invert;
            }
    }

    // ---------- 掩码罚分 ----------

    private const int PenaltyN1 = 3, PenaltyN2 = 3, PenaltyN3 = 40, PenaltyN4 = 10;

    private long PenaltyScore()
    {
        long result = 0;

        // 同色连成 5 格以上要罚；顺带找"1:1:3:1:1"那种像回字的假图形
        for (int y = 0; y < Size; y++)
        {
            bool runColor = false;
            int runLen = 0;
            var history = new int[7];
            for (int x = 0; x < Size; x++)
            {
                if (Modules[y, x] == runColor)
                {
                    runLen++;
                    if (runLen == 5) result += PenaltyN1;
                    else if (runLen > 5) result++;
                }
                else
                {
                    AddHistory(runLen, history);
                    if (!runColor) result += CountFinderLike(history) * PenaltyN3;
                    runColor = Modules[y, x];
                    runLen = 1;
                }
            }
            result += TerminateAndCount(runColor, runLen, history) * PenaltyN3;
        }

        for (int x = 0; x < Size; x++)
        {
            bool runColor = false;
            int runLen = 0;
            var history = new int[7];
            for (int y = 0; y < Size; y++)
            {
                if (Modules[y, x] == runColor)
                {
                    runLen++;
                    if (runLen == 5) result += PenaltyN1;
                    else if (runLen > 5) result++;
                }
                else
                {
                    AddHistory(runLen, history);
                    if (!runColor) result += CountFinderLike(history) * PenaltyN3;
                    runColor = Modules[y, x];
                    runLen = 1;
                }
            }
            result += TerminateAndCount(runColor, runLen, history) * PenaltyN3;
        }

        // 2x2 同色块
        for (int y = 0; y < Size - 1; y++)
            for (int x = 0; x < Size - 1; x++)
            {
                bool c = Modules[y, x];
                if (c == Modules[y, x + 1] && c == Modules[y + 1, x] && c == Modules[y + 1, x + 1])
                    result += PenaltyN2;
            }

        // 黑白比例离一半太远也要罚
        int dark = 0;
        foreach (bool b in Modules) if (b) dark++;
        int total = Size * Size;
        int k = (int)((Math.Abs((long)dark * 20 - (long)total * 10) + total - 1) / total) - 1;
        result += (long)k * PenaltyN4;
        return result;
    }

    private void AddHistory(int runLen, int[] history)
    {
        if (history[0] == 0) runLen += Size;   // 行首那段当作紧贴着留白
        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = runLen;
    }

    private int TerminateAndCount(bool runColor, int runLen, int[] history)
    {
        if (runColor)
        {
            AddHistory(runLen, history);
            runLen = 0;
        }
        runLen += Size;   // 行尾也补一段留白
        AddHistory(runLen, history);
        return CountFinderLike(history);
    }

    /// <summary>刚记完一段白之后才能调；返回 0/1/2。</summary>
    private static int CountFinderLike(int[] h)
    {
        int n = h[1];
        bool core = n > 0 && h[2] == n && h[4] == n && h[5] == n && h[3] == n * 3;
        return (core && h[0] >= n * 4 && h[6] >= n ? 1 : 0)
               + (core && h[6] >= n * 4 && h[0] >= n ? 1 : 0);
    }

    // ---------- Reed-Solomon（GF(2^8)，本原多项式 0x11D）----------

    private static byte[] RsDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;

        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = GfMul(result[j], (byte)root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = GfMul((byte)root, 0x02);
        }
        return result;
    }

    private static byte[] RsRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < divisor.Length; i++)
                result[i] ^= GfMul(divisor[i], factor);
        }
        return result;
    }

    /// <summary>GF(2^8) 上的乘法（俄罗斯农夫法）。</summary>
    public static byte GfMul(byte x, byte y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = z << 1 ^ (z >> 7) * 0x11D;
            z ^= (y >> i & 1) * x;
        }
        return (byte)z;
    }

    // ---------- 容量表 ----------

    public static int NumRawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    public static int NumDataCodewords(int ver, QrEcc ecc)
        => NumRawDataModules(ver) / 8
           - EccCodewordsPerBlock[(int)ecc][ver] * EccBlocks[(int)ecc][ver];

    /// <summary>纠错块数。自检里那份独立解码要照这个拆块。</summary>
    public static int NumEccBlocks(int ver, QrEcc ecc) => EccBlocks[(int)ecc][ver];

    /// <summary>每块的纠错码字数。</summary>
    public static int EccPerBlock(int ver, QrEcc ecc) => EccCodewordsPerBlock[(int)ecc][ver];

    // 下标 0 占位（版本从 1 起）
    private static readonly int[][] EccCodewordsPerBlock =
    {
        new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly int[][] EccBlocks =
    {
        new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    /// <summary>攒二进制位，最后按字节倒出来。</summary>
    private sealed class BitBuffer
    {
        private readonly List<byte> _bytes = new();
        private int _bits;

        public int Length => _bits;

        public void Clear()
        {
            _bytes.Clear();
            _bits = 0;
        }

        public void Append(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (_bits % 8 == 0) _bytes.Add(0);
                if ((value >> i & 1) != 0) _bytes[^1] |= (byte)(1 << 7 - _bits % 8);
                _bits++;
            }
        }

        public byte[] ToBytes() => _bytes.ToArray();
    }
}
