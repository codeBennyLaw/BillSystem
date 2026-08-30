using System.Drawing.Drawing2D;
using BillSystem.Models;
using BillSystem.Services;

namespace BillSystem.UI;

/// <summary>
/// 用 GDI+ 自己画的图表：用电量拟合成一条平滑曲线，剩余电量画柱子（用右边那根纵轴），
/// 充过值的那一格柱子换成绿色。整段历史就是一张表，滚轮（或按住左键拖）左右滑动，不分页。
/// 纵轴按整段历史定标，所以滑动过程中高度不会忽然改变比例。
/// </summary>
internal sealed class ChartControl : Control
{
    private List<Bucket> _data = new();
    private int _hover = -1;

    private readonly Anim _scrollA;
    private readonly Anim _growA;   // 换粒度/第一次拿到数据：柱子和曲线从底下长上来
    private double _scroll;      // 最左边露出来的是第几个桶，可以是小数
    private int _span = 30;      // 一屏放多少个桶
    private bool _pinned = true; // 贴着最右边：有新数据就跟着走

    private bool _pressing, _dragging;
    private int _dragX;
    private double _dragFrom;

    public ChartControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _scrollA = new Anim(this, 0, 180);
        _growA = new Anim(this, 1, 520);
        BackColor = Theme.Bg;
        Font = Theme.FontSmall;
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        AccessibleRole = AccessibleRole.Chart;
        AccessibleName = "用电量曲线";
        AccessibleDescription = "滚轮或拖动看历史，双击回到最新";
    }

    public Granularity Granularity { get; set; } = Granularity.Day;
    public string EmptyText { get; set; } = "暂无数据";

    /// <summary>一屏显示多少个区间。数据比这个少就整段铺开。</summary>
    public int Span
    {
        get => _span;
        set
        {
            _span = Math.Max(4, value);
            ClampScroll(true);
            Invalidate();
        }
    }

    public List<Bucket> Data
    {
        get => _data;
        set
        {
            List<Bucket> old = _data;
            _data = value ?? new List<Bucket>();
            _hover = -1;

            // 同一段历史只是末尾多了几格（每个整点来一条新读数就是这样）：贴着最右边看的时候
            // 让它滑过去，不要"跳"一下。换粒度、重开窗口那种整段换掉的还是直接就位。
            bool grew = _pinned
                        && old.Count > 0
                        && _data.Count > old.Count
                        && _data.Count - old.Count <= 4
                        && _data[0].Start == old[0].Start;
            ClampScroll(!grew);

            // 换粒度、第一次拿到数据才让它重新长一遍；每小时多出一格可不能整张表重来
            bool sameSeries = old.Count > 0 && _data.Count >= old.Count
                                            && _data.Count > 0 && _data[0].Start == old[0].Start;
            if (!sameSeries && _data.Count > 0)
            {
                _growA.Set(0);
                _growA.To(1);
            }

            Invalidate();
        }
    }

    private double MaxScroll => Math.Max(0, _data.Count - _span);

    /// <param name="instant">true = 不做动画（换粒度、换数据时内容整体变了，滑一下反而怪）。</param>
    private void ClampScroll(bool instant)
    {
        if (_pinned) _scroll = MaxScroll;
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        if (instant) _scrollA.Set(_scroll); else _scrollA.To(_scroll);
    }

    /// <summary>滚轮左右滑：往上滚是往过去看，一格走六分之一屏。</summary>
    public void ScrollByWheel(int delta)
    {
        if (MaxScroll <= 0) return;
        double step = Math.Max(1, _span / 6.0);
        _scroll = Math.Clamp(_scroll - delta / 120.0 * step, 0, MaxScroll);
        _pinned = _scroll >= MaxScroll - 1e-6;
        _scrollA.To(_scroll);
    }

    /// <summary>回到最右边（最新）。</summary>
    public void ScrollToEnd()
    {
        _pinned = true;
        ClampScroll(false);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollByWheel(e.Delta);
    }

    /// <summary>双击回到最新，省得从头滑回来。</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        _pressing = _dragging = false;
        ScrollToEnd();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressing = true;
        _dragging = false;
        _dragX = e.X;
        _dragFrom = _scroll;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressing = _dragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 按住拖着走：直接跟手，不要缓动
        if (_pressing && MaxScroll > 0)
        {
            if (!_dragging && Math.Abs(e.X - _dragX) < 3) return;
            _dragging = true;
            Cursor = Cursors.SizeWE;
            double slot = Math.Max(1.0, (double)Plot.Width / _span);
            _scroll = Math.Clamp(_dragFrom - (e.X - _dragX) / slot, 0, MaxScroll);
            _pinned = _scroll >= MaxScroll - 1e-6;
            _scrollA.Set(_scroll);
            if (_hover != -1) _hover = -1;
            Invalidate();
            return;
        }

        int idx = HitTest(e.X);
        if (idx != _hover) { _hover = idx; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _pressing = _dragging = false;
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    private const int PadTop = 34;
    private const int PadBottom = 44;
    private const int PadLeftBase = 58;

    /// <summary>右边这一列留给剩余电量的刻度，两条线一直都画。</summary>
    private const int PadRight = 58;

    private Rectangle Plot => new(
        PadLeftBase, PadTop,
        Math.Max(1, Width - PadLeftBase - PadRight),
        Math.Max(1, Height - PadTop - PadBottom));

    private float SlotW(Rectangle p) => (float)p.Width / _span;

    /// <summary>第 index 个桶的左边缘在哪（index 可以带小数，用来取中点）。</summary>
    private float XOf(Rectangle p, double index) => p.Left + (float)((index - _scrollA.Value) * SlotW(p));

    /// <summary>当前露出来的桶下标范围，两头各多给一个，好让内容从边上滑进来。</summary>
    private (int A, int B) VisibleRange()
    {
        int a = Math.Max(0, (int)Math.Floor(_scrollA.Value) - 1);
        int b = Math.Min(_data.Count - 1, (int)Math.Ceiling(_scrollA.Value + _span));
        return (a, b);
    }

    /// <summary>眼下这一屏里有没有充过值的格子（图例只解释看得见的东西）。</summary>
    private bool VisibleRecharged()
    {
        var (a, b) = VisibleRange();
        for (int i = a; i <= b; i++)
            if (_data[i].Recharged) return true;
        return false;
    }

    private int HitTest(int x)
    {
        if (_data.Count == 0) return -1;
        Rectangle p = Plot;
        if (x < p.Left || x > p.Right) return -1;
        int idx = (int)Math.Floor(_scrollA.Value + (x - p.Left) / SlotW(p));
        return idx >= 0 && idx < _data.Count ? idx : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 跟上面几张卡片一样的一块玻璃，图表就不像是直接飘在背景上
        Theme.Glass(g, Theme.Inner(this), 18f, 0.08f);

        // 只有一条抄表记录时用电量算不出来（要两条才能相减），但剩余电量是实打实的，
        // 所以有剩余电量就照样画，别整块空白
        bool anyUsage = _data.Any(b => b.Covered);
        bool anyRemain = _data.Any(b => b.Remaining is not null);
        if (_data.Count == 0 || (!anyUsage && !anyRemain))
        {
            using var dim = new SolidBrush(Theme.TextDim);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(EmptyText, Theme.FontBase, dim, ClientRectangle, sf);
            return;
        }

        Rectangle plot = Plot;

        // 纵轴按"整段历史"定标，不是按当前这一屏：滑动时比例不变，才看得出哪天高哪天低。
        // 一条记录都算不出用电量时（峰值 0）按 1 度画，不然左边一列刻度全是 0.00
        double peak = _data.Max(b => b.Usage);
        double yMax = NiceCeil(peak > 0 ? peak : 1);

        double maxRemain = 0;
        int remainCount = 0;
        foreach (var b in _data)
            if (b.Remaining is { } r) { remainCount++; maxRemain = Math.Max(maxRemain, r); }
        bool drawRemain = remainCount > 0;
        double rMax = NiceCeil(maxRemain <= 0 ? 1 : maxRemain);

        DrawGrid(g, plot, yMax, rMax, drawRemain);

        int partial = PartialIndex();

        // 半个身子在外面的柱子/线段裁掉，别画到坐标轴外面去
        g.SetClip(new Rectangle(plot.Left, plot.Top - 8, plot.Width + 1, plot.Height + 9));
        // 柱子先画，曲线压在上面：曲线是主角（用电量），柱子是背景（剩余电量）
        if (drawRemain) DrawRemainBars(g, plot, rMax, remainCount == 1);
        DrawUsageCurve(g, plot, yMax, !drawRemain, partial);
        g.ResetClip();

        DrawXAxis(g, plot);
        DrawLegend(g, plot, drawRemain);
        DrawScrollBar(g, plot);
        if (_hover >= 0 && _hover < _data.Count)
            DrawHover(g, plot, yMax, rMax, drawRemain);
    }

    private const int Ticks = 4;

    /// <summary>
    /// 最后有数据的那一格常常只走了一半（这个小时才过了二十分钟，或者最后一次抄表落在格子中间），
    /// 用电量天然比前面矮一截。找出来单独画成虚线 + 空心点，不然曲线右端看着像"用电突然掉下去了"。
    /// </summary>
    private int PartialIndex()
    {
        for (int i = _data.Count - 1; i >= 0; i--)
            if (_data[i].Covered) return _data[i].Partial ? i : -1;
        return -1;
    }

    private void DrawGrid(Graphics g, Rectangle p, double yMax, double rMax, bool drawRemain)
    {
        // 玻璃卡片上不能画实色线（会变成一道暗痕），网格改成极淡的白
        using var gridPen = new Pen(Color.FromArgb(20, 255, 255, 255), 1);
        using var axisPen = new Pen(Color.FromArgb(48, 255, 255, 255), 1);
        using var sub = new SolidBrush(Theme.TextSub);
        using var dim = new SolidBrush(Theme.TextDim);
        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        using var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

        for (int i = 0; i <= Ticks; i++)
        {
            float y = p.Bottom - (float)i / Ticks * p.Height;
            g.DrawLine(i == 0 ? axisPen : gridPen, p.Left, y, p.Right, y);

            double v = yMax * i / Ticks;
            g.DrawString(Fmt(v), Theme.FontSmall, sub,
                new RectangleF(8, y - 8, PadLeftBase - 16, 16), right);

            if (drawRemain)
            {
                double rv = rMax * i / Ticks;
                g.DrawString(Fmt(rv), Theme.FontSmall, dim,
                    new RectangleF(p.Right + 8, y - 8, PadRight - 14, 16), left);
            }
        }

        g.DrawString("度", Theme.FontSmall, dim, new RectangleF(8, p.Top - 22, PadLeftBase - 16, 16), right);
        if (drawRemain)
            g.DrawString("剩余", Theme.FontSmall, dim, new RectangleF(p.Right + 8, p.Top - 22, PadRight, 16), left);
    }

    /// <param name="lone">整段历史只有一条抄表记录：那根孤零零的柱子上面把数值标出来。</param>
    private void DrawRemainBars(Graphics g, Rectangle p, double rMax, bool lone)
    {
        float slot = SlotW(p);
        float bw = Math.Max(2f, Math.Min(slot * 0.62f, 42f));
        var (a, b) = VisibleRange();

        float loneX = 0, loneTop = 0;
        double loneVal = 0;
        bool drewLone = false;

        for (int i = a; i <= b; i++)
        {
            if (_data[i].Remaining is not { } r) continue;

            // 剩余为 0 也画一条贴底的细线，表示"确实抄到了，就是没电了"
            float h = Math.Max(1.5f, (float)(r / rMax * p.Height)) * (float)_growA.Value;
            float x = XOf(p, i) + (slot - bw) / 2f;
            bool hot = i == _hover;

            var rect = new RectangleF(x, p.Bottom - h, bw, h);
            using var path = Theme.TopRoundedRect(rect, Math.Min(4f, bw / 2f));
            using var brush = new LinearGradientBrush(
                new RectangleF(rect.X, rect.Y, rect.Width, Math.Max(rect.Height, 1f)),
                Color.FromArgb(hot ? 245 : 175, Theme.Remain),
                Color.FromArgb(hot ? 130 : 55, Theme.Remain),
                LinearGradientMode.Vertical);
            g.FillPath(brush, path);

            // 充过值那一格：只把"比上一格多出来"的那一截换成绿色，底下原有的那截还是金色
            if (_data[i].Recharged && GainHeight(i, r, rMax, p.Height) is { } gain && gain > 0.5f)
            {
                GraphicsState st = g.Save();
                g.IntersectClip(new RectangleF(rect.X - 1, rect.Top - 1, rect.Width + 2, gain + 1));
                using (var up = new LinearGradientBrush(
                           new RectangleF(rect.X, rect.Top, rect.Width, Math.Max(gain, 1f)),
                           Color.FromArgb(hot ? 250 : 210, Theme.Recharge),
                           Color.FromArgb(hot ? 210 : 150, Theme.Recharge),
                           LinearGradientMode.Vertical))
                    g.FillPath(up, path);
                g.Restore(st);
            }

            loneX = rect.X + bw / 2f;
            loneTop = rect.Top;
            loneVal = r;
            drewLone = true;
        }

        if (!lone || !drewLone) return;

        string txt = $"{loneVal:0.00} 度";
        SizeF sz = g.MeasureString(txt, Theme.FontSmall);
        float tx = Math.Clamp(loneX - sz.Width / 2, p.Left + 2, p.Right - sz.Width - 2);
        using var label = new SolidBrush(Theme.Remain);
        g.DrawString(txt, Theme.FontSmall, label, tx, Math.Max(p.Top - 2, loneTop - sz.Height - 2));
    }

    /// <summary>
    /// 第 <paramref name="i"/> 格的柱子里"比上一格多出来"的那一截有多高（像素）。
    /// 找的是前面最近一格有读数的，中间空着的格子跳过；没多出来（或前面没数据）返回 null。
    /// </summary>
    private float? GainHeight(int i, double cur, double rMax, int plotH)
    {
        double? prev = null;
        for (int k = i - 1; k >= 0; k--)
            if (_data[k].Remaining is { } pv) { prev = pv; break; }

        if (prev is not { } p0 || cur <= p0) return null;
        return (float)((cur - p0) / rMax * plotH) * (float)_growA.Value;
    }

    /// <param name="partial">还没走完的那一格的下标（<c>-1</c> 表示没有）。</param>
    private void DrawUsageCurve(Graphics g, Rectangle p, double yMax, bool fillArea, int partial)
    {
        var seg = new List<PointF>();
        bool tail = false;   // 这一段的最后一个点正好是"还没走完"的那格
        var (a, b) = VisibleRange();

        void Flush()
        {
            if (seg.Count > 0) PaintSegment(g, p, seg, fillArea, tail);
            seg.Clear();
            tail = false;
        }

        for (int i = a; i <= b; i++)
        {
            Bucket bk = _data[i];
            if (!bk.Covered) { Flush(); continue; }
            seg.Add(new PointF(XOf(p, i + 0.5),
                p.Bottom - (float)(bk.Usage / yMax * p.Height * _growA.Value)));
            tail = i == partial;
        }
        Flush();
    }

    /// <summary>
    /// 把这一段的点连成一条平滑曲线：单调三次 Hermite 插值（Fritsch–Carlson 限幅），
    /// 换算成贝塞尔段交给 GDI+。两个点之间没得插，直连。
    ///
    /// 不用 <c>AddCurve</c> 那种基数样条，是因为它会<b>过冲</b>：两次抄表之间的用电量是平摊的，
    /// 好几格数值一模一样，样条会在中间鼓一个包；落差大的地方还会甩到 0 以下，看着像用了负电。
    /// 这个插值保证曲线不超出相邻两点的范围——相等的那几格画出来就是平的。
    /// </summary>
    private static void AddCurve(GraphicsPath path, List<PointF> pts)
    {
        int n = pts.Count;
        if (n < 3)
        {
            path.AddLines(pts.ToArray());
            return;
        }

        // 每一段的斜率
        var d = new float[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            float dx = pts[i + 1].X - pts[i].X;
            d[i] = dx <= 0 ? 0 : (pts[i + 1].Y - pts[i].Y) / dx;
        }

        // 每个点的切线取左右两段斜率的平均；一升一降（是个拐点）就压平，免得冲出去
        var m = new float[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++)
            m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2f;

        // Fritsch–Carlson：切线再收一收，保证每一段都不超出这一段两头的范围
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }   // 相等的两格之间画平的
            float a = m[i] / d[i], b = m[i + 1] / d[i];
            float s = a * a + b * b;
            if (s <= 9) continue;
            float t = 3f / MathF.Sqrt(s);
            m[i] = t * a * d[i];
            m[i + 1] = t * b * d[i];
        }

        for (int i = 0; i < n - 1; i++)
        {
            float dx = (pts[i + 1].X - pts[i].X) / 3f;
            path.AddBezier(
                pts[i],
                new PointF(pts[i].X + dx, pts[i].Y + m[i] * dx),
                new PointF(pts[i + 1].X - dx, pts[i + 1].Y - m[i + 1] * dx),
                pts[i + 1]);
        }
    }

    private void PaintSegment(Graphics g, Rectangle p, List<PointF> pts, bool fillArea, bool tailPartial)
    {
        if (pts.Count == 1)
        {
            if (tailPartial) { OpenDot(g, pts[0]); return; }
            using var dot = new SolidBrush(Theme.Accent);
            g.FillEllipse(dot, pts[0].X - 2.5f, pts[0].Y - 2.5f, 5, 5);
            return;
        }

        // 叠着剩余电量柱子时不铺渐变面：半透明的面盖在柱子上，两边的颜色都脏了
        if (fillArea)
        {
            using var area = new GraphicsPath();
            AddCurve(area, pts);
            area.AddLine(pts[^1].X, p.Bottom, pts[0].X, p.Bottom);
            area.CloseFigure();
            using var fill = new LinearGradientBrush(
                new RectangleF(p.Left, p.Top, Math.Max(p.Width, 1), Math.Max(p.Height, 1)),
                Color.FromArgb(90, Theme.Accent), Color.FromArgb(6, Theme.Accent),
                LinearGradientMode.Vertical);
            g.FillPath(fill, area);
        }

        using var curve = new GraphicsPath();
        AddCurve(curve, pts);

        using var pen = new Pen(Theme.Accent, 2f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        if (!tailPartial)
        {
            g.DrawPath(pen, curve);
        }
        else
        {
            // 同一条曲线画两遍，用裁剪区在最后一个数据点那儿切开：
            // 走完的那截实线，还没走完的那截虚线，接口处曲率是连着的
            float cut = pts[^2].X;
            GraphicsState st = g.Save();
            g.IntersectClip(RectangleF.FromLTRB(p.Left - 40, p.Top - 40, cut, p.Bottom + 40));
            g.DrawPath(pen, curve);
            g.Restore(st);

            st = g.Save();
            g.IntersectClip(RectangleF.FromLTRB(cut, p.Top - 40, p.Right + 40, p.Bottom + 40));
            using (var dash = new Pen(Color.FromArgb(170, Theme.Accent), 2f)
                   {
                       DashStyle = DashStyle.Dash,
                       LineJoin = LineJoin.Round,
                   })
                g.DrawPath(dash, curve);
            g.Restore(st);
        }

        int solid = tailPartial ? pts.Count - 1 : pts.Count;
        if (pts.Count <= 60)
        {
            using var dot = new SolidBrush(Theme.Accent);
            for (int i = 0; i < solid; i++) g.FillEllipse(dot, pts[i].X - 2f, pts[i].Y - 2f, 4, 4);
        }

        if (tailPartial) OpenDot(g, pts[^1]);
    }

    /// <summary>空心点：这一格还在往上走，跟已经定了的那些实心点区分开。</summary>
    private static void OpenDot(Graphics g, PointF pt)
    {
        // 卡片本身是透的，圈里要压一块暗色才看得出"空心"
        using var core = new SolidBrush(Color.FromArgb(215, 0x11, 0x14, 0x1A));
        using var ring = new Pen(Theme.Accent, 1.8f);
        g.FillEllipse(core, pt.X - 3f, pt.Y - 3f, 6f, 6f);
        g.DrawEllipse(ring, pt.X - 3f, pt.Y - 3f, 6f, 6f);
    }

    private void DrawXAxis(Graphics g, Rectangle p)
    {
        float slot = SlotW(p);
        var (a, b) = VisibleRange();
        using var sub = new SolidBrush(Theme.TextSub);
        using var dim = new SolidBrush(Theme.TextDim);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

        // 按标签宽度决定隔几个画一个，免得糊成一团
        float widest = 8;
        for (int i = a; i <= b; i++)
            widest = Math.Max(widest, g.MeasureString(XLabel(_data[i]), Theme.FontSmall).Width);
        int stride = NiceStride((int)Math.Ceiling((widest + 12) / Math.Max(slot, 1f)));

        // 刻度挑在"绝对时间"的整数倍上（每 6 小时、每周一、每季度……），
        // 这样滑动时标签是跟着内容一起走的，不会每滑一格就重新排一遍
        for (int i = a; i <= b; i++)
        {
            if (AxisKey(_data[i]) % stride != 0) continue;
            float cx = XOf(p, i + 0.5);
            // 只给露在图里的桶画标签：多给的那一个桶还在坐标轴外面，标签会跑到纵轴数字那一列去
            if (cx < p.Left || cx > p.Right) continue;
            bool boundary = Granularity == Granularity.Hour && _data[i].Start.Hour == 0;
            g.DrawString(XLabel(_data[i]), Theme.FontSmall, boundary ? sub : dim,
                new RectangleF(cx - 45, p.Bottom + 8, 90, 16), center);
        }
    }

    /// <summary>绝对时间刻度值：小时粒度下 %24==0 正好是午夜，天粒度下 %7==0 正好是周一。</summary>
    private int AxisKey(Bucket b) => Granularity switch
    {
        Granularity.Hour => (int)(b.Start - DateTime.MinValue).TotalHours,
        Granularity.Day => (int)(b.Start - DateTime.MinValue).TotalDays,
        Granularity.Month => b.Start.Year * 12 + b.Start.Month,
        _ => b.Start.Year,
    };

    private int NiceStride(int need)
    {
        int[] ladder = Granularity switch
        {
            Granularity.Hour => new[] { 1, 2, 3, 4, 6, 8, 12, 24, 48, 72, 120, 240 },
            Granularity.Day => new[] { 1, 2, 3, 7, 14, 28, 56, 112 },
            Granularity.Month => new[] { 1, 2, 3, 6, 12, 24 },
            _ => new[] { 1, 2, 5, 10 },
        };
        foreach (int s in ladder)
            if (s >= need) return s;
        return ladder[^1];
    }

    private string XLabel(Bucket b) =>
        Granularity == Granularity.Hour && b.Start.Hour == 0
            ? b.Start.ToString("MM-dd")
            : b.Label;

    private void DrawLegend(Graphics g, Rectangle p, bool drawRemain)
    {
        float x = p.Left;
        float y = 10;
        using var text = new SolidBrush(Theme.TextSub);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center };

        void Item(Color c, string label, bool bar)
        {
            using var b = new SolidBrush(c);
            if (bar)
            {
                using GraphicsPath bp = Theme.TopRoundedRect(new RectangleF(x + 1, y + 3, 8, 12), 2.5f);
                g.FillPath(b, bp);
            }
            else
            {
                // 曲线的图例就画成一小段线加个点，跟图里长得一样，不用猜哪个是哪个
                using var pen = new Pen(c, 2f);
                g.DrawLine(pen, x, y + 9, x + 11, y + 9);
                g.FillEllipse(b, x + 4f, y + 7f, 4f, 4f);
            }

            x += 15;
            SizeF sz = g.MeasureString(label, Theme.FontSmall);
            g.DrawString(label, Theme.FontSmall, text, new RectangleF(x, y, sz.Width + 4, 18), sf);
            x += sz.Width + 14;
        }

        Item(Theme.Accent, $"用电量（每{UsageAggregator.UnitName(Granularity)}）", false);
        if (drawRemain) Item(Theme.Remain, "剩余电量", true);
        // 绿柱子只在眼下这一屏真有充值的时候解释一句，滑到没充过的那几天就不占地方
        if (drawRemain && VisibleRecharged()) Item(Theme.Recharge, "充值", true);

        // 当前露出来的是哪一段：原来那排翻页按钮和区间文字就是被这行替掉的
        string cap = RangeCaption();
        if (cap.Length == 0) return;
        using var dim = new SolidBrush(Theme.TextDim);
        using var far = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        float capX = Math.Max(x + 8, p.Right - 320);
        g.DrawString(cap, Theme.FontSmall, dim, new RectangleF(capX, y, p.Right - capX, 18), far);
    }

    private string RangeCaption()
    {
        if (_data.Count == 0) return "";
        int first = Math.Clamp((int)Math.Round(_scrollA.Value), 0, _data.Count - 1);
        int last = Math.Clamp(first + _span - 1, 0, _data.Count - 1);
        string from = UsageAggregator.LongLabel(_data[first].Start, Granularity);
        string to = UsageAggregator.LongLabel(_data[last].Start, Granularity);
        string range = first == last ? from : $"{from} — {to}";
        return _pinned ? $"{range}（最新）" : range;
    }

    /// <summary>底下那条细滚动条：看得出现在停在整段历史的哪儿，也提示"这张表可以滑"。</summary>
    private void DrawScrollBar(Graphics g, Rectangle p)
    {
        if (MaxScroll <= 0 || _data.Count == 0) return;

        const float th = 3.5f;
        float y = Height - 10f;
        var track = new RectangleF(p.Left, y, p.Width, th);
        using (GraphicsPath tp = Theme.RoundedRect(track, th / 2f))
        using (var tb = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
            g.FillPath(tb, tp);

        float w = Math.Max(28f, track.Width * (float)(_span / (double)_data.Count));
        float x = track.Left + (track.Width - w) * (float)(_scrollA.Value / MaxScroll);
        using (GraphicsPath kp = Theme.RoundedRect(new RectangleF(x, y, w, th), th / 2f))
        using (var kb = new SolidBrush(Color.FromArgb(_dragging ? 220 : 150, Theme.Accent)))
            g.FillPath(kb, kp);
    }

    private void DrawHover(Graphics g, Rectangle p, double yMax, double rMax, bool drawRemain)
    {
        Bucket b = _data[_hover];
        float cx = XOf(p, _hover + 0.5);
        if (cx < p.Left || cx > p.Right) return;

        // 整格先垫一层浅色：光一根虚线不太看得出"现在读的是哪一格"
        float slot = SlotW(p);
        float bandL = Math.Max(p.Left, cx - slot / 2f);
        float bandR = Math.Min(p.Right, cx + slot / 2f);
        if (bandR - bandL > 0.5f)
            using (var band = new SolidBrush(Color.FromArgb(16, Theme.Text)))
                g.FillRectangle(band, bandL, p.Top, bandR - bandL, p.Height);

        using (var pen = new Pen(Color.FromArgb(70, Theme.Text), 1) { DashStyle = DashStyle.Dot })
            g.DrawLine(pen, cx, p.Top, cx, p.Bottom);

        // 曲线上那一点套个圈，指到哪儿一目了然
        if (b.Covered)
        {
            float py = p.Bottom - (float)(b.Usage / yMax * p.Height * _growA.Value);
            using (var halo = new SolidBrush(Color.FromArgb(60, Theme.Accent)))
                g.FillEllipse(halo, cx - 6.5f, py - 6.5f, 13f, 13f);
            using (var core = new SolidBrush(Theme.Accent))
                g.FillEllipse(core, cx - 3f, py - 3f, 6f, 6f);
            using (var ring = new Pen(Theme.Bg, 1.6f))
                g.DrawEllipse(ring, cx - 3f, py - 3f, 6f, 6f);
        }

        var lines = new List<(string Text, Color Color, Font Font)>
        {
            (UsageAggregator.LongLabel(b.Start, Granularity), Theme.Text, Theme.FontBold),
        };

        if (b.Covered)
            lines.Add(($"用电  {b.Usage:0.00} 度", Theme.Accent, Theme.FontSmall));
        else
            lines.Add(("未采集到数据", Theme.TextDim, Theme.FontSmall));

        if (drawRemain && b.Remaining is { } rem)
            lines.Add(($"剩余  {rem:0.00} 度", Theme.Remain, Theme.FontSmall));

        // 这一格的柱子是绿的，气泡里把金额写出来
        if (b.Recharged)
            lines.Add(($"充值  {b.RechargeYuan:0.##} 元", Theme.Recharge, Theme.FontSmall));

        // 只盖到半格：数字还会往上走，不写一句容易当成"用得少"
        if (b.Partial)
            lines.Add((b.End > DateTime.Now
                ? $"这{UsageAggregator.UnitName(Granularity)}还没过完"
                : "这一格只抄到一半", Theme.TextDim, Theme.FontSmall));

        float w = 0, h = 6;
        foreach (var (t, _, f) in lines)
        {
            SizeF sz = g.MeasureString(t, f);
            w = Math.Max(w, sz.Width);
            h += sz.Height + 2;
        }
        w += 20;
        h += 6;

        float bx = cx + 12;
        if (bx + w > Width - 4) bx = cx - 12 - w;
        bx = Math.Max(4, bx);

        float top = p.Bottom - (float)(Math.Max(b.Usage, 0) / yMax * p.Height) - h - 12;
        if (drawRemain && b.Remaining is { } r2)
            top = Math.Min(top, p.Bottom - (float)(r2 / rMax * p.Height) - h - 12);
        top = Math.Clamp(top, 4, Math.Max(4, Height - h - 4));

        var box = new RectangleF(bx, top, w, h);
        // 气泡也是玻璃，只是厚一点：底下压着曲线和柱子，太透就看不清字了
        Theme.Glass(g, box, 10f, 0.55f, 1.5f);

        float ty = box.Y + 6;
        foreach (var (t, c, f) in lines)
        {
            using var br = new SolidBrush(c);
            g.DrawString(t, f, br, box.X + 10, ty);
            ty += g.MeasureString(t, f).Height + 2;
        }
    }

    private static string Fmt(double v) =>
        v >= 1000 ? v.ToString("0") : v >= 100 ? v.ToString("0.#") : v >= 10 ? v.ToString("0.0") : v.ToString("0.00");

    /// <summary>把坐标轴上限凑成 1/2/2.5/4/5/6/8/10 这种好看的数。</summary>
    private static double NiceCeil(double v)
    {
        if (v <= 0) return 1;
        double exp = Math.Floor(Math.Log10(v));
        double pow = Math.Pow(10, exp);
        double f = v / pow;
        double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 4 ? 4 : f <= 5 ? 5 : f <= 6 ? 6 : f <= 8 ? 8 : 10;
        return nf * pow;
    }
}
