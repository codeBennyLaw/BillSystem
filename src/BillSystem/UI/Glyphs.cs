using System.Drawing.Drawing2D;
using BillSystem.Interop;

namespace BillSystem.UI;

/// <summary>闪电图形 + 托盘图标生成。自己画，省得带图片资源。</summary>
internal static class Glyphs
{
    // 归一化到 1x1 方框里的闪电轮廓
    private static readonly PointF[] Bolt =
    {
        new(0.52f, 0.00f),
        new(0.08f, 0.60f),
        new(0.38f, 0.60f),
        new(0.30f, 1.00f),
        new(0.92f, 0.40f),
        new(0.60f, 0.40f),
        new(0.84f, 0.00f),
    };

    public static void DrawBolt(Graphics g, RectangleF box, Color fill, Color? outline = null)
    {
        var pts = new PointF[Bolt.Length];
        for (int i = 0; i < Bolt.Length; i++)
            pts[i] = new PointF(box.X + Bolt[i].X * box.Width, box.Y + Bolt[i].Y * box.Height);

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(fill))
            g.FillPolygon(brush, pts);

        if (outline is { } oc)
        {
            using var pen = new Pen(oc, Math.Max(1f, box.Width / 16f));
            g.DrawPolygon(pen, pts);
        }

        g.SmoothingMode = old;
    }

    /// <summary>
    /// 生成托盘图标。返回的 Icon 用完必须调 <see cref="DestroyIcon"/>，
    /// 因为它是从 GDI 句柄包出来的。
    /// </summary>
    public static Icon CreateTrayIcon(Color fill)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            // 深色描边，浅色任务栏上也看得见
            DrawBolt(g, new RectangleF(4, 2, 24, 28), fill, Color.FromArgb(120, 0, 0, 0));
        }

        IntPtr h = bmp.GetHicon();
        return Icon.FromHandle(h);
    }

    public static void DestroyIcon(Icon? icon)
    {
        if (icon is null) return;
        IntPtr h = icon.Handle;
        icon.Dispose();
        Win32.DestroyIcon(h);
    }
}
