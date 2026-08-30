using System.Drawing.Drawing2D;

namespace BillSystem.UI;

/// <summary>
/// 数字输入框：中间能直接打字，两头各一个 − / +（按住会连发），滚轮和上下键也能调。
///
/// 系统的 NumericUpDown 在深色主题下没法看——两个小箭头永远是浅色的，边框也是方的，
/// 所以这里自己画一个，只把中间那块交给真的 TextBox 处理输入。
/// </summary>
internal sealed class UiSpin : Control
{
    private const int BtnW = 28;

    private readonly TextBox _box = new();
    private readonly Anim _minusA, _plusA, _focusA;
    private readonly System.Windows.Forms.Timer _repeat = new() { Interval = 400 };

    private double _value;
    private int _dir;      // 正在按住的方向：-1 / 0 / +1
    private int _hover;    // 鼠标在哪个按钮上
    private bool _syncing; // 正在把 Value 写回文本框，别再反过来解析一遍

    public UiSpin()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _minusA = new Anim(this, 0, 120);
        _plusA = new Anim(this, 0, 120);
        _focusA = new Anim(this, 0, 150);

        BackColor = Theme.Bg;
        Size = new Size(120, 30);

        _box.BorderStyle = BorderStyle.None;
        _box.BackColor = Theme.Field;
        _box.ForeColor = Theme.Text;
        _box.Font = Theme.FontBase;
        _box.TextAlign = HorizontalAlignment.Center;
        _box.Text = "0";
        _box.TextChanged += (_, _) => OnTyped();
        _box.Leave += (_, _) => Commit();
        _box.GotFocus += (_, _) => _focusA.To(1);
        _box.LostFocus += (_, _) => _focusA.To(0);
        _box.KeyDown += OnBoxKeyDown;
        _box.MouseWheel += (_, e) => Bump(e.Delta > 0 ? 1 : -1);
        Controls.Add(_box);

        _repeat.Tick += (_, _) => { _repeat.Interval = 55; Bump(_dir); };
    }

    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public double Step { get; set; } = 1;
    public int Decimals { get; set; }

    public double Value
    {
        get => _value;
        set
        {
            _value = Clamp(value);
            SyncText();
            Invalidate();
        }
    }

    private double Clamp(double v) =>
        Math.Round(Math.Clamp(v, Minimum, Maximum), Math.Max(0, Decimals), MidpointRounding.AwayFromZero);

    private string Fmt(double v) => v.ToString("F" + Math.Max(0, Decimals));

    private void SyncText()
    {
        _syncing = true;
        _box.Text = Fmt(_value);
        _box.SelectionStart = _box.TextLength;
        _syncing = false;
    }

    /// <summary>打字的时候只认数，不动光标也不补零——等离开或回车再规整。</summary>
    private void OnTyped()
    {
        if (_syncing) return;
        if (double.TryParse(_box.Text, out double v) && v >= Minimum && v <= Maximum)
        {
            _value = Math.Round(v, Math.Max(0, Decimals), MidpointRounding.AwayFromZero);
            Invalidate();
        }
    }

    /// <summary>规整文本框里的内容：不是数就退回原值，超范围就夹回来。</summary>
    private void Commit()
    {
        if (double.TryParse(_box.Text, out double v)) _value = Clamp(v);
        SyncText();
        Invalidate();
    }

    private void Bump(int dir)
    {
        if (dir == 0 || !Enabled) return;
        Value = Clamp(_value + Step * dir);
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up) { Bump(1); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Down) { Bump(-1); e.Handled = e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; }
    }

    private Rectangle MinusRect => new(4, 3, BtnW, Math.Max(1, Height - 8));
    private Rectangle PlusRect => new(Math.Max(1, Width - BtnW - 4), 3, BtnW, Math.Max(1, Height - 8));

    private int HitTest(Point p) => MinusRect.Contains(p) ? -1 : PlusRect.Contains(p) ? 1 : 0;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int h = _box.PreferredHeight;
        _box.SetBounds(BtnW + 5, Math.Max(1, (Height - h) / 2),
            Math.Max(10, Width - BtnW * 2 - 10), h);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        if (h == _hover) return;
        _hover = h;
        Cursor = h == 0 ? Cursors.Default : Cursors.Hand;
        _minusA.To(h == -1 ? 1 : 0);
        _plusA.To(h == 1 ? 1 : 0);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = 0;
        _dir = 0;
        _repeat.Stop();
        _minusA.To(0);
        _plusA.To(0);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _dir = HitTest(e.Location);
        if (_dir == 0) { _box.Focus(); return; }
        Bump(_dir);
        _repeat.Interval = 400;
        _repeat.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dir = 0;
        _repeat.Stop();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Bump(e.Delta > 0 ? 1 : -1);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _box.Enabled = Enabled;
        _box.ForeColor = Enabled ? Theme.Text : Theme.TextDim;
        _box.BackColor = FieldColor;
        if (!Enabled) { _hover = 0; _dir = 0; _repeat.Stop(); _minusA.Set(0); _plusA.Set(0); }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _repeat.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float fc = (float)_focusA.Value;
        // 只让边框跟着焦点变亮：底色一动，中间那个真 TextBox 就跟不上了
        Color line = Enabled
            ? Theme.Mix(Color.FromArgb(0x3A, 0x40, 0x4C), Theme.Accent,
                0.9f * fc + 0.35f * (float)Math.Max(_minusA.Value, _plusA.Value))
            : Color.FromArgb(0x2A, 0x2E, 0x37);

        RectangleF box = Theme.Inner(this);
        Theme.Shadow(g, box, 10f, 0.6f);
        Theme.FrostField(g, box, 10f, FieldColor, line);
        Theme.FocusRing(g, box, 10f, fc * 0.9f);

        DrawBtn(g, MinusRect, false, (float)_minusA.Value);
        DrawBtn(g, PlusRect, true, (float)_plusA.Value);
    }

    /// <summary>框里那块实色。里面嵌着真 TextBox，透不出背景，只能整块同色。</summary>
    private Color FieldColor => Enabled ? Theme.Field : Theme.Mix(Theme.Field, Theme.Bg, 0.5f);

    private void DrawBtn(Graphics g, Rectangle r, bool plus, float hv)
    {
        if (Enabled && hv > 0.01f)
        {
            var pad = new RectangleF(r.X + 1.5f, r.Y + 1.5f, r.Width - 3f, r.Height - 3f);
            using GraphicsPath hp = Theme.RoundedRect(pad, 7f);
            using var hb = new SolidBrush(Color.FromArgb((int)(78 * hv), Theme.Accent));
            g.FillPath(hb, hp);
        }

        Color c = Enabled ? Theme.Mix(Theme.TextSub, Color.White, hv) : Theme.TextDim;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using var pen = new Pen(c, 1.6f);
        g.DrawLine(pen, cx - 4.5f, cy, cx + 4.5f, cy);
        if (plus) g.DrawLine(pen, cx, cy - 4.5f, cx, cy + 4.5f);
    }
}
