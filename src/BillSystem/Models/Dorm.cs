using System.Text.Json.Serialization;

namespace BillSystem.Models;

/// <summary>
/// 一间宿舍：楼栋 + 房号。数据文件名（<c>readings-B43-R422.jsonl</c>）和界面上的房间名都从这儿来。
///
/// 提醒也是按间配的：这间弹不弹 Windows 通知、发不发邮件、发到哪几个邮箱，各自说各自的。
/// 阈值（剩多少度、还能用几天）是所有宿舍共用一套，见 <see cref="AppConfig.LowThreshold"/>。
/// </summary>
public sealed class Dorm
{
    public int Building { get; set; }
    public int Room { get; set; }

    /// <summary>这间低电量时弹一条 Windows 通知。默认关。</summary>
    public bool NotifyEnabled { get; set; }

    /// <summary>这间低电量时发一封邮件。默认关；要开得先填上收件人和共用的发件箱。</summary>
    public bool MailEnabled { get; set; }

    /// <summary>这间的收件人，可以填多个，一封信同时到几边。默认空。</summary>
    public List<string> MailTo { get; set; } = new();

    public Dorm()
    {
    }

    public Dorm(int building, int room)
    {
        Building = building;
        Room = room;
    }

    /// <summary>数据文件名里那一段：<c>B43-R422</c>。配置里也拿它认"当前是哪一间"。</summary>
    [JsonIgnore]
    public string Key => KeyOf(Building, Room);

    public static string KeyOf(int building, int room) => $"B{building}-R{room}";

    /// <summary>主界面上的写法。</summary>
    [JsonIgnore]
    public string Label => $"{Building} 栋 · {Room} 房间";

    /// <summary>通知、邮件、切换器上的短写法。</summary>
    [JsonIgnore]
    public string Short => $"{Building}栋{Room}";

    [JsonIgnore]
    public bool Valid => Building is > 0 and < 10000 && Room is > 0 and < 100000;

    /// <summary>收件人连成一行，界面和邮件正文里显示用。</summary>
    [JsonIgnore]
    public string MailToLine => string.Join("、", MailTo);

    /// <summary>整间复制一份（设置窗口在副本上改，收件人列表得单独复制）。</summary>
    public Dorm Clone() => new(Building, Room)
    {
        NotifyEnabled = NotifyEnabled,
        MailEnabled = MailEnabled,
        MailTo = new List<string>(MailTo),
    };

    /// <summary>
    /// 把提醒那几项抄过来（同一个房号才有意义）。设置点了保存之后，
    /// 已经在后台跑着的那一间靠这个跟上新设置，不用重建、不用重读 jsonl。
    /// </summary>
    public void CopyAlertsFrom(Dorm o)
    {
        NotifyEnabled = o.NotifyEnabled;
        MailEnabled = o.MailEnabled;
        MailTo = new List<string>(o.MailTo);
    }

    /// <summary>收件人去掉空的和重复的。</summary>
    internal void Normalize()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MailTo = (MailTo ?? new List<string>())
            .Select(s => (s ?? "").Trim())
            .Where(s => s.Length > 0 && seen.Add(s))
            .ToList();
    }

    /// <summary>从 <c>B43-R422</c> 认回一间宿舍（数据目录里的文件名就是这个格式）。</summary>
    public static Dorm? Parse(string key)
    {
        if (string.IsNullOrEmpty(key) || key[0] != 'B') return null;
        int dash = key.IndexOf("-R", StringComparison.Ordinal);
        if (dash < 2) return null;

        if (!int.TryParse(key.AsSpan(1, dash - 1), out int b)) return null;
        if (!int.TryParse(key.AsSpan(dash + 2), out int r)) return null;

        var d = new Dorm(b, r);
        return d.Valid ? d : null;
    }

    public override string ToString() => Short;
}
