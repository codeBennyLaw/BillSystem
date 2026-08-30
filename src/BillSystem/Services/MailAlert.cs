using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using BillSystem.Models;

namespace BillSystem.Services;

/// <summary>
/// 低电量邮件提醒，走 QQ 邮箱的 SMTP。
///
/// smtp.qq.com:587 + STARTTLS，用户名是那个 QQ 邮箱地址，密码是邮箱设置→账号里生成的
/// <b>授权码</b>（16 位字母），不是 QQ 密码。授权码没填就直接跳过，不当成错误。
///
/// 信发成 multipart/alternative：HTML 那份在手机上一眼看到剩多少、还能用多久，
/// 纯文本那份保证任何客户端（和通知栏预览）都读得出来。
///
/// 用的是 <see cref="SmtpClient"/>：.NET 把它标了 obsolete（SYSLIB0014 只针对
/// WebRequest 那一套，SmtpClient 是 <c>[Obsolete]</c> 的建议性警告），但本程序不引第三方包，
/// 框架里也只有这一个能发信的东西，所以这里显式压掉那条警告。
/// </summary>
internal static class MailAlert
{
    private const string Host = "smtp.qq.com";
    private const int Port = 587;

    // 各家邮件客户端的深色模式行为都不一样，配色一律按浅底写死，不指望 prefers-color-scheme
    private const string Ink = "#22262c";
    private const string Sub = "#8a9099";
    private const string Hair = "#f0f1f4";
    private const string Edge = "#e9ebef";
    private const string Danger = "#d94f3d";   // 度数已经低于阈值
    private const string Warn = "#c8871a";     // 度数还够，但照这个用法快见底了

    private const string Sans =
        "-apple-system,BlinkMacSystemFont,'Segoe UI','PingFang SC','Microsoft YaHei',sans-serif";

    /// <summary>只看授权码：收发地址是写死的，填了授权码就发得出去，没填就只发系统通知。</summary>
    public static bool Configured(AppConfig cfg) => !string.IsNullOrWhiteSpace(cfg.MailAuthCode);

    /// <summary>一封信的三件套。自检里直接查这几段文字，不用真发出去。</summary>
    internal readonly record struct Letter(string Subject, string Text, string Html, string FromName);

    /// <summary>
    /// 标题和发件人显示名。手机上弹的邮件通知卡只给这两行：标题喊那句话，发件人报是哪个房间，
    /// 不用点开就知道是什么事、是谁的电快没了。真的那封和"试一封"用同一套，
    /// 写死在程序里，设置里没有对应项。
    /// </summary>
    private const string Subject = "电量告急！将军救急！";

    private static string RoomName(int building, int room) => $"{building}栋{room}";

    /// <summary>
    /// 低电量那封。<paramref name="belowThreshold"/> 区分两种触发：度数低于阈值，
    /// 还是度数还够但照眼下的日均撑不到设置里那个天数——开头那句话跟着变。
    /// </summary>
    public static Task SendLowAsync(
        AppConfig cfg, Reading r, Summary? s = null, bool belowThreshold = true)
        => SendAsync(cfg, LowLetter(cfg, r, s, belowThreshold));

    /// <summary>设置里"试一封"用的。带上眼下的数字，跟真的那封长一个样。</summary>
    public static Task SendTestAsync(AppConfig cfg, Summary? s = null)
        => SendAsync(cfg, TestLetter(cfg, s));

    internal static Letter LowLetter(AppConfig cfg, Reading r, Summary? s, bool belowThreshold)
    {
        string room = RoomName(r.Building, r.Room);
        string? left = s?.DaysLeftText;

        string lead = belowThreshold
            ? $"{room} 的剩余电量已经低于 {cfg.LowThreshold:0.##} 度，记得充电费。"
            : $"{room} 的电照现在的用法撑不到 {cfg.LowDaysThreshold:0.##} 天了，记得充电费。";

        var rows = new List<(string, string)> { ("剩余电量", $"{r.Remaining:0.00} 度") };
        Forecast(rows, s);
        rows.Add(("累计用电", $"{r.Used:0.00} 度"));
        rows.Add(("抄表时间", $"{r.MeterTime:MM-dd HH:mm}（{Ago(r.MeterTime)}）"));

        string[] notes =
        {
            "充电费：打开“宿舍电费助手”→ 充值，选好金额生成付款码，微信扫一下就行。",
            cfg.LowDaysThreshold > 0
                ? $"提醒条件：剩余低于 {cfg.LowThreshold:0.##} 度，或预计可用不足 {cfg.LowDaysThreshold:0.##} 天。"
                : $"提醒条件：剩余低于 {cfg.LowThreshold:0.##} 度。",
        };

        return new Letter(
            Subject,
            PlainText(lead, rows, notes),
            HtmlBody(room, $"{r.Remaining:0.00}", left is null ? null : $"照现在的用法约还能用 {left}",
                lead, rows, notes, belowThreshold ? Danger : Warn),
            room);
    }

    internal static Letter TestLetter(AppConfig cfg, Summary? s)
    {
        string room = RoomName(AppConfig.FixedBuilding, AppConfig.FixedRoom);
        const string lead = "这是一封测试邮件，能收到就说明低电量提醒发得出去。";

        var rows = new List<(string, string)>
        {
            ("剩余电量", s?.Remaining is { } rm ? $"{rm:0.00} 度" : "还没查到"),
        };
        Forecast(rows, s);
        rows.Add(("发件", cfg.MailFrom));
        rows.Add(("收件", AppConfig.MailToLine));
        rows.Add(("时间", $"{DateTime.Now:MM-dd HH:mm}"));

        string[] notes =
        {
            cfg.LowDaysThreshold > 0
                ? $"真的那封会在剩余低于 {cfg.LowThreshold:0.##} 度、或预计可用不足 {cfg.LowDaysThreshold:0.##} 天时发出。"
                : $"真的那封会在剩余低于 {cfg.LowThreshold:0.##} 度时发出。",
        };

        return new Letter(
            Subject,
            PlainText(lead, rows, notes),
            HtmlBody(room, s?.Remaining is { } r2 ? $"{r2:0.00}" : "--",
                s?.DaysLeftText is { } t ? $"照现在的用法约还能用 {t}" : null,
                lead, rows, notes, Sub),
            room);
    }

    /// <summary>
    /// 预计可用 / 日均 / 今日 / 本月这几行。历史太短算不出来的就整行不写——
    /// 宁可少一行，也别摆个"—"让人以为程序坏了。
    /// </summary>
    private static void Forecast(List<(string, string)> rows, Summary? s)
    {
        if (s is null) return;

        if (s.DaysLeftText is { } left)
            rows.Add(("预计可用", s.RunOutDate is { } end
                ? $"约 {left}（{end:MM-dd HH:mm} 前后用完）"
                : $"约 {left}"));
        if (s.AvgDaily is > 0)
            rows.Add(("日均用电", $"{s.AvgDaily:0.00} 度（近 {s.AvgSpanDays:0.#} 天）"));
        if (s.Today > 0) rows.Add(("今日用电", $"{s.Today:0.00} 度"));
        if (s.ThisMonth > 0) rows.Add(("本月用电", $"{s.ThisMonth:0.00} 度"));
    }

    /// <summary>"3 小时前"这种。学校两三个钟才抄一次表，光看绝对时间不知道这个数新不新。</summary>
    private static string Ago(DateTime t)
    {
        TimeSpan d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalMinutes < 60) return $"{d.TotalMinutes:0} 分钟前";
        if (d.TotalHours < 48) return $"{d.TotalHours:0.#} 小时前";
        return $"{d.TotalDays:0} 天前";
    }

    /// <summary>纯文本那份：开头一句话 + 一行一个数 + 末尾几句说明。</summary>
    private static string PlainText(
        string lead, List<(string Label, string Value)> rows, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder(420);
        sb.AppendLine(lead).AppendLine();
        foreach ((string label, string value) in rows) sb.AppendLine($"{label}：{value}");
        sb.AppendLine();
        foreach (string n in notes) sb.AppendLine(n);
        sb.AppendLine().Append("—— 宿舍电费助手（BillSystem）自动发送");
        return sb.ToString();
    }

    /// <summary>
    /// HTML 那份：一张白卡片，最上面是剩余度数（低于阈值是红的，"快见底"是黄的），
    /// 底下是一行一个数的表格，末尾一小段说明。
    ///
    /// 全部用 table + 行内样式：邮件客户端普遍不认 <c>&lt;style&gt;</c>、flex 和 CSS 变量。
    /// </summary>
    private static string HtmlBody(
        string room, string big, string? sub, string lead,
        List<(string Label, string Value)> rows, IReadOnlyList<string> notes, string accent)
    {
        var sb = new StringBuilder(2200);
        sb.Append($"<div style=\"margin:0;padding:20px 12px;background:#f4f5f7;font-family:{Sans}\">")
          .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" ")
          .Append($"style=\"max-width:440px;margin:0 auto;background:#ffffff;border:1px solid {Edge};border-radius:14px\">")
          .Append("<tr><td style=\"padding:22px 24px 18px\">")
          .Append($"<div style=\"font-size:12px;letter-spacing:1px;color:{Sub}\">宿舍电费助手 · {E(room)}</div>")
          .Append($"<div style=\"margin:12px 0 0;font-size:38px;line-height:1;font-weight:600;color:{accent}\">{E(big)}")
          .Append($"<span style=\"margin-left:6px;font-size:15px;font-weight:400;color:{Sub}\">度</span></div>");

        if (sub is not null)
            sb.Append($"<div style=\"margin:8px 0 0;font-size:14px;color:{Ink}\">{E(sub)}</div>");

        sb.Append($"<div style=\"margin:12px 0 0;font-size:13px;line-height:1.7;color:{Sub}\">{E(lead)}</div>")
          .Append("</td></tr>")
          .Append("<tr><td style=\"padding:0 24px\">")
          .Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\">");

        foreach ((string label, string value) in rows)
            sb.Append($"<tr><td style=\"padding:8px 0;border-top:1px solid {Hair};font-size:13px;color:{Sub};white-space:nowrap\">")
              .Append(E(label))
              .Append($"</td><td style=\"padding:8px 0;border-top:1px solid {Hair};font-size:13px;color:{Ink};text-align:right\">")
              .Append(E(value))
              .Append("</td></tr>");

        sb.Append("</table></td></tr>")
          .Append($"<tr><td style=\"padding:16px 24px 20px\"><div style=\"padding-top:14px;border-top:1px solid {Edge};font-size:12px;line-height:1.9;color:{Sub}\">");
        foreach (string n in notes) sb.Append(E(n)).Append("<br>");
        sb.Append("</div></td></tr></table>")
          .Append($"<div style=\"max-width:440px;margin:12px auto 0;font-size:11px;text-align:center;color:#a8adb5\">")
          .Append("宿舍电费助手（BillSystem）自动发送</div></div>");

        return sb.ToString();
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

#pragma warning disable SYSLIB0014
    private static async Task SendAsync(AppConfig cfg, Letter letter)
    {
        string from = cfg.MailFrom;
        string code = cfg.MailAuthCode.Trim();

        if (code.Length == 0)
            throw new InvalidOperationException(
                "还没填 QQ 邮箱授权码。到邮箱设置→账号里开启 SMTP 服务，生成的那串 16 位字母就是。");

        using var client = new SmtpClient(Host, Port)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(from, code),
            Timeout = 20000,
        };

        using var msg = new MailMessage
        {
            From = new MailAddress(from, letter.FromName, Encoding.UTF8),
            Subject = letter.Subject,
            SubjectEncoding = Encoding.UTF8,
        };
        // 两份都挂成 AlternateView（Body 留空）就是标准的 multipart/alternative：
        // 客户端认 HTML 就显示卡片，不认就退回纯文本，顺序是从素到花
        msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            letter.Text, Encoding.UTF8, MediaTypeNames.Text.Plain));
        msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            letter.Html, Encoding.UTF8, MediaTypeNames.Text.Html));

        // 两个收件人都放在收件人里，一封信同时到两边
        foreach (string to in cfg.MailTo) msg.To.Add(new MailAddress(to));

        try
        {
            await client.SendMailAsync(msg).ConfigureAwait(false);
        }
        catch (SmtpException ex)
        {
            // 最常见的两种：授权码不对（535），或者没连着网
            throw new InvalidOperationException(
                ex.StatusCode == SmtpStatusCode.ClientNotPermitted || ex.StatusCode == SmtpStatusCode.MustIssueStartTlsFirst
                    ? $"QQ 邮箱拒绝登录（{ex.StatusCode}）：授权码是不是填错了？"
                    : $"发信失败：{ex.Message}", ex);
        }
    }
#pragma warning restore SYSLIB0014
}
