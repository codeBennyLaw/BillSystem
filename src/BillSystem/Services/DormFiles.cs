using BillSystem.Models;

namespace BillSystem.Services;

/// <summary>
/// 数据目录里属于某一间宿舍的那几个 jsonl。设置里的"数据"页拿它列出<b>已经不在记录名单里</b>
/// 的那些历史文件——删掉宿舍不会顺手删数据（免得手一抖就没了），要清理在那儿手动来。
/// </summary>
public sealed class DormFiles
{
    public required Dorm Dorm { get; init; }
    public List<string> Paths { get; } = new();
    public long Bytes { get; set; }
    public int ReadingLines { get; set; }
    public int RechargeLines { get; set; }

    /// <summary>"312 条读数 · 12 笔充值 · 56 KB"。</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>(3);
            if (ReadingLines > 0) parts.Add($"{ReadingLines} 条读数");
            if (RechargeLines > 0) parts.Add($"{RechargeLines} 笔充值");
            parts.Add(Bytes >= 1024 ? $"{Bytes / 1024.0:0.#} KB" : $"{Bytes} 字节");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>数据目录里所有能认出房号的 jsonl，按宿舍归拢。</summary>
    public static List<DormFiles> Scan()
    {
        var found = new Dictionary<string, DormFiles>(StringComparer.Ordinal);
        try
        {
            foreach (string path in Directory.EnumerateFiles(AppConfig.DataDir, "*.jsonl"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                bool reading = name.StartsWith("readings-", StringComparison.Ordinal);
                bool recharge = name.StartsWith("recharges-", StringComparison.Ordinal);
                if (!reading && !recharge) continue;

                if (Dorm.Parse(name[(name.IndexOf('-') + 1)..]) is not { } dorm) continue;

                if (!found.TryGetValue(dorm.Key, out DormFiles? f))
                    found[dorm.Key] = f = new DormFiles { Dorm = dorm };

                f.Paths.Add(path);
                int lines = 0;
                try
                {
                    f.Bytes += new FileInfo(path).Length;
                    foreach (string line in File.ReadLines(path))
                        if (line.Length > 0) lines++;
                }
                catch (IOException)
                {
                    // 文件被占着就只算不进行数，照样列出来
                }

                if (reading) f.ReadingLines += lines;
                else f.RechargeLines += lines;
            }
        }
        catch (Exception)
        {
            // 目录读不了就当没有
        }

        return found.Values.OrderBy(f => f.Dorm.Building).ThenBy(f => f.Dorm.Room).ToList();
    }

    /// <summary>没在记录名单里的那些（删过的宿舍，或者从别的机器拷过来的）。</summary>
    public static List<DormFiles> Orphans(AppConfig cfg)
    {
        var kept = new HashSet<string>(cfg.Dorms.Select(d => d.Key), StringComparer.Ordinal);
        return Scan().Where(f => !kept.Contains(f.Dorm.Key)).ToList();
    }

    /// <summary>把这一间的文件删掉。失败原因原样带出来，界面上照实说。</summary>
    public bool TryDelete(out string? error)
    {
        foreach (string path in Paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>在资源管理器里打开数据目录。打不开就算了，不值得为这个弹个框。</summary>
    public static void OpenDataDir()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"\"{AppConfig.DataDir}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
