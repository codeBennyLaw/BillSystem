using System.Text.Json;
using System.Text.Json.Serialization;

namespace BillSystem.Models;

/// <summary>用户配置，存 exe 旁边的 data\config.json。</summary>
public sealed class AppConfig
{
    /// <summary>写死的宿舍：43 栋 422。整个程序只服务这一间，所以没有对应的设置项。</summary>
    public const int FixedBuilding = 43;
    public const int FixedRoom = 422;

    [JsonIgnore]
    public int Building => FixedBuilding;

    [JsonIgnore]
    public int Room => FixedRoom;

    public bool ShowWidget { get; set; } = true;

    /// <summary>组件里多显示一列"今日 / 日均"。</summary>
    public bool WidgetShowExtra { get; set; } = true;

    /// <summary>组件距任务栏左边缘的像素偏移。</summary>
    public int WidgetOffsetX { get; set; } = 4;

    public bool LowAlertEnabled { get; set; } = true;
    public double LowThreshold { get; set; } = 10;

    /// <summary>
    /// 另一条提醒线：照眼下的日均算，预计可用低于这么多天就提醒（默认半天）。
    /// 度数还在阈值以上也发——空调一开日均能翻几倍，等跌到阈值可能已经半夜断电了。
    /// 调到 0 就是不看这一条，只看度数阈值。
    /// </summary>
    public double LowDaysThreshold { get; set; } = 0.5;

    // ---------- 低电量邮件提醒（QQ 邮箱 SMTP） ----------

    /// <summary>发件地址写死：就用这个 QQ 邮箱发，界面上改不了。</summary>
    public const string FixedMailFrom = "1018273986@qq.com";

    /// <summary>收件地址也写死，两个都收同一封（抄送不分主次，都放在收件人里）。</summary>
    public static readonly string[] FixedMailTo =
    {
        "alilexiwalker@wyu.edu.cn",
        "3124002500@wyu.edu.cn",
    };

    [JsonIgnore]
    public string MailFrom => FixedMailFrom;

    [JsonIgnore]
    public IReadOnlyList<string> MailTo => FixedMailTo;

    /// <summary>收件人连成一行，界面和邮件正文里显示用。</summary>
    public static string MailToLine => string.Join("、", FixedMailTo);

    /// <summary>
    /// QQ 邮箱的 SMTP <b>授权码</b>（不是 QQ 密码，在邮箱设置→账号里单独生成）。
    /// 填了就发邮件，没填就只发系统通知——所以邮件提醒没有单独的开关。
    /// 明文存在 config.json 里——本程序不引第三方包，也没有可用的本机加密接口。
    /// </summary>
    public string MailAuthCode { get; set; } = "";

    public bool StartWithWindows { get; set; }

    public Granularity Granularity { get; set; } = Granularity.Day;

    // ---------- 持久化 ----------

    /// <summary>
    /// 数据目录：优先放在 exe 旁边的 <c>data\</c>（绿色版，拷走就能带走历史）；
    /// 装在没有写权限的目录里时退回 %APPDATA%\BillSystem。
    /// </summary>
    [JsonIgnore]
    public static string DataDir { get; } = ResolveDataDir();

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(DataDir, "config.json");

    private static string ResolveDataDir()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "data");
        if (TryPrepare(local)) return local;

        string roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BillSystem");
        try { Directory.CreateDirectory(roaming); } catch { /* 实在不行就只在内存里跑 */ }
        return roaming;
    }

    private static bool TryPrepare(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".writetest");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    // 顺手写回去：新版本加的字段（老文件里没有，走的是默认值）和被 Normalize
                    // 夹回范围的值都落进文件，自己翻 config.json 时看到的就是程序真在用的那份
                    cfg.Save();
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置坏了就用默认值，不该因此打不开程序
        }

        var fresh = new AppConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 写不进去也不影响本次运行
        }
    }

    private void Normalize()
    {
        WidgetOffsetX = Math.Clamp(WidgetOffsetX, 0, 2000);
        LowThreshold = Math.Clamp(LowThreshold, 0, 1000);
        LowDaysThreshold = Math.Clamp(LowDaysThreshold, 0, 30);
        MailAuthCode = (MailAuthCode ?? "").Trim();
    }

    /// <summary>设置窗口在副本上改，点保存才写回来。</summary>
    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    public void CopyFrom(AppConfig o)
    {
        ShowWidget = o.ShowWidget;
        WidgetShowExtra = o.WidgetShowExtra;
        WidgetOffsetX = o.WidgetOffsetX;
        LowAlertEnabled = o.LowAlertEnabled;
        LowThreshold = o.LowThreshold;
        LowDaysThreshold = o.LowDaysThreshold;
        MailAuthCode = o.MailAuthCode;
        StartWithWindows = o.StartWithWindows;
        Granularity = o.Granularity;
    }
}
