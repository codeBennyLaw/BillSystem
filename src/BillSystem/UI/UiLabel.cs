namespace BillSystem.UI;

/// <summary>
/// 自己画的文字标签。
///
/// 用不了系统 <see cref="Label"/>：窗口背景是一张带色斑的柔光底图，
/// Label 只能填一个实色（<c>BackColor = Transparent</c> 要靠父窗口回画，
/// 在自绘背景上既闪又对不上位置）。这里直接把底图对应的那一块贴进来再写字，
/// 底下的光斑就能透过文字周围，字也还是 GDI 的清晰渲染。
/// </summary>
internal sealed class UiLabel : Control
{
    public UiLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Font = Theme.FontBase;
        ForeColor = Theme.Text;
    }

    private ContentAlignment _align = ContentAlignment.MiddleLeft;

    /// <summary>默认垂直居中：给一个高度就摆在正中间，不用自己算基线。</summary>
    public ContentAlignment TextAlign
    {
        get => _align;
        set
        {
            if (_align == value) return;
            _align = value;
            Invalidate();
        }
    }

    /// <summary>放不下时折行（默认单行，超了以省略号收尾）。</summary>
    public bool Wrap { get; set; }

    /// <summary>
    /// 单行放不下时省略中段而不是尾巴。磁盘路径用：整条路径没有空格断不了行，
    /// 折行会把尾巴顶到控件外面去，而尾巴那截目录名恰恰是最该看到的。
    /// </summary>
    public bool PathEllipsis { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.PaintBackdrop(e.Graphics, this);
        if (Text.Length == 0) return;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags());
    }

    private TextFormatFlags Flags()
    {
        TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        f |= _align switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter
                => TextFormatFlags.HorizontalCenter,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight
                => TextFormatFlags.Right,
            _ => TextFormatFlags.Left,
        };
        f |= _align switch
        {
            ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight
                => TextFormatFlags.Top,
            ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight
                => TextFormatFlags.Bottom,
            _ => TextFormatFlags.VerticalCenter,
        };
        f |= Wrap
            ? TextFormatFlags.WordBreak
            : TextFormatFlags.SingleLine
              | (PathEllipsis ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis);
        return f;
    }

    /// <summary>按当前字体量一下这行字要多宽（排版时用来紧跟在文字后面摆东西）。</summary>
    public int Measure() => TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, Height),
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        Invalidate();
        base.OnForeColorChanged(e);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        Invalidate();
        base.OnFontChanged(e);
    }

    /// <summary>父窗口挪动/换背景时整块重画（贴的那块底图跟着位置变）。</summary>
    protected override void OnLocationChanged(EventArgs e)
    {
        Invalidate();
        base.OnLocationChanged(e);
    }
}
