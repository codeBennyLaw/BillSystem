using System.Drawing.Drawing2D;
using BillSystem.Services;

namespace BillSystem.UI;

/// <summary>
/// 画二维码的小控件：底下一块玻璃，码本身整块留白 + 黑格都按整数像素画，
/// 缩放时不会出现半格灰边——手机扫码对这个挺敏感，抗锯齿反而扫不出来。
/// </summary>
internal sealed class QrView : Control
{
    private QrCode? _qr;
    private string _empty = "";

    public QrView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    /// <summary>要显示的内容；<c>null</c> 就显示 <see cref="EmptyText"/>。</summary>
    public string? Payload
    {
        set
        {
            try
            {
                // 学校网页用的也是 H 档，容错高一点，屏幕反光时好扫些
                _qr = string.IsNullOrEmpty(value) ? null : QrCode.Encode(value!, QrEcc.High);
            }
            catch (ArgumentException)
            {
                _qr = null;
                _empty = "二维码内容异常";
            }
            Invalidate();
        }
    }

    public string EmptyText
    {
        get => _empty;
        set { _empty = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.Glass(g, Theme.Inner(this), 18f, 0.06f);
        g.SmoothingMode = SmoothingMode.None;   // 码要一格一格对齐像素

        if (_qr is null)
        {
            TextRenderer.DrawText(g, _empty, Theme.FontBase, ClientRectangle, Theme.TextSub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        int n = _qr.Size;
        const int quiet = 2;                       // 四周留白，少于 2 格有些相机就认不出来
        int total = n + quiet * 2;
        // 少留 20 像素给玻璃：白块贴着卡边的话，看不出下面还有一层卡
        int avail = Math.Max(total, Math.Min(Width, Height) - 20);
        int scale = Math.Max(1, avail / total);
        int side = scale * total;
        int ox = (Width - side) / 2, oy = (Height - side) / 2;

        // 白底得连留白一起铺，不然深色面板贴着黑格，边界那一圈就丢了
        g.FillRectangle(Brushes.White, ox, oy, side, side);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (_qr.Modules[y, x])
                    g.FillRectangle(Brushes.Black,
                        ox + (x + quiet) * scale, oy + (y + quiet) * scale, scale, scale);
    }
}
