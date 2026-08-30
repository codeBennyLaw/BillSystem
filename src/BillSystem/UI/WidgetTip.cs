using System.Drawing.Drawing2D;
using BillSystem.Interop;

namespace BillSystem.UI;

/// <summary>
/// 任务栏组件的悬停信息卡。
///
/// 不用 <see cref="ToolTip"/>：组件本身是置顶窗口，系统气泡会被它压在下面；而且组件是
/// <c>WS_EX_NOACTIVATE</c> 的工具窗口，标准气泡的触发时机很不稳，经常鼠标放上去没反应。
/// 这里自己画一个同样置顶、鼠标穿透（<c>WS_EX_TRANSPARENT</c>）的小窗，贴在组件外侧显示。
/// </summary>
internal sealed class WidgetTip : Form
{
    /// <summary>一行：左边灰色标签，右边彩色数值。标签为空表示整行当一句话画。</summary>
    internal readonly record struct Row(string Label, string Value, Color Color);

    private string _title = "";
    private List<Row> _rows = new();
    private Size _regionSize = Size.Empty;
    private readonly Anim _fade;

    private const int PadX = 12;
    private const int PadY = 10;
    private const int Gap = 22;   // 标签和数值之间至少留这么宽
    private const int LineH = 20;
    private const int Radius = 12;

    public WidgetTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(1, 1);
        TopMost = true;
        BackColor = Theme.Bg;
        Font = Theme.FontBase;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        // 淡入，别让卡片"啪"地一下砸出来；收的时候是直接消失，慢慢淡出会赖在刚打开的窗口上面
        Opacity = 0;
        _fade = new Anim(this, 0, 130, v => Opacity = Math.Clamp(v, 0, 1));
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void SetContent(string title, List<Row> rows)
    {
        _title = title;
        _rows = rows;
        Invalidate();
    }

    private const TextFormatFlags Flags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

    private static int W(string s, Font f) => TextRenderer.MeasureText(s, f, Size.Empty, Flags).Width;

    private Size Measure()
    {
        int w = W(_title, Theme.FontBold);
        foreach (Row r in _rows)
            w = Math.Max(w, r.Label.Length == 0
                ? W(r.Value, Theme.FontBase)
                : W(r.Label, Theme.FontSmall) + Gap + W(r.Value, Theme.FontBase));

        int h = PadY * 2 + LineH + 8 + _rows.Count * LineH;
        return new Size(w + PadX * 2, h);
    }

    /// <summary>贴在组件外侧（任务栏在下面就显示在上面，反之显示在下面），并夹在屏幕内。</summary>
    public void ShowNear(Rectangle anchor)
    {
        Size sz = Measure();
        Rectangle screen = Screen.FromRectangle(anchor).Bounds;
        Rectangle work = Screen.FromRectangle(anchor).WorkingArea;

        int y = anchor.Top - sz.Height - 8;
        if (y < screen.Top + 4) y = anchor.Bottom + 8;                 // 任务栏在顶上
        y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - sz.Height - 4));

        int x = anchor.Left;
        if (x + sz.Width > work.Right - 8) x = work.Right - 8 - sz.Width;
        x = Math.Max(work.Left + 8, x);

        Bounds = new Rectangle(x, y, sz.Width, sz.Height);
        if (sz != _regionSize)
        {
            _regionSize = sz;
            Region? old = Region;
            using (var path = Theme.RoundedRect(new RectangleF(0, 0, sz.Width, sz.Height), Radius))
                Region = new Region(path);
            old?.Dispose();
        }

        if (!Visible)
        {
            _fade.Set(0);
            Show();
        }

        Raise();
        Invalidate();
        _fade.To(1);
    }

    /// <summary>组件自己也是置顶的，顶完自己得把这张卡再顶上去，否则又被盖住。</summary>
    public void Raise()
    {
        if (!IsHandleCreated) return;
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public void HideTip()
    {
        if (!Visible) return;
        Hide();
        _fade.Set(0);
    }

    /// <summary>开发出图用：按内容定好尺寸，留在原地，不去贴组件也不上屏。</summary>
    internal void DevFreeze()
    {
        Size sz = Measure();
        _regionSize = sz;
        _fade.Set(1);
        SetBounds(Left, Top, sz.Width, sz.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 窗口被 Region 剪成圆角了，玻璃的外投影会被剪掉，所以这里不画影子，改成厚一点的玻璃
        var box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.Glass(g, box, Radius, 0.3f, 1.2f, false);

        int y = PadY;
        int innerW = Width - PadX * 2;

        TextRenderer.DrawText(g, _title, Theme.FontBold,
            new Rectangle(PadX, y, innerW, LineH), Theme.Text, Flags);
        y += LineH + 3;

        using (var line = new Pen(Color.FromArgb(34, 255, 255, 255)))
            g.DrawLine(line, PadX, y + 0.5f, Width - PadX, y + 0.5f);
        y += 5;

        foreach (Row r in _rows)
        {
            if (r.Label.Length == 0)
            {
                TextRenderer.DrawText(g, r.Value, Theme.FontSmall,
                    new Rectangle(PadX, y, innerW, LineH), r.Color, Flags);
            }
            else
            {
                TextRenderer.DrawText(g, r.Label, Theme.FontSmall,
                    new Rectangle(PadX, y, innerW, LineH), Theme.TextSub, Flags);
                TextRenderer.DrawText(g, r.Value, Theme.FontBase,
                    new Rectangle(PadX, y, innerW, LineH), r.Color, Flags | TextFormatFlags.Right);
            }
            y += LineH;
        }
    }
}
