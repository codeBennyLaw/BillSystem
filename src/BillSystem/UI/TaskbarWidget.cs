using BillSystem.Interop;
using BillSystem.Models;
using BillSystem.Services;

namespace BillSystem.UI;

/// <summary>
/// 任务栏最左侧那一条：紧凑的两行读数（剩余电量 / 抄表时间，可选今日 / 日均），
/// 没有边框和背景块，底色直接取任务栏的颜色，看着像任务栏自带的一部分。
///
/// 实现上是一个独立的置顶小窗，但会把自己认到 <c>Shell_TrayWnd</c> 名下（owner）：
/// 系统保证窗口画在自己 owner 的上面，所以切换应用时任务栏被抬到最前，它是跟着一起
/// 上来的——不需要再去抢 Z 序，也就没有"消失一下又出现"的闪烁。位置跟着任务栏窗口的
/// 真实矩形走，任务栏自动隐藏或前台是全屏窗口时，自己挪到屏幕外让开。
/// </summary>
internal sealed class TaskbarWidget : Form
{
    private readonly System.Windows.Forms.Timer _keeper = new() { Interval = 250 };
    private readonly WidgetTip _tip = new();

    private AppConfig _cfg;
    private IntPtr _taskbar = IntPtr.Zero;
    private bool _owned;   // 已经认到任务栏名下（认不上就退回定时置顶）
    private bool _away;    // 正让开：任务栏自动隐藏了，或者前台是全屏窗口
    private bool _frozen;
    private int _desiredWidth = 150;
    private int _lastHeight;
    private Font? _fontValue, _fontLabel;

    private double? _remaining, _today, _avgDaily;
    private DateTime? _meterTime;
    private string? _error;
    private bool _busy;

    private string _tipTitle = "";
    private List<WidgetTip.Row> _tipRows = new();
    private DateTime? _hoverSince;
    private bool _tipMuted;

    private Win32.WinEventProc? _fgProc;
    private IntPtr _fgHook;

    private Color _bg = Color.FromArgb(0x20, 0x20, 0x20);
    private DateTime _bgChecked = DateTime.MinValue;

    public event Action? LeftClicked;
    public ContextMenuStrip? WidgetMenu { get; set; }

    public TaskbarWidget(AppConfig cfg)
    {
        _cfg = cfg;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        MinimumSize = new Size(1, 1);
        Text = "宿舍电费";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _bg = FallbackBack();
        _keeper.Tick += (_, _) => Keep();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void Attach()
    {
        TopMost = true;
        if (!Visible) Show();
        Adopt();
        Reposition();
        Raise();
        RefreshBackColor();
        HookForeground();
        _keeper.Start();
    }

    public void Detach()
    {
        _keeper.Stop();
        UnhookForeground();
        _tip.HideTip();
        Release();
        Hide();
    }

    /// <summary>
    /// 认到任务栏名下。认上以后 Z 序就是系统在管了：任务栏被抬到最前，这个窗口跟着一起上来，
    /// 中间没有"被盖住又被顶回来"的那一帧。认不上（Win11 任务栏改版频繁）就退回原来的做法——
    /// 听前台切换事件自己顶回去，会有一点闪，但至少不会被埋掉。
    /// </summary>
    private void Adopt()
    {
        if (_frozen || !IsHandleCreated) return;

        _taskbar = Win32.FindTaskbar();
        _owned = _taskbar != IntPtr.Zero && Win32.TrySetOwner(Handle, _taskbar);
    }

    private void Release()
    {
        if (!_owned || !IsHandleCreated) return;
        Win32.TrySetOwner(Handle, IntPtr.Zero);
        _owned = false;
    }

    /// <summary>
    /// 切前台窗口的当下要办两件事：全屏程序上来了就赶紧让开；万一没认到任务栏名下，
    /// 还得像以前那样立刻把自己顶回去（等定时器那一拍就已经看得出闪了）。
    /// </summary>
    private void HookForeground()
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgProc = (_, ev, _, _, _, _, _) =>
        {
            if (ev != Win32.EVENT_SYSTEM_FOREGROUND) return;
            Reposition();
            if (!_owned) Raise();
        };
        _fgHook = Win32.HookForeground(_fgProc);
    }

    private void UnhookForeground()
    {
        Win32.Unhook(_fgHook);
        _fgHook = IntPtr.Zero;
        _fgProc = null;
    }

    public void ApplyConfig(AppConfig cfg)
    {
        _cfg = cfg;
        Measure();
        Reposition();
        Raise();
        Invalidate();
    }

    /// <summary>
    /// 开发出图用（--screenshot）：按给定高度定尺寸、留在原地渲染，不去贴任务栏。
    /// </summary>
    internal void DevFreeze(int height)
    {
        _frozen = true;
        _lastHeight = height;
        BuildFonts(height);
        Measure();
        SetBounds(Left, Top, _desiredWidth, height);
    }

    /// <summary>开发出图用：把组件自己那张悬停卡拿去画，免得出图时另抄一份内容。</summary>
    internal WidgetTip DevTip() => _tip;

    /// <summary>每 0.25 秒查一次：explorer 重启过就重新认一次，位置被挤走就摆回去，顺手对底色、管悬停卡。</summary>
    private void Keep()
    {
        // owner 没了自己也会被销毁（explorer 重启），WinForms 这边只会看到句柄不见了
        if (!_frozen && !IsHandleCreated)
        {
            _owned = false;
            Hide();
            Show();
            TopMost = true;
        }

        if (!_frozen && (!_owned || _taskbar != Win32.FindTaskbar() || Win32.GetOwner(Handle) != _taskbar))
            Adopt();

        Reposition();
        Raise();
        SyncTip();
        RefreshBackColor();
    }

    /// <summary>
    /// 认不到任务栏名下时的退路：只改 Z 序、不动位置大小——反复重设位置会把悬停卡和系统气泡挤掉。
    /// </summary>
    private void Raise()
    {
        if (_owned || _away || _frozen || !Visible || !IsHandleCreated) return;

        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        if (_tip.Visible) _tip.Raise();
    }

    /// <summary>
    /// 前台是不是占满整个屏幕的窗口（全屏游戏、全屏播放器）。任务栏这时候会自己躲开，
    /// 组件是置顶窗口不会，得自己跟着躲。
    /// </summary>
    private static bool FullscreenAhead()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || !Win32.GetWindowRect(fg, out RECT r)) return false;

        // 桌面和任务栏本身也"占满屏幕"，别把它们当全屏程序
        switch (Win32.ClassNameOf(fg))
        {
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
            case "Progman":
            case "WorkerW":
                return false;
        }

        var win = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (win.Width <= 0 || win.Height <= 0) return false;
        return win.Contains(Screen.FromRectangle(win).Bounds);
    }

    private void Reposition()
    {
        if (_frozen) return;

        if (!Win32.TryGetTaskbar(out IntPtr bar, out RECT live, out RECT docked, out TaskbarEdge edge))
            return;

        _taskbar = bar;
        bool vertical = edge is TaskbarEdge.Left or TaskbarEdge.Right;

        int barW = live.Width, barH = live.Height;
        if (barW <= 0 || barH <= 0) return;

        // 高度按停靠尺寸算，这样自动隐藏缩成一条的时候字号不会跟着跳
        int dockH = vertical ? docked.Width : docked.Height;
        int refH = Math.Max(vertical ? barW : barH, dockH);
        int h = vertical
            ? Math.Clamp(refH >= 96 ? 40 : refH - 8, 18, 56)
            : Math.Clamp(refH - 4, 18, 64);

        if (h != _lastHeight)
        {
            _lastHeight = h;
            BuildFonts(h);
            Measure();
        }

        int w = Math.Min(_desiredWidth, Math.Max(60, (vertical ? Math.Max(barW, docked.Width) : barW) - 8));

        int x, y;
        if (vertical)
        {
            // 竖着的任务栏就放最上面居中
            x = live.Left + Math.Max(0, (barW - w) / 2);
            y = live.Top + _cfg.WidgetOffsetX;
        }
        else
        {
            x = live.Left + _cfg.WidgetOffsetX;
            y = live.Top + Math.Max(0, (barH - h) / 2);
        }

        // 任务栏缩成一条（自动隐藏）或者前台全屏了，就整个挪到屏幕外，等它回来再回来
        bool hidden = (vertical ? barW : barH) * 2 < dockH;
        _away = hidden || FullscreenAhead();
        if (_away)
        {
            (x, y) = edge switch
            {
                TaskbarEdge.Top => (x, live.Top - h - 2),
                TaskbarEdge.Left => (live.Left - w - 2, y),
                TaskbarEdge.Right => (live.Right + 2, y),
                _ => (x, live.Bottom + 2),
            };
        }

        // 真实位置已经对了就别动窗口：白动一次 SetWindowPos 就是闪一下，还会打断鼠标悬停
        var want = new Rectangle(x, y, w, h);
        if (want == ScreenRect()) return;

        Win32.SetWindowPos(Handle, _away ? Win32.HWND_TOP : Win32.HWND_TOPMOST,
            x, y, w, h, Win32.SWP_NOACTIVATE);
    }

    /// <summary>刷新显示内容。</summary>
    public void UpdateData(PollStatus status, Summary? summary)
    {
        Reading? r = status.Latest;
        _remaining = r?.Remaining;
        _meterTime = r?.MeterTime;
        _today = summary is { UsageKnown: true } ? summary.Today : null;
        _avgDaily = summary?.AvgDaily;
        _error = status.Error;
        _busy = status.Busy;

        _tipTitle = $"{_cfg.Building} 栋 {_cfg.Room} 房间";
        _tipRows = new List<WidgetTip.Row>
        {
            new("剩余电量", _remaining is { } rem ? $"{rem:0.00} 度" : "--",
                Theme.LevelColor(_remaining, _cfg.LowThreshold)),
            new("抄表时间", _meterTime is { } mt ? mt.ToString("MM-dd HH:mm") : "--", Theme.Text),
        };
        if (r is not null) _tipRows.Add(new("累计用电", $"{r.Used:0.00} 度", Theme.Text));
        if (_today is { } td) _tipRows.Add(new("今日用电", $"{td:0.00} 度", Theme.Accent));
        if (_avgDaily is { } avg) _tipRows.Add(new("日均用电", $"{avg:0.00} 度", Theme.Accent));
        if (summary?.DaysLeftText is { } left)
            _tipRows.Add(new("预计可用", summary.RunOutDate is { } end
                ? $"约 {left} · {end:MM-dd HH:mm}"
                : $"约 {left}", Theme.Text));
        if (_error is not null) _tipRows.Add(new("", $"更新失败：{_error}", Theme.Bad));

        _tip.SetContent(_tipTitle, _tipRows);
        if (_tip.Visible && IsHandleCreated) _tip.ShowNear(ScreenRect());

        Measure();
        Reposition();
        Invalidate();
    }

    /// <summary>
    /// 悬停卡的显示时机自己盯：组件是不激活的工具窗口，靠 MouseEnter/MouseLeave 经常收不到事件，
    /// 直接每一拍看鼠标在不在自己身上最可靠。
    /// </summary>
    private void SyncTip()
    {
        if (_frozen || _away || !Visible || !IsHandleCreated) return;

        Rectangle rc = ScreenRect();
        bool over = rc.Width > 0 && rc.Contains(Cursor.Position);

        if (!over)
        {
            _hoverSince = null;
            _tipMuted = false;
            _tip.HideTip();
            return;
        }

        if (_tipMuted) return;

        _hoverSince ??= DateTime.UtcNow;
        if (!_tip.Visible && (DateTime.UtcNow - _hoverSince.Value).TotalMilliseconds >= 260)
        {
            _tip.SetContent(_tipTitle, _tipRows);
            _tip.ShowNear(rc);
        }
    }

    private Rectangle ScreenRect() =>
        Win32.GetWindowRect(Handle, out RECT r)
            ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : Rectangle.Empty;

    private void BuildFonts(int h)
    {
        _fontValue?.Dispose();
        _fontLabel?.Dispose();

        bool two = h >= 30;
        float vp = two ? Math.Max(9f, h * 0.30f) : Math.Max(9f, h * 0.44f);
        float lp = two ? Math.Max(8f, h * 0.27f) : Math.Max(8f, h * 0.40f);

        _fontValue = new Font(Theme.Family, vp, FontStyle.Bold, GraphicsUnit.Pixel);
        _fontLabel = new Font(Theme.Family, lp, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private const int PadX = 8;
    private const int ColGap = 16;
    private const int RowGap = 1;
    private const TextFormatFlags TextFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

    /// <summary>一格读数：灰色小标签 + 彩色数字 + 灰色单位。</summary>
    private readonly record struct Cell(string Label, string Value, string Unit, Color Color);

    private bool DarkBg => _bg.GetBrightness() < 0.5f;
    private Color MainColor => DarkBg ? Color.FromArgb(0xEC, 0xEF, 0xF4) : Color.FromArgb(0x1B, 0x1E, 0x25);
    private Color SubColor => DarkBg ? Color.FromArgb(0x9B, 0xA3, 0xB2) : Color.FromArgb(0x5C, 0x62, 0x6C);
    private Color AccentColor => DarkBg ? Theme.Accent : Color.FromArgb(0x18, 0x5A, 0xC8);

    private Color RemainColor
    {
        get
        {
            if (_remaining is null || _error is not null) return SubColor;
            Color c = Theme.LevelColor(_remaining, _cfg.LowThreshold);
            return DarkBg ? c : Theme.Mix(c, Color.Black, 0.3f); // 浅色任务栏上要压暗一点才看得清
        }
    }

    private List<Cell> Column1() => new()
    {
        new("剩余", _remaining is { } r ? r.ToString("0.00") : "--", "度", RemainColor),
        new("抄表", _meterTime is { } t ? t.ToString("MM-dd HH:mm") : "--", "", MainColor),
    };

    private List<Cell>? Column2()
    {
        if (!_cfg.WidgetShowExtra || (_today is null && _avgDaily is null)) return null;
        return new List<Cell>
        {
            new("今日", _today is { } t ? t.ToString("0.00") : "--", "度", AccentColor),
            new("日均", _avgDaily is { } a ? a.ToString("0.00") : "--", "度", AccentColor),
        };
    }

    private string OneLineText =>
        _meterTime is { } t
            ? $"{(_remaining is { } r ? r.ToString("0.00") : "--")}度 {t:MM-dd HH:mm}"
            : $"{(_remaining is { } r2 ? r2.ToString("0.00") : "--")}度";

    private int MeasureV(string s) => TextRenderer.MeasureText(s, _fontValue!, Size.Empty, TextFlags).Width;
    private int MeasureL(string s) => TextRenderer.MeasureText(s, _fontLabel!, Size.Empty, TextFlags).Width;

    private int CellWidth(Cell c) =>
        MeasureL(c.Label) + 5 + MeasureV(c.Value) + (c.Unit.Length > 0 ? 2 + MeasureL(c.Unit) : 0);

    private void Measure()
    {
        int h = _lastHeight > 0 ? _lastHeight : 40;
        if (_fontValue is null || _fontLabel is null) BuildFonts(h);

        if (h >= 30)
        {
            int w = PadX * 2 + Column1().Max(CellWidth);
            if (Column2() is { } c2) w += ColGap + c2.Max(CellWidth);
            _desiredWidth = w + 4; // 左边那条竖色块
        }
        else
        {
            _desiredWidth = PadX * 2 + MeasureV(OneLineText) + 4;
        }
    }

    private static Color FallbackBack() =>
        Theme.SystemUsesLightTheme() ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x20, 0x20, 0x20);

    /// <summary>
    /// 取任务栏自己的底色：在组件右边空白处采两个点，两点一样就认为那是背景色。
    /// 采不到（或那儿有图标）就退回按系统主题猜一个。
    /// </summary>
    private void RefreshBackColor()
    {
        if ((DateTime.UtcNow - _bgChecked).TotalSeconds < 2) return;
        _bgChecked = DateTime.UtcNow;

        Color bg = FallbackBack();
        Rectangle me = ScreenRect();
        if (!_away && me.Width > 0
            && Win32.IsWindow(_taskbar)
            && Win32.GetWindowRect(_taskbar, out RECT tb)
            && Win32.GetClientRect(_taskbar, out RECT c))
        {
            // GetPixel 要的是任务栏客户区坐标，组件这边是屏幕坐标，减掉任务栏左上角就行
            int y = Math.Clamp(me.Top + me.Height / 2 - tb.Top, 0, Math.Max(0, c.Height - 1));
            int x1 = me.Right - tb.Left + 20, x2 = x1 + 24;
            if (x1 >= 0 && x2 < c.Width - 2
                && Win32.SampleColor(_taskbar, x1, y) is { } s1
                && Win32.SampleColor(_taskbar, x2, y) is { } s2
                && s1.ToArgb() == s2.ToArgb()
                // 纯黑基本都是"这块没画在这个 DC 上"，不是真的任务栏颜色
                && (s1.R | s1.G | s1.B) != 0)
            {
                bg = s1;
            }
        }

        if (bg.ToArgb() == _bg.ToArgb()) return;
        _bg = bg;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(_bg);

        if (_fontValue is null || _fontLabel is null) BuildFonts(Math.Max(18, Height));

        // 最左边一条细色块，电量见底时就是红的，扫一眼就知道
        using (var bar = new SolidBrush(RemainColor))
        using (var path = Theme.RoundedRect(new RectangleF(1, Height * 0.22f, 2.5f, Height * 0.56f), 1.25f))
            g.FillPath(bar, path);

        int x = PadX;
        if (Height >= 30)
        {
            int lineH = Math.Max(
                TextRenderer.MeasureText("0度", _fontValue!, Size.Empty, TextFlags).Height,
                TextRenderer.MeasureText("剩", _fontLabel!, Size.Empty, TextFlags).Height);
            int top = Math.Max(0, (Height - (lineH * 2 + RowGap)) / 2);

            List<Cell> c1 = Column1();
            int w1 = c1.Max(CellWidth);
            DrawCell(g, c1[0], x, top, lineH);
            DrawCell(g, c1[1], x, top + lineH + RowGap, lineH);

            if (Column2() is { } c2)
            {
                x += w1 + ColGap;
                DrawCell(g, c2[0], x, top, lineH);
                DrawCell(g, c2[1], x, top + lineH + RowGap, lineH);
            }
        }
        else
        {
            TextRenderer.DrawText(g, OneLineText, _fontValue!,
                new Rectangle(x, 0, Math.Max(10, Width - x), Height), RemainColor, TextFlags);
        }

        if (_error is not null)
            using (var dot = new SolidBrush(Theme.Bad))
                g.FillEllipse(dot, Width - 7, 3, 4, 4);
        else if (_busy)
            using (var dot = new SolidBrush(AccentColor))
                g.FillEllipse(dot, Width - 7, 3, 4, 4);
    }

    private void DrawCell(Graphics g, Cell c, int x, int top, int lineH)
    {
        int lw = MeasureL(c.Label);
        TextRenderer.DrawText(g, c.Label, _fontLabel!, new Rectangle(x, top, lw + 2, lineH), SubColor, TextFlags);

        int vx = x + lw + 5;
        int vw = MeasureV(c.Value);
        TextRenderer.DrawText(g, c.Value, _fontValue!, new Rectangle(vx, top, vw + 2, lineH), c.Color, TextFlags);

        if (c.Unit.Length > 0)
            TextRenderer.DrawText(g, c.Unit, _fontLabel!,
                new Rectangle(vx + vw + 2, top, MeasureL(c.Unit) + 2, lineH), SubColor, TextFlags);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // 点了就把卡收掉，不然它会一直浮在刚打开的主界面上面
        _tip.HideTip();
        _tipMuted = true;
        _hoverSince = null;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
            LeftClicked?.Invoke();
        else if (e.Button == MouseButtons.Right && WidgetMenu is not null)
            WidgetMenu.Show(this, e.Location);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Cursor = Cursors.Hand;
        _hoverSince ??= DateTime.UtcNow;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverSince = null;
        _tipMuted = false;
        _tip.HideTip();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _keeper.Stop();
            _keeper.Dispose();
            UnhookForeground();
            _tip.Dispose();
            _fontValue?.Dispose();
            _fontLabel?.Dispose();
        }
        base.Dispose(disposing);
    }
}
