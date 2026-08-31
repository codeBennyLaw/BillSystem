using System.Drawing.Drawing2D;

namespace BillSystem.UI;

/// <summary>
/// 开关（滑块式复选框）。深色下比系统 CheckBox 清楚得多。
/// 点一下切换，也可以按住滑块拖——<b>松手那一下</b>才算改动，拖回原处就等于没动过。
/// 能 Tab 过来，空格/回车切换，键盘走到时轨道外描一圈焦点环。
/// </summary>
internal sealed class UiToggle : Control
{
    private bool _checked;

    private bool _pressing, _dragging, _eatClick;
    private int _pressX;
    private float _grabDx;   // 按下时光标离滑块左边缘多远，拖动时保持这个距离

    // 滑块滑过去、底色淡入、按下时的手感、焦点环淡入，都不是瞬间切换
    private readonly Anim _onA, _hoverA, _pressA, _focusA;

    public UiToggle(string text, bool on = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _onA = new Anim(this, on ? 1 : 0, 170);
        _hoverA = new Anim(this, 0, 130);
        _pressA = new Anim(this, 0, 90);
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
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!_pressing) _hoverA.To(0); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        _pressing = true;
        _dragging = false;
        _pressX = e.X;
        _grabDx = e.X - KnobX((float)_onA.Value);
        _pressA.To(1);
    }

    /// <summary>按住滑块横着拖，松手前只是在动滑块，不算改。</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressing) return;
        if (!_dragging && Math.Abs(e.X - _pressX) < 3) return;
        _dragging = true;
        Cursor = Cursors.SizeWE;
        _onA.Set(Math.Clamp((e.X - _grabDx - KnobHome) / Travel, 0f, 1f));   // 跟手，不缓动
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressA.To(0);
        if (!_pressing) return;
        _pressing = false;
        if (!_dragging) return;   // 只是点了一下，交给 OnClick 翻面

        _dragging = false;
        Cursor = Cursors.Hand;
        // 拖完松手系统还会补一次 Click，别让它又翻回去
        _eatClick = ClientRectangle.Contains(e.Location);

        bool want = _onA.Value >= 0.5;
        if (want == _checked) _onA.To(want ? 1 : 0);   // 拖了一半又拖回来：滑块自己吸附回去
        else Checked = want;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (_eatClick) { _eatClick = false; return; }
        if (Enabled) Checked = !_checked;
    }

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
        if (!Enabled)
        {
            _hoverA.Set(0);
            _focusA.Set(0);
            _pressA.Set(0);
            _pressing = _dragging = false;
        }

        Invalidate();
    }

    private const int TrackW = 38;
    private const int TrackH = 22;
    private const float TrackLeft = 1f;
    private const float KnobD = TrackH - 6f;
    private const float KnobHome = TrackLeft + 3f;          // 关到底时滑块的左边缘
    private const float Travel = TrackW - KnobD - 6f;       // 滑块能走的距离

    private static float KnobX(float v) => KnobHome + Travel * v;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float v = (float)_onA.Value, hv = (float)_hoverA.Value, pv = (float)_pressA.Value;
        var track = new RectangleF(TrackLeft, (Height - TrackH) / 2f, TrackW, TrackH);
        float rad = TrackH / 2f;

        // 关着是一段空玻璃，打开是灌了主色的玻璃：中间那段渐变过去
        Theme.Glass(g, track, rad, 0.1f + 0.5f * hv + 0.15f * pv, 1f, Enabled, null, Enabled ? 1f : 0.6f);
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

        // 按住时滑块鼓起来一点，手底下有反应
        var knobBox = new RectangleF(KnobX(v), track.Top + 3f, KnobD, KnobD);
        knobBox.Inflate(0.9f * pv, 0.9f * pv);
        using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            g.FillEllipse(shadow, RectangleF.Inflate(knobBox, 1.2f, 1.2f));
        using (var knob = new LinearGradientBrush(RectangleF.Inflate(knobBox, 1f, 1f),
                   Color.White, Theme.Mix(Color.White, Theme.Bg, 0.22f), LinearGradientMode.Vertical))
            g.FillEllipse(knob, knobBox);

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
