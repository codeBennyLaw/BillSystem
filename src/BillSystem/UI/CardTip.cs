using System.Drawing.Drawing2D;

namespace BillSystem.UI;

/// <summary>
/// 数字卡片的悬停小窗：把卡片上那几个度数按电价折成钱。
///
/// 做成主窗口里的子控件，而不是像 <see cref="WidgetTip"/> 那样单开一个置顶窗口——那张是没办法，
/// 任务栏组件本身就置顶，普通窗口压不住它；主窗口里用子控件就够了，也不会切走程序之后还赖在屏幕上。
/// 自己不认鼠标（<see cref="Control.Enabled"/> 关着），要显示什么、什么时候收，都由主窗口说。
/// </summary>
internal sealed class CardTip : Control
{
    /// <summary>一行：左边灰色标签，右边数值。</summary>
    internal readonly record struct Row(string Label, string Value);

    private string _title = "";
    private List<Row> _rows = new();
    private readonly Anim _fade;

    private const int PadX = 12;
    private const int PadY = 9;
    private const int Gap = 20;   // 标签和数值之间至少留这么宽
    private const int LineH = 19;
    private const int Radius = 12;

    public CardTip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Enabled = false;
        Visible = false;
        BackColor = Theme.Bg;
        Font = Theme.FontBase;
        // 淡完再真正藏起来，不然移开鼠标那一下是"啪"地消失
        _fade = new Anim(this, 0, 140, v =>
        {
            if (v <= 0.001) Visible = false;
        });
    }

    private const TextFormatFlags Flags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

    private static int W(string s, Font f) => TextRenderer.MeasureText(s, f, Size.Empty, Flags).Width;

    private Size Measure()
    {
        int w = W(_title, Theme.FontBold);
        foreach (Row r in _rows)
            w = Math.Max(w, W(r.Label, Theme.FontSmall) + Gap + W(r.Value, Theme.FontBase));
        return new Size(w + PadX * 2, PadY * 2 + LineH + 8 + _rows.Count * LineH);
    }

    /// <summary>
    /// 贴在卡片下面，位置夹在父窗口里（最右边那张卡不能把小窗顶出窗外）。
    /// 底下摆不开就翻到卡片上面去。
    /// </summary>
    public void ShowFor(Control anchor, string title, List<Row> rows)
    {
        if (rows.Count == 0) { HideTip(); return; }
        if (Parent is not { } host) return;

        _title = title;
        _rows = rows;

        Size sz = Measure();
        int x = Math.Clamp(anchor.Left + 6, 6, Math.Max(6, host.ClientSize.Width - sz.Width - 6));
        int y = anchor.Bottom + 6;
        if (y + sz.Height > host.ClientSize.Height - 6) y = Math.Max(6, anchor.Top - sz.Height - 6);

        SetBounds(x, y, sz.Width, sz.Height);
        if (!Visible)
        {
            _fade.Set(0);
            Visible = true;
        }
        BringToFront();
        Invalidate();
        _fade.To(1);
    }

    public void HideTip()
    {
        if (Visible) _fade.To(0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float f = (float)Math.Clamp(_fade.Value, 0, 1);
        if (f <= 0.004f) return;

        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 文字画不了半透明（TextRenderer 不认 alpha），淡入只能往身下那块背景色里兑
        Color under = Theme.BackdropAt(this);
        Color Fade(Color c) => Theme.Mix(under, c, f);

        // 影子会被自己的边框剪掉（贴在别的控件上面，没法往外画），所以不画影子，改成厚一点的玻璃
        var box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.Glass(g, box, Radius, 0.34f, 1.25f, false, null, f);

        int y = PadY;
        int innerW = Width - PadX * 2;

        TextRenderer.DrawText(g, _title, Theme.FontBold,
            new Rectangle(PadX, y, innerW, LineH), Fade(Theme.Text), Flags);
        y += LineH + 3;

        using (var line = new Pen(Color.FromArgb((int)(34 * f), 255, 255, 255)))
            g.DrawLine(line, PadX, y + 0.5f, Width - PadX, y + 0.5f);
        y += 5;

        foreach (Row r in _rows)
        {
            TextRenderer.DrawText(g, r.Label, Theme.FontSmall,
                new Rectangle(PadX, y, innerW, LineH), Fade(Theme.TextSub), Flags);
            TextRenderer.DrawText(g, r.Value, Theme.FontBase,
                new Rectangle(PadX, y, innerW, LineH), Fade(Theme.Remain), Flags | TextFormatFlags.Right);
            y += LineH;
        }
    }
}
