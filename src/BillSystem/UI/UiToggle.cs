using System.Drawing.Drawing2D;

namespace BillSystem.UI;

/// <summary>
/// 开关（滑块式复选框）。深色下比系统 CheckBox 清楚得多。
/// 能 Tab 过来，空格/回车切换，键盘走到时轨道外描一圈焦点环。
/// </summary>
internal sealed class UiToggle : Control
{
    private bool _checked;

    // 滑块滑过去、底色淡入、焦点环淡入，都不是瞬间切换
    private readonly Anim _onA, _hoverA, _focusA;

    public UiToggle(string text, bool on = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _onA = new Anim(this, on ? 1 : 0, 170);
        _hoverA = new Anim(this, 0, 130);
        _focusA = new Anim(this, 0, 120);
        Text = text;
        _checked = on;
        Font = Theme.FontBase;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        Size = new Size(220, 28);
        TabStop = true;
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleName = Text;
    }

    public event Action<bool>? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            _onA.To(value ? 1 : 0);
            CheckedChanged?.Invoke(_checked);
        }
    }

    /// <summary>只改外观，不触发事件（同步配置用）。</summary>
    public void SetSilently(bool on)
    {
        _checked = on;
        _onA.Set(on ? 1 : 0);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hoverA.To(1); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverA.To(0); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !_checked; }

    /// <summary>点出来的焦点不画环，只有 Tab 走过来才画。</summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _focusA.To(MouseButtons == MouseButtons.None ? 1 : 0);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _focusA.To(0);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Space or Keys.Enter && Enabled) Checked = !_checked;
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Space || base.IsInputKey(keyData);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled) { _hoverA.Set(0); _focusA.Set(0); }
        Invalidate();
    }

    private const int TrackW = 38;
    private const int TrackH = 22;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float v = (float)_onA.Value, hv = (float)_hoverA.Value;
        var track = new RectangleF(1f, (Height - TrackH) / 2f, TrackW, TrackH);
        float rad = TrackH / 2f;

        // 关着是一段空玻璃，打开是灌了主色的玻璃：中间那段渐变过去
        Theme.Glass(g, track, rad, 0.1f + 0.5f * hv, 1f, Enabled, null, Enabled ? 1f : 0.6f);
        if (v > 0.004f)
        {
            Color on = Enabled ? Theme.Accent : Theme.Mix(Theme.Accent, Theme.Bg, 0.6f);
            using (GraphicsPath path = Theme.RoundedRect(track, rad))
            using (var brush = new LinearGradientBrush(RectangleF.Inflate(track, 1f, 1f),
                       Color.FromArgb((int)(235 * v), Theme.Mix(on, Color.White, 0.18f)),
                       Color.FromArgb((int)(235 * v), Theme.Mix(on, Color.Black, 0.06f)),
                       LinearGradientMode.Vertical))
                g.FillPath(brush, path);
            Theme.Gloss(g, track, rad, 0.3f + 0.4f * hv, v);
        }

        float knobD = TrackH - 6;
        float kx = track.Left + 3f + (track.Width - knobD - 6f) * v;
        var knobBox = new RectangleF(kx, track.Top + 3f, knobD, knobD);
        using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            g.FillEllipse(shadow, RectangleF.Inflate(knobBox, 1.2f, 1.2f));
        using (var knob = new LinearGradientBrush(RectangleF.Inflate(knobBox, 1f, 1f),
                   Color.White, Theme.Mix(Color.White, Theme.Bg, 0.22f), LinearGradientMode.Vertical))
            g.FillEllipse(knob, knobBox);

        // Tab 走到这个开关上：轨道外面套一圈亮环
        Theme.FocusRing(g, RectangleF.Inflate(track, 2f, 2f), rad + 2f, (float)_focusA.Value);

        var textRect = new Rectangle(TrackW + 12, 0, Math.Max(10, Width - TrackW - 12), Height);
        TextRenderer.DrawText(g, Text, Font, textRect,
            Enabled ? Theme.Mix(ForeColor, Theme.Text, hv) : Theme.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>自绘控件默认不会告诉读屏软件"勾上了没有"，这里补上。</summary>
    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessible(this);

    private sealed class ToggleAccessible : ControlAccessibleObject
    {
        private readonly UiToggle _owner;

        public ToggleAccessible(UiToggle owner) : base(owner) => _owner = owner;

        public override AccessibleStates State =>
            base.State | (_owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);

        public override string? DefaultAction => _owner.Checked ? "关掉" : "打开";

        public override void DoDefaultAction()
        {
            if (_owner.Enabled) _owner.Checked = !_owner.Checked;
        }
    }
}
