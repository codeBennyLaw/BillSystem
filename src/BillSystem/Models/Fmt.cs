namespace BillSystem.Models;

/// <summary>界面、提醒和邮件都要写的那几种数：度数折钱、"多久之前"、太长的字截一截。</summary>
public static class Fmt
{
    public static string Money(double kwh) => $"{AppConfig.YuanOf(kwh):0.00} 元";

    /// <summary>"3 小时前"这种。抄表间隔说不准，光看绝对时间不知道这个数新不新。</summary>
    public static string Ago(DateTime t)
    {
        TimeSpan d = DateTime.Now - t;
        if (d < TimeSpan.FromSeconds(45)) return "刚刚";
        if (d < TimeSpan.FromMinutes(60)) return $"{d.TotalMinutes:0} 分钟前";
        if (d < TimeSpan.FromHours(48)) return $"{d.TotalHours:0.#} 小时前";
        return $"{d.TotalDays:0} 天前";
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
