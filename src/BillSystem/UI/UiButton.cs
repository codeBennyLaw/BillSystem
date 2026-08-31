using System.Drawing.Drawing2D;

namespace BillSystem.UI;

/// <summary>按钮的观感：主色实心 / 描边 / 纯文字。</summary>
internal enum BtnKind
{
    Primary,
    Ghost,
    Quiet,
}

/// <summary>
/// 自己画的圆角按钮，带悬停和按下反馈。WinForms 自带的按钮在深色下太旧了。
/// 能 Tab 过来，空格/回车按下去，键盘走到的时候画一圈焦点环。
/// </summary>
internal sealed class UiButton : Control
{
    // 悬停/按下/焦点环都是渐变出来的，不是一下跳过去
    private readonly Anim _hoverA, _downA, _focusA;
    private bool _keyFocus;   // 焦点是键盘给的：鼠标点出来的焦点不画环，太吵

    public UiButton(string text, BtnKind kind = BtnKind.Ghost)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                 | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        _hoverA = new Anim(this, 0, 130);
        _downA = new Anim(this, 0, 80);
        _focusA = new Anim(this, 0, 120);
        Text = text;
        Kind = kind;
        Font = Theme.FontBase;
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
        Size = new Size(80, 30);
        TabStop = true;
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = Text;
    }

    private BtnKind _kind;

    /// <summary>观感。改了要自己重画——它不像 Text 那样有现成的变更通知。</summary>
    public BtnKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            Invalidate();
        }
    }

    /// <summary>圆角半径。实际画的时候不会超过高度的一半（那就是个胶囊）。</summary>
    public float Radius { get; set; } = 11f;

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hoverA.To(1); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverA.To(0); _downA.To(0); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); _downA.To(1); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _downA.To(0); }

    /// <summary>点出来的焦点不画环，只有 Tab 走过来才画——看这一刻鼠标键是不是按着的就知道。</summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _keyFocus = MouseButtons == MouseButtons.None;
        _focusA.To(_keyFocus ? 1 : 0);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _keyFocus = false;
        _focusA.To(0);
        _downA.To(0);
    }

    // 空格/回车当点一下，按住的时候按钮也是"按下"的样子
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) _downA.To(1);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is not (Keys.Space or Keys.Enter)) return;
        _downA.To(0);
        if (Enabled) PerformClick();
    }

    /// <summary>键盘按下去等于点一下。</summary>
    public void PerformClick() => OnClick(EventArgs.Empty);

    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Space || base.IsInputKey(keyData);

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        // 变灰的一瞬间鼠标可能正停在上面，此时不会再收到 MouseLeave，得自己清掉悬停状态
        if (!Enabled) { _hoverA.Set(0); _downA.Set(0); _focusA.Set(0); _keyFocus = false; }
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float hv = (float)_hoverA.Value, dn = (float)_downA.Value;
        float lift = Math.Clamp(hv * 0.8f + dn * 0.2f, 0f, 1f);

        // 按下去整块缩一点，像真被压下去
        RectangleF box = Theme.Inner(this);
        box.Inflate(-dn, -dn);
        if (box.Width < 1f || box.Height < 1f) return;
        float rad = Math.Min(Radius, box.Height / 2f);

        Color fore;
        if (Kind == BtnKind.Primary)
        {
            // 主色那颗不透明：一层主色渐变打底，再套玻璃的亮边和反光，看着像有颜色的玻璃
            Color fill = Theme.Mix(Theme.Accent, Color.White, 0.16f * hv);
            fill = Theme.Mix(fill, Color.Black, 0.24f * dn);
            if (!Enabled) fill = Theme.Mix(fill, Theme.Bg, 0.62f);

            Theme.Shadow(g, box, rad, Enabled ? 1.1f + 0.5f * hv : 0.4f);
            using (GraphicsPath path = Theme.RoundedRect(box, rad))
            using (var brush = new LinearGradientBrush(RectangleF.Inflate(box, 1f, 1f),
                       Theme.Mix(fill, Color.White, 0.13f), Theme.Mix(fill, Color.Black, 0.10f),
                       LinearGradientMode.Vertical))
                g.FillPath(brush, path);
            Theme.Gloss(g, box, rad, lift, Enabled ? 1f : 0.5f);
            fore = Enabled ? Color.White : Theme.Mix(Color.White, Theme.Bg, 0.45f);
        }
        else if (Kind == BtnKind.Ghost)
        {
            Theme.Glass(g, box, rad, lift, Enabled ? 1f : 0.75f, Enabled,
                hv > 0.01f ? Theme.Accent : null, Enabled ? 1f : 0.6f);
            fore = Enabled ? Theme.Text : Theme.TextDim;
        }
        else
        {
            // 纯文字那种平时什么都不画，鼠标过来才浮起一小块玻璃
            Theme.Glass(g, box, rad, lift, 0.85f, false, null, Math.Max(hv, dn));
            fore = !Enabled ? Theme.TextDim : Theme.Mix(Theme.TextSub, Theme.Text, hv);
        }

        // 焦点环画在里面，往里缩 2 像素，跟外面那圈边框分得开
        float fv = (float)_focusA.Value;
        if (fv > 0.01f)
        {
            var ring = new RectangleF(box.X + 2f, box.Y + 2f,
                Math.Max(1f, box.Width - 4f), Math.Max(1f, box.Height - 4f));
            using GraphicsPath rp = Theme.RoundedRect(ring, Math.Max(1f, rad - 2f));
            using var rpen = new Pen(Color.FromArgb((int)(215 * fv),
                Kind == BtnKind.Primary ? Color.White : Theme.Accent), 1.4f);
            g.DrawPath(rpen, rp);
        }

        var textBox = new Rectangle(0, (int)Math.Round(dn), Width, Height);
        TextRenderer.DrawText(g, Text, Font, textBox, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
            | TextFormatFlags.EndEllipsis);
    }
}
