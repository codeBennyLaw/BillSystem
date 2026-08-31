using System.Drawing.Drawing2D;

namespace BillSystem.UI;

/// <summary>
/// 分段选择器（小时 | 日 | 月 | 年 这种一整条的胶囊按钮）。
/// 点一下切过去，也可以按住那颗选中的胶囊左右拖——拖到哪一格就切到哪一格，松手吸附回格子。
/// Tab 走过来之后左右方向键也能切，Home / End 直接跳两头。
/// </summary>
internal sealed class Segment : Control
{
    private readonly List<(string Text, object Tag)> _items = new();
    private int _index;
    private int _hover = -1;

    private bool _pressing, _dragging;
    private int _pressX;
    private float _grabDx;   // 按下时光标离滑块左边缘多远，拖动时保持这个距离

    // 选中的胶囊滑过去，而不是瞬间挪位置；hover 底色和焦点环也是淡入的
    private readonly Anim _posA, _hoverA, _focusA;

    public Segment()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _posA = new Anim(this, 0, 200);
        _hoverA = new Anim(this, 0, 120);
        _focusA = new Anim(this, 0, 120);
        Font = Theme.FontBase;
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
        Height = 34;
        TabStop = true;
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        AccessibleRole = AccessibleRole.PageTabList;
        if (string.IsNullOrEmpty(AccessibleName)) AccessibleName = "统计粒度";
    }

    public event Action<object>? SelectionChanged;

    public void Add(string text, object tag)
    {
        _items.Add((text, tag));
        Invalidate();
    }

    public int Count => _items.Count;

    /// <summary>整条重排（宿舍列表变了就得重来一遍）。</summary>
    public void Clear()
    {
        _items.Clear();
        _index = 0;
        _posA.Set(0);
        _hover = -1;
        Invalidate();
    }

    public object? SelectedTag => _index >= 0 && _index < _items.Count ? _items[_index].Tag : null;

    /// <summary>按 Tag 选中，不触发事件（用来跟配置同步）。</summary>
    public void Select(object tag)
    {
        int i = _items.FindIndex(x => Equals(x.Tag, tag));
        if (i < 0 || i == _index) return;
        _index = i;
        _posA.To(i);
    }

    /// <summary>按每格宽度自动定宽（两头还要留出玻璃外的投影和内圈的空隙）。</summary>
    public void AutoWidth(int cellWidth) => Width = Math.Max(1, cellWidth * _items.Count) + 10;

    /// <summary>
    /// 按最宽那一格的字定宽。房号长短不齐（"43栋422" 和 "999栋9999" 差一截），
    /// 固定格宽会把字顶到胶囊外面去，所以量一遍再定。
    /// </summary>
    public void FitWidth(int minCell, int pad = 26)
    {
        int widest = 0;
        foreach ((string text, _) in _items)
            widest = Math.Max(widest, TextRenderer.MeasureText(text, Font).Width);
        AutoWidth(Math.Max(minCell, widest + pad));
    }

    /// <summary>某一格（可以给小数，滑块滑到一半时用）在控件里的位置。</summary>
    private RectangleF CellRect(float i)
    {
        RectangleF track = Theme.Inner(this);
        track.Inflate(-2f, -2f);
        float cw = track.Width / Math.Max(1, _items.Count);
        return new RectangleF(track.X + cw * i, track.Y, Math.Max(1f, cw), Math.Max(1f, track.Height));
    }

    private float CellWidth => Math.Max(1f, (Theme.Inner(this).Width - 4f) / Math.Max(1, _items.Count));

    private int HitTest(Point p)
    {
        for (int i = 0; i < _items.Count; i++)
            if (CellRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 按住滑块横着拖：滑块跟着手走，经过哪一格就切到哪一格，松手再吸附回格子中间
        if (_pressing)
        {
            if (!_dragging && Math.Abs(e.X - _pressX) < 3) return;
            _dragging = true;
            Cursor = Cursors.SizeWE;
            if (_hover != -1) { _hover = -1; _hoverA.Set(0); }

            float pos = Math.Clamp((e.X - _grabDx - CellRect(0).Left) / CellWidth, 0, _items.Count - 1);
            _posA.Set(pos);

            int near = (int)Math.Round(pos);
            if (near != _index)
            {
                _index = near;
                SelectionChanged?.Invoke(_items[near].Tag);
            }
            Invalidate();
            return;
        }

        int h = HitTest(e.Location);
        if (h == _hover) return;
        _hover = h;
        // 换了一格就从头淡入，免得底色跟着鼠标整条跑
        _hoverA.Set(0);
        if (h >= 0) _hoverA.To(1); else Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_pressing || _hover == -1) return;
        _hover = -1;
        _hoverA.To(0);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        int i = HitTest(e.Location);
        if (i < 0) return;

        Focus();

        // 点在别的格子上：先当一次点击切过去，滑块也就到了手底下，接着可以直接拖
        if (i != _index)
        {
            _index = i;
            _posA.To(i);
            SelectionChanged?.Invoke(_items[i].Tag);
        }

        _pressing = true;
        _dragging = false;
        _pressX = e.X;
        _grabDx = e.X - CellRect(i).Left;
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

    /// <summary>方向键换一格，Home / End 跳到两头。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;

        int want = e.KeyCode switch
        {
            Keys.Left or Keys.Up => _index - 1,
            Keys.Right or Keys.Down => _index + 1,
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            _ => _index,
        };
        want = Math.Clamp(want, 0, _items.Count - 1);
        if (want == _index) return;

        _index = want;
        _posA.To(want);
        SelectionChanged?.Invoke(_items[want].Tag);
        e.Handled = true;
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End
           || base.IsInputKey(keyData);

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressing) return;
        _pressing = false;
        Cursor = Cursors.Hand;
        if (_dragging) _posA.To(_index);   // 松手吸附到格子上
        _dragging = false;
        _hover = HitTest(e.Location);
        if (_hover >= 0) _hoverA.To(1);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.PaintBackdrop(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        RectangleF box = Theme.Inner(this);
        float rad = box.Height / 2f;
        Theme.Glass(g, box, rad, _hover >= 0 ? 0.25f : 0.1f);

        // Tab 走到这一条上：整条外圈描亮，提示"现在方向键管这里"
        Theme.FocusRing(g, box, rad, (float)_focusA.Value);

        // 悬停底色画在滑块下面，不然滑块经过时会被盖住
        if (_hover >= 0 && _hoverA.Value > 0.01)
        {
            RectangleF hr = CellRect(_hover);
            using GraphicsPath hp = Theme.RoundedRect(hr, hr.Height / 2f);
            using var hb = new SolidBrush(Color.FromArgb((int)(34 * _hoverA.Value), 255, 255, 255));
            g.FillPath(hb, hp);
        }

        float pos = (float)_posA.Value;
        RectangleF sel = CellRect(pos);
        if (_items.Count > 0 && sel.Width >= 1 && sel.Height >= 1)
        {
            // 选中那颗是"有颜色的玻璃"：主色渐变打底，再套一层亮边和顶上的反光
            Theme.Shadow(g, sel, sel.Height / 2f, 0.9f);
            using (GraphicsPath sp = Theme.RoundedRect(sel, sel.Height / 2f))
            using (var brush = new LinearGradientBrush(RectangleF.Inflate(sel, 1f, 1f),
                       Theme.Mix(Theme.Accent, Color.White, 0.16f),
                       Theme.Mix(Theme.Accent, Color.Black, 0.08f), LinearGradientMode.Vertical))
                g.FillPath(brush, sp);
            Theme.Gloss(g, sel, sel.Height / 2f, 0.4f);
        }

        for (int i = 0; i < _items.Count; i++)
        {
            // 滑块盖住多少，文字就有多白——滑动过程中两边的字自然交接
            float cover = 1f - Math.Min(1f, Math.Abs(i - pos));
            Color baseColor = i == _hover ? Theme.Mix(Theme.TextSub, Theme.Text, (float)_hoverA.Value) : Theme.TextSub;
            TextRenderer.DrawText(g, _items[i].Text, Font, Rectangle.Round(CellRect(i)),
                Theme.Mix(baseColor, Color.White, cover),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
