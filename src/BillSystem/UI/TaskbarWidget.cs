using System.Drawing.Imaging;
using BillSystem.Interop;
using BillSystem.Models;
using BillSystem.Services;

namespace BillSystem.UI;

/// <summary>
/// 任务栏最左侧那一条：紧凑的两行读数（剩余电量 / 抄表时间，可选今日 / 日均）。
/// <b>只有字，没有底</b>——窗口是分层窗口（<c>WS_EX_LAYERED</c>），画面存在 DWM 那边，
/// 别的窗口在上面画都擦不掉，所以不会"闪一下没了、切个窗口才回来"。
/// 又把自己认到 <c>Shell_TrayWnd</c> 名下（owner），任务栏被抬到最前时它跟着一起上来；
/// 这条路在某台机器上不好使就退回自己定时抢 Z 序。
/// </summary>
internal sealed class TaskbarWidget : Form
{
    private readonly System.Windows.Forms.Timer _keeper = new() { Interval = 250 };
    private readonly WidgetTip _tip = new();

    private AppConfig _cfg;
    private Dorm? _dorm;
    private IntPtr _taskbar = IntPtr.Zero;
    private bool _owned;      // 已经认到任务栏名下（认不上就退回定时置顶）
    private bool _adoptOff;   // 认过了但压不住，别再认了
    private int _buried;      // 连续几拍发现自己被埋着
    private bool _away;       // 正让开：任务栏自动隐藏了，或者前台是全屏窗口
    private bool _frozen;
    private int _desiredWidth = 150;
    private int _lastHeight;
    private Font? _fontValue, _fontLabel;
    private Bitmap? _surface;

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

    private bool _light;
    private DateTime _lightChecked = DateTime.MinValue;

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
        _light = Theme.SystemUsesLightTheme();
        _keeper.Tick += (_, _) => Keep();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            // 出图（--screenshot）走 DrawToBitmap，那条路要的是普通窗口的重画
            if (!_frozen) cp.ExStyle |= Win32.WS_EX_LAYERED;
            return cp;
        }
    }

    public void Attach()
    {
        TopMost = true;
        if (!Visible) Show();
        _adoptOff = false;
        _buried = 0;
        Adopt();
        Reposition();
        Raise();
        Render();
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
    /// 认到任务栏名下，Z 序就交给系统管了。认不上（Win11 任务栏改版频繁）
    /// 就退回听前台切换事件自己顶回去。
    /// </summary>
    private void Adopt()
    {
        if (_frozen || _adoptOff || !IsHandleCreated) return;

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
    /// 切前台窗口的当下办两件事：全屏程序上来了就让开；新窗口压在自己身上就立刻顶回去
    /// （等定时器那一拍已经看得出闪了）。
    /// </summary>
    private void HookForeground()
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgProc = (_, ev, _, _, _, _, _) =>
        {
            if (ev != Win32.EVENT_SYSTEM_FOREGROUND) return;
            Reposition();
            Unbury();
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
        Render();
    }

    public void SetDorm(Dorm? dorm) => _dorm = dorm;

    /// <summary>开发出图用（--screenshot）：按给定高度定尺寸、留在原地渲染，不去贴任务栏。</summary>
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

    private void Keep()
    {
        // owner 没了自己也会被销毁（explorer 重启），WinForms 这边只会看到句柄不见了
        if (!_frozen && !IsHandleCreated)
        {
            _owned = false;
            _adoptOff = false;   // 换了个新任务栏，认一认说不定就成了
            Hide();
            Show();
            TopMost = true;
            Render();
        }

        if (!_frozen && (!_owned || _taskbar != Win32.FindTaskbar() || Win32.GetOwner(Handle) != _taskbar))
            Adopt();

        Reposition();
        Unbury();
        SyncTip();
        RefreshTheme();
    }

    /// <summary>
    /// 埋着就顶回去；连着两秒顶不动说明"认 owner"这条路在这台机器上不好使，松开退回定时抢 Z 序。
    /// </summary>
    private void Unbury()
    {
        if (_frozen || _away || !Visible || !IsHandleCreated) return;

        if (!Covered())
        {
            _buried = 0;
            if (!_owned) Raise();
            return;
        }

        _buried++;
        Raise(force: true);

        if (_buried >= 8 && _owned)
        {
            Release();
            _adoptOff = true;
        }
    }

    /// <summary>
    /// 正中那一点归不归自己：分层窗口按 alpha 判定命中，整块铺的那层 alpha=1 在这儿正好用得上。
    /// 自己的右键菜单盖住这一点不算被埋。
    /// </summary>
    private bool Covered()
    {
        if (WidgetMenu is { Visible: true }) return false;

        Rectangle rc = ScreenRect();
        if (rc.Width <= 0 || rc.Height <= 0) return false;

        IntPtr top = Win32.WindowAt(new Point(rc.Left + rc.Width / 2, rc.Top + rc.Height / 2));
        return top != IntPtr.Zero && top != Handle;
    }

    /// <summary>只改 Z 序、不动位置大小——反复重设位置会把悬停卡和系统气泡挤掉。</summary>
    private void Raise(bool force = false)
    {
        if (_away || _frozen || !Visible || !IsHandleCreated) return;
        if (_owned && !force) return;

        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        if (_tip.Visible) _tip.Raise();
    }

    /// <summary>前台是不是全屏窗口（全屏游戏、播放器）。任务栏这时会自己躲开，置顶的组件不会。</summary>
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

    public void UpdateData(PollStatus status, Summary? summary)
    {
        Reading? r = status.Latest;
        _remaining = r?.Remaining;
        _meterTime = r?.MeterTime;
        _today = summary is { UsageKnown: true } ? summary.Today : null;
        _avgDaily = summary?.AvgDaily;
        _error = status.Error;
        _busy = status.Busy;

        _tipTitle = _dorm?.Label ?? "宿舍电费助手";
        _tipRows = new List<WidgetTip.Row>
        {
            new("剩余电量", _remaining is { } rem ? $"{rem:0.00} 度" : "--",
                Theme.LevelColor(_remaining, LowThreshold)),
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
        Render();
    }

    /// <summary>
    /// 悬停卡的显示时机自己盯：组件是不激活的工具窗口，MouseEnter/MouseLeave 经常收不到。
    /// </summary>
    private void SyncTip()
    {
        if (_frozen) return;

        // 组件自己让开了（任务栏自动隐藏、前台是全屏窗口）或者被关掉了，
        // 那张卡得跟着收走——它是鼠标穿透的置顶窗口，留在屏幕上点都点不掉
        if (_away || !Visible || !IsHandleCreated)
        {
            _hoverSince = null;
            _tipMuted = false;
            if (_tip.Visible) _tip.HideTip();
            return;
        }

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

    /// <summary>
    /// 量字画字都走 GDI+ 这一套排版参数。<b>不能用 <c>TextRenderer</c></b>——那条路是 GDI 画的，
    /// 出来的像素 alpha 一律是 0，推给分层窗口就是一片透明。
    /// </summary>
    private static readonly StringFormat Fmt = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
                      | StringFormatFlags.MeasureTrailingSpaces,
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.None,
    };

    // 量字用的一张 1×1 画布：窗口位图还没建起来的时候（启动、重新定尺寸）也得量得出来
    private static readonly Bitmap RulerPad = new(1, 1);
    private static readonly Graphics Ruler = Graphics.FromImage(RulerPad);

    /// <summary>一格读数：灰色小标签 + 彩色数字 + 灰色单位。</summary>
    private readonly record struct Cell(string Label, string Value, string Unit, Color Color);

    private Color MainColor => _light ? Color.FromArgb(0x1B, 0x1E, 0x25) : Color.FromArgb(0xEC, 0xEF, 0xF4);
    private Color SubColor => _light ? Color.FromArgb(0x4B, 0x51, 0x5C) : Color.FromArgb(0x9B, 0xA3, 0xB2);
    private Color AccentColor => _light ? Color.FromArgb(0x18, 0x5A, 0xC8) : Theme.Accent;

    private Color HaloColor => _light ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0);

    private Color RemainColor
    {
        get
        {
            if (_remaining is null || _error is not null) return SubColor;
            Color c = Theme.LevelColor(_remaining, LowThreshold);
            return _light ? Theme.Mix(c, Color.Black, 0.3f) : c; // 浅色任务栏上要压暗一点才看得清
        }
    }

    private double LowThreshold => _dorm?.LowThreshold ?? 0;

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

    private static int Wide(string s, Font f) =>
        s.Length == 0 ? 0 : (int)Math.Ceiling(Ruler.MeasureString(s, f, PointF.Empty, Fmt).Width);

    private int MeasureV(string s) => Wide(s, _fontValue!);
    private int MeasureL(string s) => Wide(s, _fontLabel!);

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

    /// <summary>系统主题两秒对一次：字的深浅跟着它走。</summary>
    private void RefreshTheme()
    {
        if ((DateTime.UtcNow - _lightChecked).TotalSeconds < 2) return;
        _lightChecked = DateTime.UtcNow;

        bool light = Theme.SystemUsesLightTheme();
        if (light == _light) return;
        _light = light;
        Render();
    }

    /// <summary>
    /// 把这一帧整块推给系统。分层窗口的画面不靠 WM_PAINT，推一次就存在 DWM 那边；
    /// 内容、尺寸、主题变了才推一次。
    /// </summary>
    private void Render()
    {
        if (_frozen) { Invalidate(); return; }   // 出图那条路是普通窗口，走 OnPaint
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

        if (_surface is null || _surface.Width != Width || _surface.Height != Height)
        {
            _surface?.Dispose();
            _surface = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        }

        using (Graphics g = Graphics.FromImage(_surface))
        {
            // 整块铺一层 alpha=1：肉眼看不见（255 分之一的黑），但分层窗口是按 alpha 判定
            // 鼠标命中的，真全透明的话只有笔画上点得中，左键和右键菜单就等于没了
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            Draw(g, Width, Height);
        }

        Win32.PushLayered(Handle, _surface);
    }

    /// <summary>尺寸一变那张位图就得重做：尺寸对不上，系统直接不认。</summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Render();
    }

    /// <summary>只有出图（<see cref="DevFreeze"/>）才走到这里：那时是普通窗口，得自己铺个底。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_frozen) return;
        e.Graphics.Clear(_light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x20, 0x20, 0x20));
        Draw(e.Graphics, Width, Height);
    }

    private void Draw(Graphics g, int w, int h)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // ClearType 得有一块不透明的底才算得出来，这边只能用灰度反锯齿
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        if (_fontValue is null || _fontLabel is null) BuildFonts(Math.Max(18, h));

        // 最左边一条细色块：电量见底就是红的
        using (var bar = new SolidBrush(RemainColor))
        using (var path = Theme.RoundedRect(new RectangleF(1, h * 0.22f, 2.5f, h * 0.56f), 1.25f))
            g.FillPath(bar, path);

        float x = PadX;
        if (h >= 30)
        {
            int lineH = (int)Math.Ceiling(Math.Max(_fontValue!.GetHeight(g), _fontLabel!.GetHeight(g)));
            float top = Math.Max(0f, (h - (lineH * 2 + RowGap)) / 2f);

            List<Cell> c1 = Column1();
            DrawCell(g, c1[0], x, top, lineH);
            DrawCell(g, c1[1], x, top + lineH + RowGap, lineH);

            if (Column2() is { } c2)
            {
                x += c1.Max(CellWidth) + ColGap;
                DrawCell(g, c2[0], x, top, lineH);
                DrawCell(g, c2[1], x, top + lineH + RowGap, lineH);
            }
        }
        else
        {
            Glyph(g, OneLineText, _fontValue!, new RectangleF(x, 0, Math.Max(10f, w - x), h), RemainColor);
        }

        if (_error is not null || _busy)
            using (var dot = new SolidBrush(_error is not null ? Theme.Bad : AccentColor))
                g.FillEllipse(dot, w - 7, 3, 4, 4);
    }

    private void DrawCell(Graphics g, Cell c, float x, float top, int lineH)
    {
        int lw = MeasureL(c.Label);
        Glyph(g, c.Label, _fontLabel!, new RectangleF(x, top, lw + 2, lineH), SubColor);

        float vx = x + lw + 5;
        int vw = MeasureV(c.Value);
        Glyph(g, c.Value, _fontValue!, new RectangleF(vx, top, vw + 2, lineH), c.Color);

        if (c.Unit.Length > 0)
            Glyph(g, c.Unit, _fontLabel!,
                new RectangleF(vx + vw + 2, top, MeasureL(c.Unit) + 2, lineH), SubColor);
    }

    private static readonly PointF[] Halo = { new(-1, 0), new(1, 0), new(0, -1), new(0, 1) };

    /// <summary>
    /// 一段字：先在四周描一圈反色淡影，再把字压上去。底下是真透明的，任务栏什么色、
    /// 透出来什么壁纸都说不准，光靠字自己的颜色总有糊掉的时候。
    /// </summary>
    private void Glyph(Graphics g, string s, Font f, RectangleF box, Color c)
    {
        if (s.Length == 0) return;

        using (var halo = new SolidBrush(HaloColor))
            foreach (PointF d in Halo)
                g.DrawString(s, f, halo,
                    new RectangleF(box.X + d.X, box.Y + d.Y, box.Width, box.Height), Fmt);

        using var ink = new SolidBrush(c);
        g.DrawString(s, f, ink, box, Fmt);
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
            _surface?.Dispose();
            _fontValue?.Dispose();
            _fontLabel?.Dispose();
        }
        base.Dispose(disposing);
    }
}
