using System.Text;
using BillSystem.Services;

namespace BillSystem;

/// <summary>
/// 二维码自检。不看编码器的任何中间结果，只把它画出来的格子当成扫码器眼里的图，
/// 独立地解一遍：格式信息 → 反掩码 → 蛇形取码字 → 去交错 → RS 校验子 → 解回原文。
/// 编码这条链上任何一步错位，这儿都会解不出来。
/// </summary>
internal static class QrSelfTest
{
    public static void Run(Action<string, bool, string> check)
    {
        // 容量对照 ISO/IEC 18004 公布的"字节模式最多装几个字符"，看我这边的公式算得对不对
        (int Ver, QrEcc Ecc, int Chars)[] caps =
        {
            (1, QrEcc.Low, 17), (1, QrEcc.Medium, 14), (1, QrEcc.Quartile, 11), (1, QrEcc.High, 7),
            (2, QrEcc.Low, 32), (2, QrEcc.High, 14),
            (3, QrEcc.Low, 53), (3, QrEcc.High, 24),
            (10, QrEcc.Low, 271), (10, QrEcc.High, 119),
            (40, QrEcc.Low, 2953), (40, QrEcc.High, 1273),
        };
        foreach ((int Ver, QrEcc Ecc, int Chars) c in caps)
        {
            int header = c.Ver <= 9 ? 12 : 20;   // 模式 4 位 + 长度 8/16 位
            int got = (QrCode.NumDataCodewords(c.Ver, c.Ecc) * 8 - header) / 8;
            check($"容量 v{c.Ver}-{c.Ecc}={c.Chars} 字节", got == c.Chars, got.ToString());
        }

        const string url = "weixin://wxpay/bizpayurl?pr=Ab3kD9mZZ";
        foreach (QrEcc ecc in new[] { QrEcc.Low, QrEcc.Medium, QrEcc.Quartile, QrEcc.High })
            Roundtrip(check, url, ecc);

        Roundtrip(check, "", QrEcc.High);
        Roundtrip(check, "43栋422 · 30 元", QrEcc.High);       // 中文，UTF-8 三字节
        Roundtrip(check, new string('7', 200), QrEcc.High);    // 够长：带版本信息位 + 多块交错
        Roundtrip(check, new string('x', 300), QrEcc.Quartile);

        bool threw = false;
        try { QrCode.Encode(new string('z', 3000), QrEcc.High); }
        catch (ArgumentException) { threw = true; }
        check("超长内容拒绝编码", threw, "");
    }

    private static void Roundtrip(Action<string, bool, string> check, string text, QrEcc ecc)
    {
        string tag = $"{Encoding.UTF8.GetByteCount(text)}B/{ecc}";

        QrCode qr;
        try { qr = QrCode.Encode(text, ecc); }
        catch (Exception ex) { check($"编码 {tag}", false, ex.Message); return; }

        check($"边长 {tag}", qr.Size == qr.Version * 4 + 17, $"v{qr.Version} 边长 {qr.Size}");
        check($"固定黑格 {tag}", qr.Modules[qr.Size - 8, 8], "");
        check($"三个回字 {tag}", FindersOk(qr), "");

        var d = new Decoder(qr.Modules, qr.Size);
        if (!d.ReadFormat(out QrEcc gotEcc, out int mask, out string err))
        {
            check($"格式信息 {tag}", false, err);
            return;
        }
        check($"格式信息 {tag}", gotEcc == ecc && mask is >= 0 and < 8, $"{gotEcc} 掩码 {mask}");

        d.Unmask(mask);
        byte[] stream = d.ReadCodewords(out int spare);
        check($"码字铺满 {tag}", spare is >= 0 and < 8, $"剩 {spare} 位");

        if (!d.SplitAndVerify(stream, ecc, out byte[] data, out err))
        {
            check($"RS 校验子 {tag}", false, err);
            return;
        }
        check($"RS 校验子 {tag}", true, "");

        string? back = Decoder.DecodeBytes(data, qr.Version, out err);
        check($"解回原文 {tag}", back == text, back is null ? err : Trim(back));
    }

    private static string Trim(string s) => s.Length <= 24 ? s : s[..24] + "…";

    /// <summary>三个角上的回字是不是标准的 7×7。</summary>
    private static bool FindersOk(QrCode qr)
    {
        int n = qr.Size;
        foreach ((int oy, int ox) in new[] { (0, 0), (0, n - 7), (n - 7, 0) })
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 7; x++)
                    if (qr.Modules[oy + y, ox + x] != (Math.Max(Math.Abs(y - 3), Math.Abs(x - 3)) != 2))
                        return false;
        return true;
    }

    /// <summary>只为自检存在的迷你解码器：能力刚够验证自家编码器，不追求容错纠正。</summary>
    private sealed class Decoder
    {
        private readonly bool[,] _m;
        private readonly bool[,] _fn;
        private readonly int _n;
        private readonly int _ver;

        public Decoder(bool[,] modules, int size)
        {
            _n = size;
            _ver = (size - 17) / 4;
            _m = new bool[_n, _n];
            _fn = new bool[_n, _n];
            for (int y = 0; y < _n; y++)
                for (int x = 0; x < _n; x++)
                    _m[y, x] = modules[y, x];
            MarkFunction();
        }

        /// <summary>照 ISO 的图自己标一遍功能格，位置对不上就说明编码器画错地方了。</summary>
        private void MarkFunction()
        {
            for (int i = 0; i < _n; i++) { _fn[6, i] = true; _fn[i, 6] = true; }

            Block(0, 0, 9, 9);              // 左上：回字 + 分隔带 + 格式信息
            Block(0, _n - 8, 9, 8);         // 右上：回字 + 格式信息副本
            Block(_n - 8, 0, 8, 9);         // 左下：回字 + 格式信息副本 + 那格恒黑
            if (_ver >= 7)
            {
                Block(_n - 11, 0, 3, 6);
                Block(0, _n - 11, 6, 3);
            }

            int[] pos = AlignTable[_ver];
            for (int a = 0; a < pos.Length; a++)
                for (int b = 0; b < pos.Length; b++)
                {
                    bool corner = (a == 0 && b == 0)
                                  || (a == 0 && b == pos.Length - 1)
                                  || (a == pos.Length - 1 && b == 0);
                    if (!corner) Block(pos[a] - 2, pos[b] - 2, 5, 5);
                }
        }

        private void Block(int y, int x, int h, int w)
        {
            for (int dy = 0; dy < h; dy++)
                for (int dx = 0; dx < w; dx++)
                    _fn[y + dy, x + dx] = true;
        }

        /// <summary>读两处格式信息：要一致、BCH 要能整除，再拆出纠错等级和掩码号。</summary>
        public bool ReadFormat(out QrEcc ecc, out int mask, out string err)
        {
            ecc = QrEcc.Low;
            mask = -1;
            err = "";

            int a = 0, b = 0;
            for (int i = 0; i <= 5; i++) if (_m[i, 8]) a |= 1 << i;
            if (_m[7, 8]) a |= 1 << 6;
            if (_m[8, 8]) a |= 1 << 7;
            if (_m[8, 7]) a |= 1 << 8;
            for (int i = 9; i < 15; i++) if (_m[8, 14 - i]) a |= 1 << i;

            for (int i = 0; i < 8; i++) if (_m[8, _n - 1 - i]) b |= 1 << i;
            for (int i = 8; i < 15; i++) if (_m[_n - 15 + i, 8]) b |= 1 << i;

            if (a != b)
            {
                err = $"两处格式信息不一致 {a:X4}/{b:X4}";
                return false;
            }

            int raw = a ^ 0x5412;
            int rem = raw;
            for (int i = 14; i >= 10; i--)
                if ((rem >> i & 1) != 0) rem ^= 0x537 << (i - 10);
            if (rem != 0)
            {
                err = $"BCH 除不尽 {raw:X4}";
                return false;
            }

            int data = raw >> 10;
            int[] fromBits = { 1, 0, 3, 2 };          // 枚举值 → 编码值
            int bits = data >> 3;
            int idx = Array.IndexOf(fromBits, bits);
            if (idx < 0)
            {
                err = $"纠错等级位 {bits}";
                return false;
            }
            ecc = (QrEcc)idx;
            mask = data & 7;
            return true;
        }

        public void Unmask(int mask)
        {
            for (int y = 0; y < _n; y++)
                for (int x = 0; x < _n; x++)
                {
                    if (_fn[y, x]) continue;
                    _m[y, x] ^= mask switch
                    {
                        0 => (y + x) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (y + x) % 3 == 0,
                        4 => (y / 2 + x / 3) % 2 == 0,
                        5 => y * x % 2 + y * x % 3 == 0,
                        6 => (y * x % 2 + y * x % 3) % 2 == 0,
                        _ => ((y + x) % 2 + y * x % 3) % 2 == 0,
                    };
                }
        }

        /// <summary>两列一组、右列先读、组间上下折返；<paramref name="spare"/> 是末尾凑不满一字节的零散位。</summary>
        public byte[] ReadCodewords(out int spare)
        {
            var rights = new List<int>();
            for (int x = _n - 1; x >= 7; x -= 2) rights.Add(x);   // 一路排到 (8,7) 这组
            for (int x = 5; x >= 1; x -= 2) rights.Add(x);        // 第 6 列是定位虚线，整个跳过

            var cw = new byte[QrCode.NumRawDataModules(_ver) / 8];
            int bit = 0, seen = 0;
            bool up = true;
            foreach (int right in rights)
            {
                for (int k = 0; k < _n; k++)
                {
                    int y = up ? _n - 1 - k : k;
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int x = right - dx;
                        if (_fn[y, x]) continue;
                        seen++;
                        if (bit >= cw.Length * 8) continue;
                        if (_m[y, x]) cw[bit >> 3] |= (byte)(0x80 >> (bit & 7));
                        bit++;
                    }
                }
                up = !up;
            }
            spare = seen - bit;
            return cw;
        }

        /// <summary>
        /// 去交错拆回各块，再逐块算校验子 S_i = C(α^i)。RS 码的定义就是这些值全为 0，
        /// 跟生成多项式怎么造出来的无关，所以这条能独立验出编码器的纠错部分。
        /// </summary>
        public bool SplitAndVerify(byte[] stream, QrEcc ecc, out byte[] data, out string err)
        {
            data = Array.Empty<byte>();
            err = "";

            int blocks = QrCode.NumEccBlocks(_ver, ecc);
            int eccLen = QrCode.EccPerBlock(_ver, ecc);
            int shortLen = stream.Length / blocks;
            int numShort = blocks - stream.Length % blocks;

            var body = new byte[blocks][];
            for (int j = 0; j < blocks; j++)
                body[j] = new byte[j < numShort ? shortLen : shortLen + 1];

            int Len(int j) => shortLen - eccLen + (j < numShort ? 0 : 1);

            int at = 0;
            for (int i = 0; i <= shortLen - eccLen; i++)
                for (int j = 0; j < blocks; j++)
                    if (i < Len(j)) body[j][i] = stream[at++];
            for (int i = 0; i < eccLen; i++)
                for (int j = 0; j < blocks; j++)
                    body[j][Len(j) + i] = stream[at++];
            if (at != stream.Length)
            {
                err = $"拆块用了 {at} 个码字，一共 {stream.Length} 个";
                return false;
            }

            for (int j = 0; j < blocks; j++)
                for (int i = 0; i < eccLen; i++)
                {
                    byte root = Pow2(i), acc = 0;
                    foreach (byte c in body[j]) acc = (byte)(Mul(acc, root) ^ c);
                    if (acc != 0)
                    {
                        err = $"第 {j + 1}/{blocks} 块 S{i}={acc}";
                        return false;
                    }
                }

            var flat = new List<byte>();
            for (int j = 0; j < blocks; j++) flat.AddRange(body[j].Take(Len(j)));
            data = flat.ToArray();
            return true;
        }

        /// <summary>按字节模式把数据码字解回字符串。</summary>
        public static string? DecodeBytes(byte[] data, int ver, out string err)
        {
            err = "";
            int at = 0;

            int Read(int count)
            {
                int v = 0;
                for (int i = 0; i < count; i++, at++)
                {
                    if (at >= data.Length * 8) return -1;
                    v = v << 1 | (data[at >> 3] >> (7 - (at & 7)) & 1);
                }
                return v;
            }

            int mode = Read(4);
            if (mode != 0b0100)
            {
                err = $"模式位是 {mode:X}，不是字节模式";
                return null;
            }

            int len = Read(ver <= 9 ? 8 : 16);
            if (len < 0 || len > data.Length)
            {
                err = $"长度字段 {len} 不合理";
                return null;
            }

            var bytes = new byte[len];
            for (int i = 0; i < len; i++)
            {
                int v = Read(8);
                if (v < 0)
                {
                    err = "位数不够，数据被截断了";
                    return null;
                }
                bytes[i] = (byte)v;
            }

            int term = Read(4);
            if (term > 0)
            {
                err = $"结束符不是 0000（{term:X}）";
                return null;
            }
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>GF(2^8) 乘法，本原多项式 0x11D。这儿自己写一份，不借编码器那份。</summary>
        private static byte Mul(byte a, byte b)
        {
            int result = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((b >> i & 1) != 0) result ^= a << i;
            }
            for (int i = 14; i >= 8; i--)
            {
                if ((result >> i & 1) != 0) result ^= 0x11D << (i - 8);
            }
            return (byte)result;
        }

        /// <summary>α^i，α = 0x02。</summary>
        private static byte Pow2(int i)
        {
            byte v = 1;
            for (int k = 0; k < i; k++) v = Mul(v, 0x02);
            return v;
        }

        // 校正图形坐标，照 ISO/IEC 18004 附录 E 抄的（版本 1~20 够用了）
        private static readonly int[][] AlignTable =
        {
            Array.Empty<int>(), Array.Empty<int>(),
            new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
            new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
            new[] { 6, 30, 54 }, new[] { 6, 32, 58 }, new[] { 6, 34, 62 },
            new[] { 6, 26, 46, 66 }, new[] { 6, 26, 48, 70 }, new[] { 6, 26, 50, 74 },
            new[] { 6, 30, 54, 78 }, new[] { 6, 30, 56, 82 }, new[] { 6, 30, 58, 86 },
            new[] { 6, 34, 62, 90 },
        };
    }
}
