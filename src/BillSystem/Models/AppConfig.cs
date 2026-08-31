using System.Text.Json;
using System.Text.Json.Serialization;

namespace BillSystem.Models;

/// <summary>用户配置，存 exe 旁边的 data\config.json。</summary>
public sealed class AppConfig
{
    /// <summary>
    /// 电价：<b>三分之二元一度</b>（0.666…，6 循环），学校定的，界面上改不了。
    /// 写成 <c>2.0 / 3.0</c> 而不是抄几位 0.6666——小数点后截断会让"充 60 元该进来 90 度"
    /// 差出零点几度，柱子和记录里的度数都跟着偏。
    /// </summary>
    public const double PricePerKwh = 2.0 / 3.0;

    /// <summary>钱换成度。</summary>
    public static double KwhOf(double yuan) => yuan / PricePerKwh;

    /// <summary>度换成钱。</summary>
    public static double YuanOf(double kwh) => kwh * PricePerKwh;

    // ---------- 宿舍 ----------

    /// <summary>
    /// 要记录的宿舍，<b>默认空</b>：第一次打开先到设置里把自己的楼栋房号加进来。
    /// 每一间各有自己的一对 jsonl，全部一起轮询，主界面上切换看哪一间。
    /// </summary>
    public List<Dorm> Dorms { get; set; } = new();

    /// <summary>主界面正在看哪一间，存 <see cref="Dorm.Key"/>——按序号存的话，删掉一间就串位了。</summary>
    public string CurrentDorm { get; set; } = "";

    /// <summary>当前那一间；一间都没加时是 null，界面上会提示先去设置里加。</summary>
    [JsonIgnore]
    public Dorm? Current => Dorms.Count == 0
        ? null
        : Dorms.FirstOrDefault(d => d.Key == CurrentDorm) ?? Dorms[0];

    // ---------- 任务栏组件 ----------

    public bool ShowWidget { get; set; } = true;

    /// <summary>组件里多显示一列"今日 / 日均"。</summary>
    public bool WidgetShowExtra { get; set; } = true;

    /// <summary>组件距任务栏左边缘的像素偏移。</summary>
    public int WidgetOffsetX { get; set; } = 4;

    // ---------- 低电量提醒 ----------

    /// <summary>
    /// 发件的 QQ 邮箱地址。<b>所有宿舍共用这一个发件箱</b>（一个授权码只对应一个邮箱），
    /// 什么时候提醒、提醒谁都是按间配的，见 <see cref="Dorm.LowThreshold"/> 和
    /// <see cref="Dorm.MailTo"/>；发件人显示名写的是对应那间的房号。
    /// </summary>
    public string MailFrom { get; set; } = "";

    /// <summary>
    /// QQ 邮箱的 SMTP <b>授权码</b>（不是 QQ 密码，在邮箱设置→账号里单独生成）。
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
    public static string DataDir => _dataDir ??= ResolveDataDir();

    private static string? _dataDir;

    /// <summary>
    /// 自检和出图用：把数据目录挪到一个单独的沙盒，用户真实的 jsonl 和 config.json 一个字都不碰。
    /// 得在任何存储建起来之前调用（<see cref="Program"/> 里那两个开关就是最早的时机）。
    /// </summary>
    internal static void UseSandbox(string dir)
    {
        Directory.CreateDirectory(dir);
        _dataDir = dir;
    }

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

    internal void Normalize()
    {
        // 房号填错、同一间加了两次，都在这儿收拾掉
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Dorms = (Dorms ?? new List<Dorm>()).Where(d => d is not null && d.Valid && seen.Add(d.Key)).ToList();
        foreach (Dorm d in Dorms) d.Normalize();
        CurrentDorm = Current?.Key ?? "";

        WidgetOffsetX = Math.Clamp(WidgetOffsetX, 0, 2000);

        MailFrom = (MailFrom ?? "").Trim();
        MailAuthCode = (MailAuthCode ?? "").Trim();
    }

    /// <summary>设置窗口在副本上改，点保存才写回来。宿舍得整间复制，不然改的还是同一份。</summary>
    public AppConfig Clone()
    {
        var c = (AppConfig)MemberwiseClone();
        c.Dorms = Dorms.Select(d => d.Clone()).ToList();
        return c;
    }

    public void CopyFrom(AppConfig o)
    {
        Dorms = o.Dorms.Select(d => d.Clone()).ToList();
        CurrentDorm = o.CurrentDorm;
        ShowWidget = o.ShowWidget;
        WidgetShowExtra = o.WidgetShowExtra;
        WidgetOffsetX = o.WidgetOffsetX;
        MailFrom = o.MailFrom;
        MailAuthCode = o.MailAuthCode;
        StartWithWindows = o.StartWithWindows;
        Granularity = o.Granularity;
        Normalize();
    }
}
