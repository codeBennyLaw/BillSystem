using System.Globalization;
using System.Text.Json.Serialization;

namespace BillSystem.Models;

/// <summary>
/// 一次整点读数。学校接口只返回"当前快照"，历史曲线由本程序<b>每到整点和半点各采一次</b>拼出来；
/// 半点那一次落在同一格上，值变了就顶替掉整点那条（见 <see cref="SlotOf"/>）。
/// </summary>
public sealed class Reading
{
    /// <summary>
    /// 这条读数算在哪个整点上（本机时间，一定是 xx:00:00）。
    /// 一个整点只留一条，两条相邻读数的差值就是它们之间那一个小时的用电量——
    /// 也就是说 3:00 那条读数减去 2:00 那条，记在<b>两点</b>这一格里。
    /// </summary>
    public DateTime SlotTime { get; set; }

    /// <summary>抄表时间（接口字段 updatedt），网页上显示的那个时间。</summary>
    public DateTime MeterTime { get; set; }

    /// <summary>本程序抓到这条记录的本机时间。</summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>累计用电量（接口字段 usedamp，单调递增，单位度）。</summary>
    public double Used { get; set; }

    /// <summary>剩余电量（接口字段 resamp，单位度）。</summary>
    public double Remaining { get; set; }

    public int Building { get; set; }

    public int Room { get; set; }

    [JsonIgnore]
    public string RoomLabel => $"{Building}栋 {Room}";

    /// <summary>抄表时间距今多久。用来判断数据是否已经很旧。</summary>
    [JsonIgnore]
    public TimeSpan Age => DateTime.Now - MeterTime;

    /// <summary>
    /// 这个时刻算哪个整点。<b>基本是向下取整</b>：只有差不到 5 分钟就到下一个整点了才算下一格。
    ///
    /// 整点轮询偶尔会早几十毫秒，2:59:59.98 查到的那条得算 3:00 的读数，不然会覆盖 2:00 那格；
    /// 半点那一次（3:30）和程序刚启动时那一次（23:42）落在任意一分钟上，都该记在<b>本整点</b>——
    /// 要是按"最近的整点"算就借用了还没到的下一格，等下一个整点真的查一次又把它盖掉，
    /// 本该有数的那格反倒空着。
    /// </summary>
    public static DateTime SlotOf(DateTime t)
    {
        var hour = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0);
        return t - hour >= TimeSpan.FromMinutes(55) ? hour.AddHours(1) : hour;
    }

    public static string FormatTime(DateTime t) =>
        t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
