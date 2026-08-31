using System.Drawing.Imaging;

namespace BillSystem.UI;

/// <summary>
/// 换页时盖在内容上的一张"交接图"：底下摆新页面，上面压着旧页面，旧的淡掉就算交接完了。
///
/// 兄弟控件之间没法互相透出来（每个控件只画自己那块），所以两页都先各画成一张图，
/// 在这一层里混。淡完自己从窗口上摘下来，连两张图一起释放。
/// </summary>
internal sealed class PageSwap : Control
{
    private readonly Bitmap _from, _to;
    private readonly Anim _fade;

    private PageSwap(Bitmap from, Bitmap to)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Enabled = false;   // 只是块盖着的图，鼠标不该被它接住
        _from = from;
        _to = to;
        _fade = new Anim(this, 0, 180, v =>
        {
            // 自己在动画回调里摘自己不安全，排到下一轮消息里去
            if (v >= 1 - 1e-9) Parent?.BeginInvoke(Done);
        });
    }

    /// <summary>把 <paramref name="from"/> 淡成 <paramref name="to"/>。两张图之后由它负责释放。</summary>
    public static PageSwap? Play(Control parent, Rectangle at, Bitmap? from, Bitmap? to)
    {
        if (from is null || to is null || at.Width < 1 || at.Height < 1)
        {
            from?.Dispose();
            to?.Dispose();
            return null;
        }

        var swap = new PageSwap(from, to) { Bounds = at };
        parent.Controls.Add(swap);
        swap.BringToFront();
        swap._fade.To(1);
        return swap;
    }

    /// <summary>上一次还没淡完就又换页了：直接收掉，不然两张旧图叠在一起。</summary>
    public void Kill() => Done();

    private void Done()
    {
        if (IsDisposed) return;
        Parent?.Controls.Remove(this);
        Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float t = (float)Math.Clamp(_fade.Value, 0, 1);
        e.Graphics.DrawImageUnscaled(_to, 0, 0);
        if (t >= 0.999f) return;

        using var attr = new ImageAttributes();
        attr.SetColorMatrix(new ColorMatrix { Matrix33 = 1f - t });
        e.Graphics.DrawImage(_from, new Rectangle(0, 0, _from.Width, _from.Height),
            0, 0, _from.Width, _from.Height, GraphicsUnit.Pixel, attr);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _from.Dispose();
            _to.Dispose();
        }
        base.Dispose(disposing);
    }
}
