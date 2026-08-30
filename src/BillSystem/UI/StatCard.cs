using System.Drawing.Drawing2D;

namespace BillSystem.UI;

/// <summary>顶部那几张数字卡片：标题 + 大数字 + 单位 + 一行小字。</summary>
internal sealed class StatCard : Control
{
    private string _title = "";
    private string _value = "--";
    private string _unit = "";
    private string _sub = "";
    private Color _valueColor = Theme.Text;
    private Color _colorFrom = Theme.Text;

    // 数字变了就滚过去，不是直接换掉
    private readonly Anim _valA;
    private int _decimals;

    /// <summary>入场（淡入 + 微微上浮）、悬停抬起、数字换色，三样都是渐变。</summary>
    private readonly Anim _enterA, _hoverA, _colorA;

    public StatCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _valA = new Anim(this, 0, 420);
        _enterA = new Anim(this, 1, 380);
        _hoverA = new Anim(this, 0, 140);
        _colorA = new Anim(this, 1, 260);
        BackColor = Theme.Bg;
        Height = 104;
    }

    /// <summary>
    /// 从下往上淡入一次。<paramref name="delayMs"/> 让同一排的卡片依次错开，
    /// 看着像"铺开"而不是"一起蹦出来"。窗口第一次露脸时叫一次就够。
    /// </summary>
    public void Reveal(int delayMs)
    {
        _enterA.Set(0);
        _enterA.To(1, delayMs);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hoverA.To(1); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverA.To(0); }

    public string Title { get => _title; set { _title = value; SyncAccessible(); Invalidate(); } }
    public string Unit { get => _unit; set { _unit = value; SyncAccessible(); Invalidate(); } }

    public Color ValueColor
    {
        get => _valueColor;
        set => SetColor(value);
    }

    public string Value
    {
        get => _value;
        set => Set(value);
    }

    public string Sub
    {
        get => _sub;
        set { _sub = value; Invalidate(); }
    }

    /// <summary>换颜色也是滑过去的：绿→黄→红那一下跳变很扎眼。</summary>
    private void SetColor(Color c)
    {
        if (c.ToArgb() == _valueColor.ToArgb()) return;
        _colorFrom = Painted;
        _valueColor = c;
        _colorA.Set(0);
        _colorA.To(1);
        Invalidate();
    }

    /// <summary>眼下真正画出来的那个颜色（换色动画的中间值）。</summary>
    private Color Painted => Theme.Mix(_colorFrom, _valueColor, (float)_colorA.Value);

    public void Set(string value, string? sub = null, Color? color = null)
    {
        if (value != _value)
        {
            // 两头都是数字才滚；"--" 这种就直接换
            if (double.TryParse(_value, out double from) && double.TryParse(value, out double to))
            {
                _decimals = Decimals(value);
                // 上一次还在滚就从当下的数接着滚，别先跳回旧值再重新出发
                if (!_valA.Running) _valA.Set(from);
                _valA.To(to);
            }
            else
            {
                _valA.Set(double.TryParse(value, out double v) ? v : 0);
            }
            _value = value;
        }

        if (sub is not null) _sub = sub;
        if (color is not null) SetColor(color.Value);
        SyncAccessible();
        Invalidate();
    }

    /// <summary>卡片是自绘的，读屏软件只能靠这两个字段知道上面写了什么。</summary>
    private void SyncAccessible()
    {
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = _unit.Length > 0 ? $"{_title} {_value} {_unit}" : $"{_title} {_value}";
        AccessibleDescription = _sub;
    }

    private static int Decimals(string s)
    {
        int dot = s.IndexOf('.');
        return dot < 0 ? 0 : s.Length - dot - 1;
    }

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float en = (float)_enterA.Value;         // 0 = 还没入场，1 = 到位
        if (en <= 0.004f) return;                // 一点没进来就只剩背景，别留个影子
        float hv = (float)_hoverA.Value;

        // 玻璃自己按 en 淡入；文字画不了半透明（TextRenderer 不认 alpha），
        // 只能往背景色里兑——入场那半程玻璃还很薄，兑背景色跟实际看到的几乎一样
        Color Fade(Color c) => Theme.Mix(Theme.Bg, c, en);

        // 入场从下面浮上来；鼠标停上去再抬 2 像素，像被托起来一点
        int dy = (int)Math.Round((1f - en) * 12f - hv * 2f);
        Color value = Fade(Painted);

        RectangleF box = Theme.Inner(this);
        box.Y += dy;
        Theme.Glass(g, box, 16f, 0.12f + 0.88f * hv, 1f, true,
            hv > 0.01f ? Painted : null, en);

        // 左上角一个小色点，跟数字同色，扫一眼就知道哪张卡在报警；悬停时点外面透出一圈光
        if (hv > 0.01f)
            using (var halo = new SolidBrush(Color.FromArgb((int)(56 * hv * en), Painted)))
                g.FillEllipse(halo, 12f, 12f + dy, 15f, 15f);
        using (var dot = new SolidBrush(value))
            g.FillEllipse(dot, 16f, 16f + dy, 7f, 7f);

        TextRenderer.DrawText(g, _title, Theme.FontSmall, new Point(30, 12 + dy), Fade(Theme.TextSub), Flags);

        var vf = Theme.FontBig;
        string shown = _valA.Running ? _valA.Value.ToString("F" + _decimals) : _value;
        Size vs = TextRenderer.MeasureText(g, shown, vf, Size.Empty, Flags);
        int vy = 34 + dy;
        TextRenderer.DrawText(g, shown, vf, new Point(15, vy), value, Flags);

        if (_unit.Length > 0)
        {
            Size us = TextRenderer.MeasureText(g, _unit, Theme.FontSmall, Size.Empty, Flags);
            TextRenderer.DrawText(g, _unit, Theme.FontSmall,
                new Point(15 + vs.Width + 4, vy + vs.Height - us.Height - 3), Fade(Theme.TextSub), Flags);
        }

        if (_sub.Length > 0)
            TextRenderer.DrawText(g, _sub, Theme.FontSmall,
                new Rectangle(15, Height - 27 + dy, Math.Max(10, Width - 26), 18), Fade(Theme.TextDim),
                Flags | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }
}
