using System.Diagnostics;

namespace BillSystem.UI;

/// <summary>
/// 一个会自己平滑追到目标值的量，自绘控件的过渡动画都靠它。
/// 所有 Anim 共用一个 15ms 的计时器，只在真有东西在动的时候才转——界面静止时一点 CPU 都不占。
/// 控件释放了、或者根本没显示出来，就直接就位不再逐帧算。
/// </summary>
internal sealed class Anim
{
    private static readonly List<Anim> Live = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static System.Windows.Forms.Timer? _timer;
    private static double _last;

    private readonly Control _owner;
    private readonly Action<double>? _onFrame;
    private double _from, _to, _t = 1, _ms, _delay;

    public Anim(Control owner, double value = 0, int ms = 150, Action<double>? onFrame = null)
    {
        _owner = owner;
        _onFrame = onFrame;
        Value = _from = _to = value;
        _ms = Math.Max(1, ms);
    }

    public double Value { get; private set; }
    public double Target => _to;

    /// <summary>还在动。数字滚动那种需要"动的时候画中间值"的可以看这个。</summary>
    public bool Running => _t < 1 || _delay > 0;

    public int Ms
    {
        get => (int)_ms;
        set => _ms = Math.Max(1, value);
    }

    /// <summary>
    /// 平滑过渡到新目标。<paramref name="delayMs"/> 给了就先等这么久再起步——
    /// 几张卡片依次错开一点入场，比一起冒出来好看。
    /// </summary>
    public void To(double target, int delayMs = 0)
    {
        if (Math.Abs(target - _to) < 1e-9 && _delay <= 0) return;
        _from = Value;
        _to = target;
        _t = 0;
        _delay = Math.Max(0, delayMs);
        Join(this);
    }

    /// <summary>立刻就位，不做动画（初始化、跟配置同步、内容整体换掉时用）。</summary>
    public void Set(double value)
    {
        Value = _from = _to = value;
        _t = 1;
        _delay = 0;
        _onFrame?.Invoke(Value);
    }

    /// <summary>让所有正在跑的动画立刻就位。出图（--screenshot）时用，免得截到动画的中间帧。</summary>
    public static void FinishAll()
    {
        foreach (Anim a in Live.ToArray()) a.Set(a._to);
        Live.Clear();
        if (_timer is not null) _timer.Enabled = false;
    }

    /// <returns>还要不要继续跑这一项。</returns>
    private bool Step(double dt)
    {
        if (_owner.IsDisposed) return false;

        // 没显示出来就别逐帧画了，直接就位——下次露脸时已经是终值
        if (!_owner.IsHandleCreated || !_owner.Visible)
        {
            Set(_to);
            return false;
        }

        // 排在后面的那几个先等着，等的时候不动也不重画
        if (_delay > 0)
        {
            _delay -= dt;
            return true;
        }

        _t = Math.Min(1, _t + dt / _ms);
        Value = _from + (_to - _from) * Ease(_t);
        _onFrame?.Invoke(Value);
        _owner.Invalidate();
        return _t < 1;
    }

    /// <summary>三次缓出：起步快收尾慢，看着是"滑"过去而不是"跳"过去。</summary>
    private static double Ease(double t)
    {
        double u = 1 - t;
        return 1 - u * u * u;
    }

    private static void Join(Anim a)
    {
        if (!Live.Contains(a)) Live.Add(a);

        if (_timer is null)
        {
            _timer = new System.Windows.Forms.Timer { Interval = 15 };
            _timer.Tick += (_, _) => Tick();
        }
        if (!_timer.Enabled)
        {
            _last = Clock.Elapsed.TotalMilliseconds;
            _timer.Enabled = true;
        }
    }

    private static void Tick()
    {
        double now = Clock.Elapsed.TotalMilliseconds;
        // 卡了一下（比如别的窗口占着 CPU）就当只过了 80ms，别一帧跳到底
        double dt = Math.Clamp(now - _last, 1, 80);
        _last = now;

        for (int i = Live.Count - 1; i >= 0; i--)
            if (!Live[i].Step(dt)) Live.RemoveAt(i);

        if (Live.Count == 0 && _timer is not null) _timer.Enabled = false;
    }
}
