using System.Text;
using System.Text.Json;
using BillSystem.Models;

namespace BillSystem.Services;

/// <summary>
/// 整点读数仓库。一行一条 JSON（JSONL），按 楼栋-房号 分文件存，追加写、启动时全量载入。
///
/// <b>一个整点只留一条</b>：键是 <see cref="Reading.SlotTime"/>。同一个整点再查到就把那条覆盖掉
/// （值真的变了才追加一行，载入时后写的一行赢）；下次启动载入完会把文件收拢成一格一行。
/// 半点那一次查询走的就是这条路——变了就顶替掉整点那条，没变什么都不做。
/// </summary>
public sealed class ReadingStore
{
    private readonly List<Reading> _readings = new();
    private readonly Dictionary<DateTime, int> _bySlot = new();
    private readonly object _gate = new();

    public string FilePath { get; }
    public int Building { get; }
    public int Room { get; }

    /// <summary>载入时跳过的坏行数量。</summary>
    public int SkippedLines { get; private set; }

    public ReadingStore(int building, int room)
    {
        Building = building;
        Room = room;
        Directory.CreateDirectory(AppConfig.DataDir);
        FilePath = Path.Combine(AppConfig.DataDir, $"readings-B{building}-R{room}.jsonl");
        Load();
    }

    /// <summary>按整点升序的全部记录（返回快照，调用方可以随便遍历）。</summary>
    public List<Reading> Snapshot()
    {
        lock (_gate) return new List<Reading>(_readings);
    }

    public int Count
    {
        get { lock (_gate) return _readings.Count; }
    }

    public Reading? Latest
    {
        get { lock (_gate) return _readings.Count == 0 ? null : _readings[^1]; }
    }

    public Reading? Earliest
    {
        get { lock (_gate) return _readings.Count == 0 ? null : _readings[0]; }
    }

    /// <summary>
    /// 写入一条整点读数。这个整点已经有记录了就覆盖它。
    /// 返回 true 表示数据真的变了（新格子，或者同一格里的读数不一样了）——界面据此决定要不要重画。
    /// </summary>
    public bool TryAdd(Reading r)
    {
        lock (_gate)
        {
            if (r.SlotTime == default) r.SlotTime = Reading.SlotOf(r.FetchedAt == default ? r.MeterTime : r.FetchedAt);

            if (_bySlot.TryGetValue(r.SlotTime, out int at))
            {
                Reading old = _readings[at];
                bool same = old.Used.Equals(r.Used)
                            && old.Remaining.Equals(r.Remaining)
                            && old.MeterTime == r.MeterTime;
                _readings[at] = r;
                // 跟这一格里已经有的一模一样（电表还没上传新读数）：不用再写一行。这是文件里重复行最主要的来源
                if (same) return false;

                Append(r);          // 文件只追加，载入时这一行会覆盖前面那条
                return true;
            }

            _bySlot[r.SlotTime] = _readings.Count;
            _readings.Add(r);
            // 正常都是递增的，偶尔乱序时兜一下
            if (_readings.Count > 1 && _readings[^2].SlotTime > r.SlotTime) Resort();

            Append(r);
            return true;
        }
    }

    /// <summary>重排后索引全废，整个重建一遍（很少走到）。</summary>
    private void Resort()
    {
        _readings.Sort(static (a, b) => a.SlotTime.CompareTo(b.SlotTime));
        _bySlot.Clear();
        for (int i = 0; i < _readings.Count; i++) _bySlot[_readings[i].SlotTime] = i;
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;

        int fileLines = 0;
        try
        {
            foreach (string line in File.ReadLines(FilePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                fileLines++;
                try
                {
                    var r = JsonSerializer.Deserialize<Reading>(line);
                    if (r is null || r.MeterTime == default) { SkippedLines++; continue; }

                    // 整点一律按采集时间现算：老版本按"最近的整点"盖章，23:42 查到的会被记到
                    // 0 点那格去，重新算一遍就回到 23 点。没有采集时间的老数据退回抄表时间。
                    r.SlotTime = Reading.SlotOf(r.FetchedAt == default ? r.MeterTime : r.FetchedAt);

                    if (_bySlot.TryGetValue(r.SlotTime, out int at)) _readings[at] = r;   // 后写的赢
                    else
                    {
                        _bySlot[r.SlotTime] = _readings.Count;
                        _readings.Add(r);
                    }
                }
                catch (JsonException)
                {
                    SkippedLines++;
                }
            }
        }
        catch (IOException)
        {
            // 文件被占用之类，当作没有历史，别炸
            return;
        }

        Resort();
        Compact(fileLines);
    }

    /// <summary>
    /// 载入完顺手把文件收拢成"一格一行"，顺带清掉跳过的坏行——只追加的文件行数会慢慢多于
    /// 图表上的格子数，自己翻 jsonl 的时候很容易看糊。
    /// 先写临时文件再整体替换，中途出事最多丢这一次收拢。
    /// </summary>
    private void Compact(int fileLines)
    {
        if (fileLines <= _readings.Count) return;

        try
        {
            var sb = new StringBuilder(_readings.Count * 180);
            foreach (Reading r in _readings)
                sb.Append(JsonSerializer.Serialize(r)).Append(Environment.NewLine);

            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // 收拢只是让文件好看，失败了照旧用内存里那份，不影响任何功能
        }
    }

    private void Append(Reading r)
    {
        try
        {
            File.AppendAllText(FilePath, JsonSerializer.Serialize(r) + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException)
        {
            // 落盘失败不影响内存里的数据和界面
        }
    }
}
