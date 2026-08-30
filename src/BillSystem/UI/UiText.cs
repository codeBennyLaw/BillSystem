using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace BillSystem.UI;

/// <summary>
/// 文本输入框。跟 <see cref="UiSpin"/> 一个路子：外框自己画（圆角、聚焦时描边变亮），
/// 中间嵌一个真的 TextBox 负责输入法和光标。
/// </summary>
internal sealed class UiText : Control
{
    private readonly TextBox _box = new();
    private readonly Anim _focusA;

    public UiText()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _focusA = new Anim(this, 0, 150);

        BackColor = Theme.Bg;
        Size = new Size(160, 30);

        _box.BorderStyle = BorderStyle.None;
        _box.BackColor = Theme.Field;
        _box.ForeColor = Theme.Text;
        _box.Font = Theme.FontBase;
        _box.GotFocus += (_, _) => _focusA.To(1);
        _box.LostFocus += (_, _) => _focusA.To(0);
        _box.TextChanged += (_, _) => TextEdited?.Invoke(_box.Text);
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Submitted?.Invoke(); e.SuppressKeyPress = true; }
        };
        // 按键事件不会从子控件冒上来，过滤只能挂在这个 TextBox 上
        _box.KeyPress += (_, e) =>
        {
            if (DigitsOnly && !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        };
        Controls.Add(_box);
    }

    public event Action<string>? TextEdited;

    /// <summary>在框里按回车。</summary>
    public event Action? Submitted;

    /// <summary>只收数字（金额用得上）。</summary>
    public bool DigitsOnly { get; set; }

    /// <summary>设成 '●' 就是密码框（授权码那格用）。</summary>
    public char PasswordChar
    {
        get => _box.PasswordChar;
        set => _box.PasswordChar = value;
    }

    public int MaxLength
    {
        get => _box.MaxLength;
        set => _box.MaxLength = value;
    }

    public HorizontalAlignment TextAlign
    {
        get => _box.TextAlign;
        set => _box.TextAlign = value;
    }

    [AllowNull]
    public override string Text
    {
        get => _box.Text;
        set
        {
            string v = value ?? "";
            if (_box.Text == v) return;
            _box.Text = v;
            _box.SelectionStart = _box.TextLength;
        }
    }

    /// <summary>没输入内容时显示的灰字提示。交给里面那个 TextBox 画——
    /// 外框自己画的话会被它不透明的底色盖掉。</summary>
    public string Placeholder
    {
        get => _box.PlaceholderText;
        set => _box.PlaceholderText = value ?? "";
    }

    public new void Focus() => _box.Focus();

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int h = _box.PreferredHeight;
        _box.SetBounds(10, Math.Max(1, (Height - h) / 2), Math.Max(10, Width - 20), h);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _box.Focus();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _box.Enabled = Enabled;
        _box.ForeColor = Enabled ? Theme.Text : Theme.TextDim;
        _box.BackColor = FieldColor;
        if (!Enabled) _focusA.Set(0);
        Invalidate();
    }

    /// <summary>框里那块实色。里面嵌着真 TextBox，透不出背景，只能整块同色。</summary>
    private Color FieldColor => Enabled ? Theme.Field : Theme.Mix(Theme.Field, Theme.Bg, 0.5f);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float fc = (float)_focusA.Value;
        Color line = Enabled
            ? Theme.Mix(Color.FromArgb(0x3A, 0x40, 0x4C), Theme.Accent, 0.9f * fc)
            : Color.FromArgb(0x2A, 0x2E, 0x37);

        RectangleF box = Theme.Inner(this);
        Theme.Shadow(g, box, 10f, 0.6f);
        Theme.FrostField(g, box, 10f, FieldColor, line);
        Theme.FocusRing(g, box, 10f, fc * 0.9f);
    }
}
