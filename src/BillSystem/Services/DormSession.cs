using BillSystem.Models;

namespace BillSystem.Services;

/// <summary>
/// 一间宿舍在运行期的全套东西：两个仓库（整点读数 / 充值记录）+ 一个后台轮询 + 低电量提醒的状态。
/// 记录几间就来几份，各自记账、各自重试、各自提醒，互不干扰。
/// </summary>
public sealed class DormSession : IDisposable
{
    private bool _syncing;

    public Dorm Dorm { get; }
    public ReadingStore Readings { get; }
    public RechargeStore Recharges { get; }
    public PollService Poll { get; }

    /// <summary>这一轮已经提醒过了，充上电（回到阈值 1.2 倍以上）才会再提醒下一次。</summary>
    public bool LowNotified { get; set; }

    /// <summary>上一封邮件发失败了，别每个整点都弹一次同样的错。</summary>
    public bool MailFailed { get; set; }

    public DormSession(Dorm dorm, ElectricityApi api)
    {
        Dorm = dorm;
        Readings = new ReadingStore(dorm.Building, dorm.Room);
        Recharges = new RechargeStore(dorm.Building, dorm.Room);
        Poll = new PollService(api, Readings);
    }

    public Summary Summarize() => UsageAggregator.Summarize(Readings.Snapshot(), DateTime.Now);

    /// <summary>把学校那边的充值记录合并到本地一份，返回新增了几笔。同一时间只跑一次。</summary>
    public async Task<int> SyncRechargesAsync(RechargeApi api)
    {
        if (_syncing) return 0;
        _syncing = true;
        try
        {
            List<RechargeRecord> list =
                await api.QueryHistoryAsync(Dorm.Building, Dorm.Room).ConfigureAwait(true);
            return Recharges.Merge(list);
        }
        catch (Exception)
        {
            return 0;   // 充值记录拉不到不影响主功能，下次再说
        }
        finally
        {
            _syncing = false;
        }
    }

    public void Dispose() => Poll.Dispose();
}
