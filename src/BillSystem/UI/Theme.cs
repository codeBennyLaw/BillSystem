using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace BillSystem.UI;

/// <summary>统一配色与字体。深色为主，浅色只用于跟随系统的任务栏组件。</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x12, 0x14, 0x18);

    /// <summary>标题栏和托盘菜单那种"实色面板"：玻璃透不进去的地方（系统画的部件）才用。</summary>
    public static readonly Color PanelHi = Color.FromArgb(0x24, 0x28, 0x31);

    public static readonly Color Text = Color.FromArgb(0xEC, 0xEF, 0xF4);
    public static readonly Color TextSub = Color.FromArgb(0x9B, 0xA3, 0xB2);
    public static readonly Color TextDim = Color.FromArgb(0x6A, 0x72, 0x80);

    public static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);
    public static readonly Color Good = Color.FromArgb(0x34, 0xD3, 0x99);
    public static readonly Color Warn = Color.FromArgb(0xFB, 0xBF, 0x24);
    public static readonly Color Bad = Color.FromArgb(0xF8, 0x71, 0x71);
    public static readonly Color Remain = Color.FromArgb(0xFB, 0xBF, 0x24);

    /// <summary>剩余电量因为充值涨上去的那一格：跟平时那根金色柱子分开，一眼看得出"这儿充过"。</summary>
    public static readonly Color Recharge = Color.FromArgb(0x35, 0xD6, 0xA4);

    /// <summary>背景光斑里那团紫，只用来给玻璃透出点颜色。</summary>
    private static readonly Color Violet = Color.FromArgb(0x7C, 0x5C, 0xFF);

    /// <summary>
    /// 嵌着真 TextBox 的输入框只能用实色打底——子控件透不出下面的背景，
    /// 所以取一个跟玻璃面板差不多亮的实色，摆在一起看不出接缝。
    /// </summary>
    public static readonly Color Field = Color.FromArgb(0x23, 0x27, 0x31);

    /// <summary>优先用微软雅黑，没有就退回系统默认无衬线字体。</summary>
    public static string Family { get; } = PickFamily("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial");

    public static readonly Font FontSmall = new(Family, 8.25f);
    public static readonly Font FontBase = new(Family, 9f);
    public static readonly Font FontMid = new(Family, 10.5f);
    public static readonly Font FontBold = new(Family, 9f, FontStyle.Bold);
    public static readonly Font FontBig = new(Family, 21f, FontStyle.Bold);
    public static readonly Font FontTitle = new(Family, 13.5f, FontStyle.Bold);

    private static string PickFamily(params string[] candidates)
    {
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string c in candidates)
            if (names.Contains(c)) return c;
        return FontFamily.GenericSansSerif.Name;
    }

    /// <summary>剩余电量对应的颜色：够用=绿，偏少=黄，见底=红。</summary>
    public static Color LevelColor(double? remaining, double threshold)
    {
        if (remaining is null) return TextDim;
        if (remaining <= threshold) return Bad;
        if (remaining <= threshold * 2) return Warn;
        return Good;
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            object? v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0);
            return v is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>两色按比例混合，做悬停/按下的深浅变化用。</summary>
    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>让窗口用深色标题栏（Win10 1809+）。</summary>
    public static void ApplyDarkChrome(Form f)
    {
        void Apply() => Interop.Win32.UseDarkTitleBar(f.Handle, PanelHi);
        if (f.IsHandleCreated) Apply();
        f.HandleCreated += (_, _) => Apply();
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.1f)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>只有上面两个角是圆的（画柱状图用）。</summary>
    public static GraphicsPath TopRoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.1f)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
        path.CloseFigure();
        return path;
    }

    // ---------- 液态玻璃 ----------
    //
    // GDI+ 没有"背景模糊"这种东西（那要拿到窗口后面的像素再做高斯模糊，WinForms 给不了），
    // 所以这里换个路子：整窗先铺一张自己画的柔光背景（几团大色斑），面板不再用实色，
    // 而是在同一张背景上压一层半透明暗色 + 自上而下的白色高光 + 一圈亮边 + 顶上一道反光。
    // 背景本身是渐变加大色斑，透过来的颜色随位置变，看着就有玻璃那股子通透。

    /// <summary>玻璃往控件里缩这么多，外面那点地方留给投影。</summary>
    public const float Bleed = 3f;

    /// <summary>控件里画玻璃的那块矩形（四周留出投影的地方，顶上少留一点）。</summary>
    public static RectangleF Inner(Control c) => new(
        Bleed, Bleed - 1f,
        Math.Max(1f, c.Width - Bleed * 2f),
        Math.Max(1f, c.Height - Bleed * 2f));

    private static readonly Dictionary<Size, Bitmap> Backdrops = new();

    /// <summary>
    /// 整窗的柔光背景。同一个尺寸只画一次存起来——每帧重画几团 <see cref="PathGradientBrush"/>
    /// 太慢，而且色斑位置是按比例算死的，所以 <c>--screenshot</c> 每次出的图都一模一样。
    /// </summary>
    public static Bitmap Backdrop(Size size)
    {
        size = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        if (Backdrops.TryGetValue(size, out Bitmap? hit)) return hit;

        // 缓存太多张就整批扔掉重来：窗口尺寸只有几种，拖动缩放时才会攒起来
        if (Backdrops.Count >= 12)
        {
            foreach (Bitmap old in Backdrops.Values) old.Dispose();
            Backdrops.Clear();
        }

        var bmp = new Bitmap(size.Width, size.Height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = size.Width, h = size.Height, u = Math.Max(w, h);

            using (var back = new LinearGradientBrush(
                       new RectangleF(-1, -1, w + 2, h + 2),
                       Color.FromArgb(0x16, 0x19, 0x20), Color.FromArgb(0x0C, 0x0E, 0x12),
                       LinearGradientMode.ForwardDiagonal))
                g.FillRectangle(back, 0, 0, w, h);

            Blob(g, w * 0.10f, h * -0.04f, u * 0.46f, Accent, 52);
            Blob(g, w * 1.00f, h * 0.08f, u * 0.38f, Violet, 42);
            Blob(g, w * 0.74f, h * 0.98f, u * 0.44f, Recharge, 26);
            Blob(g, w * -0.02f, h * 0.82f, u * 0.34f, Warn, 20);
        }

        Backdrops[size] = bmp;
        return bmp;
    }

    /// <summary>一团中间亮、边上化开的圆光斑。</summary>
    private static void Blob(Graphics g, float cx, float cy, float r, Color c, int alpha)
    {
        var box = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using var path = new GraphicsPath();
        path.AddEllipse(box);
        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = new PointF(cx, cy),
            CenterColor = Color.FromArgb(alpha, c),
            SurroundColors = new[] { Color.FromArgb(0, c) },
        };
        brush.SetSigmaBellShape(0.98f, 1f);
        g.FillPath(brush, path);
    }

    /// <summary>
    /// 自己另画一张底图的窗口（比如设置里那几张分组卡）实现这个，
    /// 里面的子控件就会去贴那张合成好的图，而不是光秃秃的窗口背景。
    /// </summary>
    internal interface IBackdropHost
    {
        Bitmap BackdropImage { get; }
    }

    /// <summary>
    /// 把整窗背景按控件自己的位置贴进来，代替 <c>g.Clear(Bg)</c>。
    /// 每个控件贴的是同一张图的对应那一块，所以拼在一起看不出接缝，
    /// 而且贴完就是不透明的表面，GDI 的文字（<c>TextRenderer</c> 不认 alpha）画上去才清楚。
    /// </summary>
    public static void PaintBackdrop(Graphics g, Control c)
    {
        g.Clear(Bg);
        int ox = 0, oy = 0;
        Control top = c;
        while (top.Parent is not null)
        {
            ox += top.Left;
            oy += top.Top;
            top = top.Parent;
        }

        Bitmap bmp = top is IBackdropHost host ? host.BackdropImage : Backdrop(top.ClientSize);
        g.DrawImageUnscaled(bmp, -ox, -oy);
    }

    /// <summary>
    /// 一块玻璃：柔和投影 → 半透明暗底 → 自上而下的白高光 → 斜着渐变的亮边 → 顶上一道反光。
    /// <paramref name="lift"/> 是"抬起来"的程度（0 平放，1 悬停/按下），越高越亮、投影越散。
    /// <paramref name="density"/> 调厚度：气泡这种要压住底下的图，得比面板更实。
    /// <paramref name="opacity"/> 是整块玻璃的淡入程度（入场动画用）。
    /// </summary>
    public static void Glass(Graphics g, RectangleF r, float radius,
        float lift = 0f, float density = 1f, bool shadow = true, Color? glow = null, float opacity = 1f)
    {
        if (r.Width < 1f || r.Height < 1f) return;
        lift = Math.Clamp(lift, 0f, 1f);
        opacity = Math.Clamp(opacity, 0f, 1f);
        if (opacity <= 0.004f) return;
        if (shadow) Shadow(g, r, radius, (1f + 0.5f * lift) * opacity);

        using GraphicsPath path = RoundedRect(r, radius);
        RectangleF grad = RectangleF.Inflate(r, 1f, 1f);

        using (var tint = new SolidBrush(Color.FromArgb(
                   A((116 - 14 * lift) * density * opacity), 0x0A, 0x0D, 0x13)))
            g.FillPath(tint, path);

        using (var sheen = new LinearGradientBrush(grad,
                   Color.FromArgb(A((23 + 24 * lift) * opacity), 255, 255, 255),
                   Color.FromArgb(A((5 + 8 * lift) * opacity), 255, 255, 255), LinearGradientMode.Vertical))
            g.FillPath(sheen, path);

        if (glow is { } gl)
            using (var halo = new LinearGradientBrush(grad,
                       Color.FromArgb(A((26 + 44 * lift) * opacity), gl), Color.FromArgb(0, gl),
                       LinearGradientMode.Vertical))
                g.FillPath(halo, path);

        using (var edge = new LinearGradientBrush(grad,
                   Color.FromArgb(A((70 + 60 * lift) * opacity), 255, 255, 255),
                   Color.FromArgb(A((14 + 10 * lift) * opacity), 255, 255, 255), LinearGradientMode.ForwardDiagonal))
        using (var pen = new Pen(edge, 1f))
            g.DrawPath(pen, path);

        Specular(g, r, radius, (0.55f + 0.45f * lift) * opacity);
    }

    /// <summary>
    /// 只画玻璃的"面"：一圈斜着渐变的亮边 + 顶上那道反光。
    /// 自己已经上了色的东西（主色按钮、选中的那一格）套一层这个就有玻璃的光泽。
    /// </summary>
    public static void Gloss(Graphics g, RectangleF r, float radius, float lift = 0f, float opacity = 1f)
    {
        if (r.Width < 1f || r.Height < 1f || opacity <= 0.004f) return;
        using GraphicsPath path = RoundedRect(r, radius);
        using (var edge = new LinearGradientBrush(RectangleF.Inflate(r, 1f, 1f),
                   Color.FromArgb(A((66 + 54 * lift) * opacity), 255, 255, 255),
                   Color.FromArgb(A((12 + 10 * lift) * opacity), 255, 255, 255), LinearGradientMode.ForwardDiagonal))
        using (var pen = new Pen(edge, 1f))
            g.DrawPath(pen, path);
        Specular(g, r, radius, (0.5f + 0.4f * lift) * opacity);
    }

    private static int A(float v) => (int)Math.Clamp(v, 0f, 255f);

    /// <summary>叠几层越来越淡的圆角矩形当投影——GDI+ 没有现成的高斯阴影。</summary>
    public static void Shadow(Graphics g, RectangleF r, float radius, float amount = 1f)
    {
        for (int i = 3; i >= 1; i--)
        {
            RectangleF box = RectangleF.Inflate(r, i * 1.6f, i * 1.6f);
            box.Y += i * 1.1f;
            using GraphicsPath p = RoundedRect(box, radius + i * 1.6f);
            using var b = new SolidBrush(Color.FromArgb(A(12 * amount), 0, 0, 0));
            g.FillPath(b, p);
        }
    }

    /// <summary>顶边那道细反光：中间偏左最亮，两头化开，玻璃的"边缘感"全靠它。</summary>
    private static void Specular(Graphics g, RectangleF r, float radius, float amount)
    {
        float inset = Math.Max(2f, radius * 0.6f);
        var line = new RectangleF(r.X + inset, r.Y + 1.1f, r.Width - inset * 2f, 1.1f);
        if (line.Width < 6f) return;

        using var brush = new LinearGradientBrush(
            RectangleF.Inflate(line, 1f, 1f), Color.White, Color.White, LinearGradientMode.Horizontal);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = new[]
            {
                Color.FromArgb(0, 255, 255, 255),
                Color.FromArgb(A(118 * amount), 255, 255, 255),
                Color.FromArgb(A(44 * amount), 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
            },
            Positions = new[] { 0f, 0.2f, 0.74f, 1f },
        };
        g.FillRectangle(brush, line);
    }

    /// <summary>
    /// 输入框那种"实心磨砂"：里面嵌着真 TextBox，透不出背景，只能整块实色，
    /// 但边和顶上那道反光照玻璃的画法来，摆在玻璃面板旁边不打眼。
    /// </summary>
    public static void FrostField(Graphics g, RectangleF r, float radius, Color fill, Color line)
    {
        using GraphicsPath path = RoundedRect(r, radius);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        using (var pen = new Pen(line)) g.DrawPath(pen, path);
        Specular(g, r, radius, 0.42f);
    }

    /// <summary>键盘走到的控件外面那圈亮环（鼠标点出来的焦点不描）。</summary>
    public static void FocusRing(Graphics g, RectangleF r, float radius, float amount, Color? color = null)
    {
        if (amount <= 0.004f) return;
        Color c = color ?? Accent;
        for (int i = 2; i >= 1; i--)
        {
            using GraphicsPath p = RoundedRect(RectangleF.Inflate(r, i, i), radius + i);
            using var pen = new Pen(Color.FromArgb(A((i == 1 ? 190 : 70) * amount), c), 1f);
            g.DrawPath(pen, p);
        }
    }
}
