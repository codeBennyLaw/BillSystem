namespace BillSystem.UI;

/// <summary>
/// 窗口淡入。弹窗"啪"地砸出来挺硬的，淡进来舒服些。
///
/// 主窗口不用这个：它开着 <c>WS_EX_COMPOSITED</c>，再叠一层半透明只会更慢。
/// 起点不从 0 开始——万一动画一帧都没跑（出图时会被 <see cref="Anim.FinishAll"/> 掐掉），
/// 窗口也还是看得见的，不会变成一块透明。
/// </summary>
internal static class Fade
{
    private const double From = 0.35;

    /// <summary>让这个窗口每次露脸时淡入一次。构造函数里挂一下就行。</summary>
    public static void In(Form f, int ms = 160)
    {
        Anim? anim = null;

        f.VisibleChanged += (_, _) =>
        {
            if (!f.Visible || !f.IsHandleCreated || f.IsDisposed) return;
            anim ??= new Anim(f, 1, ms, v =>
            {
                if (!f.IsDisposed) f.Opacity = Math.Clamp(v, 0, 1);
            });
            anim.Set(From);
            anim.To(1);
        };

        // 关掉时收回全不透明：同一个实例反复用（充值窗口就是），下次别停在半透明上
        f.FormClosed += (_, _) => anim?.Set(1);
    }
}
