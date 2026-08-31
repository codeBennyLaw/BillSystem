namespace BillSystem.UI;

/// <summary>自己会滑的控件（图表左右滑、记录列表上下滑）。滚轮由 <see cref="Wheel"/> 按鼠标位置派过来。</summary>
internal interface IWheelScroll
{
    void ScrollByWheel(int delta);
}

/// <summary>
/// 把滚轮派给<b>鼠标底下</b>那个控件。
///
/// Win32 是把 <c>WM_MOUSEWHEEL</c> 发给有键盘焦点的窗口的，不是鼠标停着的那个。图表和记录列表都
/// 不接焦点（点一下不该把输入框里的光标抢走），所以在控件里重写 <c>OnMouseWheel</c> 根本等不到消息，
/// 滚轮会落在刚点过的那个按钮或输入框身上，白转半天。这儿在消息进控件之前先拦一道，
/// 鼠标底下要是个会滑的控件就交给它、就此为止；不是就原样放过去（数字框自己那套滚轮调数照旧）。
/// </summary>
internal sealed class Wheel : IMessageFilter
{
    private const int WmMouseWheel = 0x020A;

    /// <summary>进消息循环之前挂一次就行（<see cref="Program"/> 里）。</summary>
    public static void Install() => Application.AddMessageFilter(new Wheel());

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel) return false;

        // 消息本来要发给谁：拿它所在的窗口当搜索起点，多开几个窗口时才不会串
        Control? to = Control.FromHandle(m.HWnd);
        if ((to?.FindForm() ?? to as Form) is not { } host) return false;
        if (Deepest(host, Control.MousePosition) is not IWheelScroll target) return false;

        target.ScrollByWheel((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
        return true;
    }

    /// <summary>鼠标底下最里层那个控件（<see cref="Control.GetChildAtPoint(Point)"/> 一次只看一层）。</summary>
    private static Control? Deepest(Control root, Point screen)
    {
        Control cur = root;
        while (true)
        {
            Control? child = cur.GetChildAtPoint(cur.PointToClient(screen),
                GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Disabled);
            if (child is null) return ReferenceEquals(cur, root) ? null : cur;
            cur = child;
        }
    }
}
