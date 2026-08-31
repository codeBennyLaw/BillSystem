using BillSystem.Models;

namespace BillSystem.Services;

public sealed class PollStatus
{
    public Reading? Latest { get; init; }
    public DateTime? LastSuccess { get; init; }
    public DateTime? LastAttempt { get; init; }
    public string? Error { get; init; }
    public bool Busy { get; init; }
}

/// <summary>
/// 后台轮询：<b>每到整点和半点各查一次</b>，读数都记在那个整点那一格上。
/// 半点查到的值跟这一格里已经有的不一样就顶替掉它，一样就什么都不做。
/// 事件都会切回创建它的线程（UI 线程）上抛，界面里直接用就行。
/// </summary>
public sealed class PollService : IDisposable
{
    private readonly ElectricityApi _api;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _once = new(1, 1);

    /// <summary>整点之间的等待就阻塞在这上面，<see cref="Wake"/> 放一个信号就能立刻醒。</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    private readonly ReadingStore _store;
    private Task? _loop;

    /// <summary>每次查询完成（成功或失败）都会触发。</summary>
    public event Action<PollStatus>? StatusChanged;

    /// <summary>整点读数有变化时触发。</summary>
    public event Action<Reading>? NewReading;

    public PollStatus Status { get; private set; } = new();

    public ReadingStore Store => _store;

    /// <summary>查的是仓库自己那间宿舍：一间一个 <see cref="PollService"/>，各自记账、各自重试。</summary>
    public PollService(ElectricityApi api, ReadingStore store)
    {
        _api = api;
        _store = store;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Status = new PollStatus { Latest = store.Latest, LastSuccess = null };
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>立刻查一次（不等下一个整点 / 半点）。正查着的时候按下也不会丢，查完马上再来一次。</summary>
    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* 已经有一个待处理的唤醒了 */ }
        catch (ObjectDisposedException) { /* 已经退出了 */ }
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            bool ok = false;
            if (await _once.WaitAsync(0).ConfigureAwait(false))
            {
                try { ok = await QueryOnceAsync(_cts.Token).ConfigureAwait(false); }
                finally { _once.Release(); }
            }

            // 失败了就一分钟后再试，别傻等到下一个整点 / 半点
            TimeSpan wait = ok ? NextDelay(DateTime.Now) : TimeSpan.FromMinutes(1);

            try { await _wake.WaitAsync(wait, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 退出 */ }
            catch (ObjectDisposedException) { return; /* 退出时把信号量关了 */ }
        }
    }

    /// <summary>
    /// 下一次查询的时刻：<b>每个整点和每个半点</b>（xx:00:00 / xx:30:00）。
    /// 半点那一次落的是同一格，值一样就当没这回事（<see cref="ReadingStore.TryAdd"/> 返回 false）。
    /// 电表什么时候上传读数说不准，多查这一次不会凭空多出数据点，但一上传就能早半小时看到。
    /// </summary>
    internal static TimeSpan NextDelay(DateTime now)
    {
        DateTime hour = now.Date.AddHours(now.Hour);
        DateTime next = hour.AddMinutes(now.Minute < 30 ? 30 : 60);
        TimeSpan d = next - now;
        return d < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : d;
    }

    private async Task<bool> QueryOnceAsync(CancellationToken ct)
    {
        Status = new PollStatus
        {
            Latest = Status.Latest,
            LastSuccess = Status.LastSuccess,
            LastAttempt = Status.LastAttempt,
            Error = Status.Error,
            Busy = true,
        };
        Raise();

        try
        {
            Reading r = await _api.QueryAsync(_store.Building, _store.Room, ct).ConfigureAwait(false);
            bool isNew = _store.TryAdd(r);

            Status = new PollStatus
            {
                Latest = r,
                LastSuccess = DateTime.Now,
                LastAttempt = DateTime.Now,
                Error = null,
                Busy = false,
            };
            Raise();
            if (isNew) _ui.Post(_ => NewReading?.Invoke(r), null);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Status = new PollStatus
            {
                Latest = Status.Latest ?? _store.Latest,
                LastSuccess = Status.LastSuccess,
                LastAttempt = DateTime.Now,
                Error = ex.Message,
                Busy = false,
            };
            Raise();
            return false;
        }
    }

    private void Raise()
    {
        PollStatus s = Status;
        _ui.Post(_ => StatusChanged?.Invoke(s), null);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Dispose();
        _once.Dispose();
        _cts.Dispose();
    }
}
