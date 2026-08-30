using System.Drawing.Drawing2D;
using BillSystem.Models;

namespace BillSystem.UI;

/// <summary>
/// 充值记录列表。条目不多（一年一百来条），自己画一整块 + 手动滚动就够了，
/// 用 ListView 反而要跟深色主题打一架。
/// </summary>
internal sealed class RecordList : Control
{
    private const int RowH = 34;
    private const int HeadH = 26;

    private List<RechargeRecord> _items = new();
    private double _scroll;     // 已经滚过去多少像素（目标值）
    private int _hover = -1;

    private readonly Anim _hoverA, _scrollA;

    public RecordList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _hoverA = new Anim(this, 0, 120);
        _scrollA = new Anim(this, 0, 180);
        BackColor = Theme.Bg;
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "充值记录";
    }

    public void SetItems(List<RechargeRecord> items)
    {
        _items = items;
        _scroll = 0;
        _scrollA.Set(0);   // 换了一整份内容，滑一下反而怪
        _hover = -1;
        Invalidate();
    }

    private int ContentH => _items.Count * RowH;

    private int MaxScroll => Math.Max(0, ContentH - (Height - HeadH - 2));

    /// <summary>画出来的位置（动画中的中间值），命中判定也用它，跟眼睛看到的对得上。</summary>
    private int Shown => (int)Math.Round(_scrollA.Value);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (MaxScroll <= 0) return;
        double before = _scroll;
        _scroll = Math.Clamp(_scroll - e.Delta / 120.0 * RowH * 2, 0, MaxScroll);
        if (Math.Abs(_scroll - before) > 0.01) _scrollA.To(_scroll);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = e.Y < HeadH ? -1 : (e.Y - HeadH + Shown) / RowH;
        if (h >= _items.Count) h = -1;
        if (h == _hover) return;
        _hover = h;
        _hoverA.Set(0);
        if (h >= 0) _hoverA.To(1); else Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == -1) return;
        _hover = -1;
        _hoverA.To(0);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        _scrollA.Set(_scroll);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        RectangleF card = Theme.Inner(this);
        Theme.Glass(g, card, 14f, 0.05f);

        // 四列：时间 / 金额 / 方式 / 状态
        int x0 = 14, x3 = Width - 14;
        int cAmount = x0 + Math.Max(120, (int)((x3 - x0) * 0.42));
        int cMethod = cAmount + 86;
        int cState = Math.Max(cMethod + 70, x3 - 66);

        Head(g, "时间", x0, cAmount - x0);
        Head(g, "金额", cAmount, cMethod - cAmount, true);
        Head(g, "方式", cMethod + 10, cState - cMethod - 10);
        Head(g, "状态", cState, x3 - cState);

        using (var line = new Pen(Color.FromArgb(30, 255, 255, 255)))
            g.DrawLine(line, x0, HeadH, x3, HeadH);

        if (_items.Count == 0)
        {
            TextRenderer.DrawText(g, "还没有充值记录", Font,
                new Rectangle(0, HeadH, Width, Height - HeadH), Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        g.SetClip(new Rectangle((int)card.X + 1, HeadH + 1,
            Math.Max(1, (int)card.Width - 2), Math.Max(1, Height - HeadH - 2 - (int)Theme.Bleed)));

        int shown = Shown;
        int first = Math.Max(0, shown / RowH);
        int last = Math.Min(_items.Count - 1, (shown + Height - HeadH) / RowH);
        for (int i = first; i <= last; i++)
        {
            RechargeRecord r = _items[i];
            int top = HeadH + i * RowH - shown;
            var row = new Rectangle(x0 - 6, top, x3 - x0 + 12, RowH);

            if (i == _hover && _hoverA.Value > 0.01)
            {
                float hv = (float)_hoverA.Value;
                // 卡片是透的，指到的那行只加一层白，不铺实色
                using (GraphicsPath hp = Theme.RoundedRect(row, 8f))
                using (var hb = new SolidBrush(Color.FromArgb((int)(30 * hv), 255, 255, 255)))
                    g.FillPath(hb, hp);

                // 左边一小条主色，指着的是哪一行更清楚
                using var bar = new SolidBrush(Color.FromArgb((int)(220 * hv), Theme.Accent));
                using GraphicsPath bp = Theme.RoundedRect(
                    new RectangleF(row.X + 1f, row.Y + 7f, 2.5f, Math.Max(2f, RowH - 14f)), 1.25f);
                g.FillPath(bar, bp);
            }
            else if (i % 2 == 1)
            {
                using var zb = new SolidBrush(Color.FromArgb(11, 255, 255, 255));
                g.FillRectangle(zb, row);
            }

            Cell(g, r.PayTime.ToString("yyyy-MM-dd HH:mm"), x0, cAmount - x0, top, Theme.Text);
            Cell(g, $"{r.Yuan:0.##} 元", cAmount, cMethod - cAmount, top, Theme.Remain, true);
            Cell(g, r.MethodLabel, cMethod + 10, cState - cMethod - 10, top, Theme.TextSub);
            Cell(g, r.PayResult.Length == 0 ? "—" : r.PayResult, cState, x3 - cState, top, Theme.Good);
        }

        g.ResetClip();

        // 内容超出一屏才画那根细滚动条
        if (MaxScroll > 0)
        {
            int trackTop = HeadH + 4, trackH = Height - HeadH - 8;
            int thumbH = Math.Max(24, (int)((long)trackH * trackH / Math.Max(1, ContentH)));
            int thumbY = trackTop + (int)((trackH - thumbH) * (Shown / (double)MaxScroll));
            using var sb = new SolidBrush(Color.FromArgb(64, 255, 255, 255));
            using GraphicsPath tp = Theme.RoundedRect(new RectangleF(Width - 9, thumbY, 3, thumbH), 1.5f);
            g.FillPath(sb, tp);
        }
    }

    private void Head(Graphics g, string text, int x, int w, bool right = false)
        => TextRenderer.DrawText(g, text, Theme.FontSmall, new Rectangle(x, 4, Math.Max(1, w), 18),
            Theme.TextDim,
            (right ? TextFormatFlags.Right : TextFormatFlags.Left)
            | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

    private void Cell(Graphics g, string text, int x, int w, int top, Color color, bool right = false)
        => TextRenderer.DrawText(g, text, Font, new Rectangle(x, top, Math.Max(1, w), RowH), color,
            (right ? TextFormatFlags.Right : TextFormatFlags.Left)
            | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
}
