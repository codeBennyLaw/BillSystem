using System.Globalization;
using System.Text.Json.Serialization;

namespace BillSystem.Models;

/// <summary>
/// 一条充值记录。来自 <c>orderQueryWithPage</c>，也就是网页上"充值记录"那个弹窗里的数据。
/// </summary>
public sealed class RechargeRecord
{
    /// <summary>订单号（学校那边的流水号，用来去重）。</summary>
    public string OrderCode { get; set; } = "";

    /// <summary>支付时间（接口字段 payTime）。</summary>
    public DateTime PayTime { get; set; }

    /// <summary>充值金额，单位分（接口字段 payCent）。</summary>
    public int PayCent { get; set; }

    /// <summary>支付方式：<c>code</c> 扫码，<c>card</c> 学生卡。</summary>
    public string PayMethod { get; set; } = "";

    /// <summary>支付结果，接口直接给的是中文（"已完成"）。</summary>
    public string PayResult { get; set; } = "";

    public int Building { get; set; }

    public int Room { get; set; }

    [JsonIgnore]
    public double Yuan => PayCent / 100.0;

    [JsonIgnore]
    public string MethodLabel => PayMethod == "card" ? "学生卡" : "扫码充值";

    public static string FormatTime(DateTime t) =>
        t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
