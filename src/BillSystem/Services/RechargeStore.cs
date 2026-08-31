using System.Text;
using System.Text.Json;
using BillSystem.Models;

namespace BillSystem.Services;

/// <summary>
/// 充值记录仓库。跟 <see cref="ReadingStore"/> 一个路子：一行一条 JSON，按 楼栋-房号 分文件。
/// 学校那边本来就存着完整的历史，这份本地副本是为了离线也能看，以及"这次比上次多了几笔"这种判断。
///
/// <b>文件里是正序的</b>（最早的在第一行），跟 readings 那个文件一样；内存里那份反过来排
/// （<see cref="Snapshot"/>），因为界面要的是"最近一笔在最上面"。
/// </summary>
public sealed class RechargeStore
{
    private readonly List<RechargeRecord> _records = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string FilePath { get; }
    public int Building { get; }
    public int Room { get; }

    /// <summary>最近一次从服务器同步成功的时间。</summary>
    public DateTime? LastSync { get; private set; }

    public RechargeStore(int building, int room)
    {
        Building = building;
        Room = room;
        Directory.CreateDirectory(AppConfig.DataDir);
        FilePath = Path.Combine(AppConfig.DataDir, $"recharges-B{building}-R{room}.jsonl");
        Load();
    }

    /// <summary>按支付时间倒序（最近的在前）的全部记录。</summary>
    public List<RechargeRecord> Snapshot()
    {
        lock (_gate) return new List<RechargeRecord>(_records);
    }

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    public RechargeRecord? Latest
    {
        get { lock (_gate) return _records.Count == 0 ? null : _records[0]; }
    }

    /// <summary>本月已充金额（元）。</summary>
    public double MonthTotalYuan()
    {
        DateTime from = new(DateTime.Now.Year, DateTime.Now.Month, 1);
        lock (_gate) return _records.Where(r => r.PayTime >= from).Sum(r => r.Yuan);
    }

    public double TotalYuan()
    {
        lock (_gate) return _records.Sum(r => r.Yuan);
    }

    /// <summary>把服务器那份合并进来，返回本地原先没有的条数。</summary>
    public int Merge(IEnumerable<RechargeRecord> fromServer)
    {
        var fresh = new List<RechargeRecord>();
        lock (_gate)
        {
            foreach (RechargeRecord r in fromServer)
            {
                if (string.IsNullOrEmpty(r.OrderCode) || !_seen.Add(r.OrderCode)) continue;
                _records.Add(r);
                fresh.Add(r);
            }

            if (fresh.Count > 0)
            {
                Sort();
                Save();
            }
            LastSync = DateTime.Now;
        }
        return fresh.Count;
    }

    /// <summary>内存里按支付时间倒序排（最近的在前），界面直接照这个顺序列。</summary>
    private void Sort() => _records.Sort(static (a, b) => b.PayTime.CompareTo(a.PayTime));

    private void Load()
    {
        if (!File.Exists(FilePath)) return;

        int fileLines = 0;
        bool ordered = true;
        DateTime prev = DateTime.MinValue;

        try
        {
            foreach (string line in File.ReadLines(FilePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                fileLines++;
                try
                {
                    var r = JsonSerializer.Deserialize<RechargeRecord>(line);
                    if (r is null || string.IsNullOrEmpty(r.OrderCode)) continue;

                    if (r.PayTime < prev) ordered = false;
                    prev = r.PayTime;

                    if (_seen.Add(r.OrderCode)) _records.Add(r);
                }
                catch (JsonException)
                {
                    // 坏行跳过就行，服务器上还有全量
                }
            }
        }
        catch (IOException)
        {
            return;
        }

        Sort();
        // 早先的版本照服务器给的倒序往后追加，载入时顺手理成正序，重复行和坏行一并清掉
        if (!ordered || fileLines != _records.Count) Save();
    }

    /// <summary>
    /// 把整份记录重写一遍，文件里按支付时间正序。合并进来的可能比现有记录更早（第一次拉全量就是），
    /// 追加写没法保证顺序，索性每次整份落一次——总共一百来行，开销可以忽略。
    /// 先写临时文件再整体替换，中途出事最多丢这一次落盘。
    /// </summary>
    private void Save()
    {
        try
        {
            var sb = new StringBuilder(_records.Count * 180);
            for (int i = _records.Count - 1; i >= 0; i--)      // 内存倒序 → 文件正序
                sb.Append(JsonSerializer.Serialize(_records[i])).Append(Environment.NewLine);

            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // 落盘失败不影响这次运行，学校那边还留着全量
        }
    }
}
