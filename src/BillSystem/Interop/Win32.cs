using System.Runtime.InteropServices;

namespace BillSystem.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct APPBARDATA
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public IntPtr lParam;
}

internal enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

internal static class Win32
{
    /// <summary>窗口的"主人"（owner）。系统始终把窗口画在它 owner 的上面。</summary>
    public const int GWLP_HWNDPARENT = -8;

    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_COMPOSITED = 0x02000000;
    public const int WS_EX_LAYERED = 0x00080000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint ABM_GETTASKBARPOS = 0x00000005;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] buf, int count);

    private const uint GW_OWNER = 4;

    public static IntPtr GetOwner(IntPtr hWnd) => GetWindow(hWnd, GW_OWNER);

    /// <summary>
    /// 把 <paramref name="child"/> 认到 <paramref name="owner"/> 名下。认上以后系统会保证它
    /// 一直画在 owner 上面——比自己反复 SetWindowPos 抢 Z 序稳得多，也不会闪。
    /// 注意：owner 被销毁时它名下的窗口会跟着一起销毁（explorer 重启就是这种情况）。
    /// 返回 false 表示系统没吃这一套，调用方得退回轮询置顶。
    /// </summary>
    public static bool TrySetOwner(IntPtr child, IntPtr owner)
    {
        if (child == IntPtr.Zero) return false;
        SetWindowLongEx(child, GWLP_HWNDPARENT, owner.ToInt64());
        return GetOwner(child) == owner;
    }

    public static string ClassNameOf(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";
        var buf = new char[256];
        int n = GetClassName(hWnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>窗口事件回调（只用来听"前台窗口换了"）。</summary>
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod,
        WinEventProc proc, uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    /// <summary>
    /// 监听前台窗口切换。回调走安装它的那个线程的消息循环（也就是 UI 线程），可以直接动窗口。
    /// 返回 IntPtr.Zero 表示装不上，调用方自己退回定时轮询。
    /// </summary>
    public static IntPtr HookForeground(WinEventProc proc) =>
        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT);

    public static void Unhook(IntPtr hook)
    {
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>
    /// 给进程一个固定的 AppUserModelID。Win10/11 拿它把托盘气泡当成正经应用的通知来显示
    /// （能进通知中心、能在"通知和操作"里单独设置），没有它有时候只闪一下就没了。
    /// </summary>
    public static void SetAppId(string appId)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(appId);
        }
        catch (Exception)
        {
            // 老系统没这个导出，无所谓
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    /// <summary>屏幕上这一点归哪个窗口。分层窗口按 alpha 判定：透明的地方点不到。</summary>
    public static IntPtr WindowAt(Point p) => WindowFromPoint(new POINT { X = p.X, Y = p.Y });

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;

    /// <summary>标题栏跟着深色主题走，省得白底标题栏很突兀。</summary>
    public static void UseDarkTitleBar(IntPtr hWnd, Color? border = null)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            if (border is { } c)
            {
                int bgr = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref bgr, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // 老系统没有 dwmapi，无所谓
        }
    }

    // ---------- 分层窗口：整块位图连 alpha 一起交给系统合成 ----------

    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Cx, Cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, IntPtr pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey,
        ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    /// <summary>
    /// 把一张带 alpha 的位图整块推给分层窗口（<c>WS_EX_LAYERED</c> + <c>UpdateLayeredWindow</c>）。
    /// 没画到的地方是<b>真透明</b>；画面存在 DWM 那边，别的窗口在上面重画不会把它擦掉，
    /// 也就没有"闪一下没了、切个窗口又回来"的毛病。位置和大小仍旧由 <see cref="SetWindowPos"/> 管。
    /// </summary>
    public static bool PushLayered(IntPtr hWnd, Bitmap bmp)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;

        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbm = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            if (mem == IntPtr.Zero) return false;

            hbm = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hbm);

            var src = new POINT();
            var size = new SIZE { Cx = bmp.Width, Cy = bmp.Height };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };

            return UpdateLayeredWindow(hWnd, screen, IntPtr.Zero, ref size,
                mem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static void SetWindowLongEx(IntPtr hWnd, int nIndex, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
        else SetWindowLong32(hWnd, nIndex, (int)value);
    }

    /// <summary>主任务栏窗口（Win10/Win11 都是 Shell_TrayWnd）。</summary>
    public static IntPtr FindTaskbar() => FindWindow("Shell_TrayWnd", null);

    /// <summary>任务栏在屏幕上的位置和停靠边。失败时返回 false。</summary>
    private static bool TryGetTaskbarPos(out RECT rect, out TaskbarEdge edge)
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != IntPtr.Zero)
        {
            rect = data.rc;
            edge = (TaskbarEdge)data.uEdge;
            return true;
        }

        IntPtr tb = FindTaskbar();
        if (tb != IntPtr.Zero && GetWindowRect(tb, out rect))
        {
            edge = rect.Width >= rect.Height ? TaskbarEdge.Bottom : TaskbarEdge.Left;
            return true;
        }

        rect = default;
        edge = TaskbarEdge.Bottom;
        return false;
    }

    /// <summary>
    /// 任务栏的完整情况：窗口句柄、此刻真正在屏幕上的位置（开了自动隐藏时会缩成一条）、
    /// 停靠时的位置（<c>ABM_GETTASKBARPOS</c> 给的，隐藏时也不变）、停靠边。
    /// </summary>
    public static bool TryGetTaskbar(out IntPtr hwnd, out RECT live, out RECT docked, out TaskbarEdge edge)
    {
        hwnd = FindTaskbar();
        if (!TryGetTaskbarPos(out docked, out edge))
        {
            live = default;
            return false;
        }

        // 停靠位置来自 ABM（隐藏了也照样报完整高度），真实位置只能问窗口自己
        live = hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT r) && r.Width > 0 && r.Height > 0
            ? r
            : docked;
        return true;
    }
}
