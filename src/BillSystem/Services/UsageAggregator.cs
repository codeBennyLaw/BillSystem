using BillSystem.Models;

namespace BillSystem.Services;

/// <summary>一个统计区间（一小时 / 一天 / 一月 / 一年）。</summary>
public sealed class Bucket
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string Label { get; init; } = "";

    /// <summary>该区间用电量（度）。</summary>
    public double Usage { get; set; }

    /// <summary>该区间结束时的剩余电量（度），没有数据则为 null。</summary>
    public double? Remaining { get; set; }

    /// <summary>该区间是否被抄表数据覆盖到。没覆盖的区间画成空白，而不是 0。</summary>
    public bool Covered { get; set; }

    /// <summary>这一格被抄表区间盖到了多少秒，用来判断是不是只盖到半格。</summary>
    public double CoveredSeconds { get; set; }

    /// <summary>这一格充了多少钱（元）。0 就是这一格没充值。</summary>
    public double RechargeYuan { get; set; }

    /// <summary>这一格充进来多少度（金额按电价换算）。</summary>
    public double RechargeKwh { get; set; }

    public bool Recharged => RechargeYuan > 0;

    /// <summary>
    /// 只盖到这一格的一部分：要么这一格还没走完，要么最后一次抄表落在格子中间。
    /// 用电量天然比整格矮一截，图上得标出来，不然看着像"用电突然掉下去了"。
    /// </summary>
    public bool Partial => Covered && CoveredSeconds < (End - Start).TotalSeconds - 1;
}

/// <summary>汇总数字，给顶部几张卡片用。</summary>
public sealed class Summary
{
    public double? Remaining { get; init; }
    public DateTime? MeterTime { get; init; }
    public double? TotalUsed { get; init; }
    public double Today { get; init; }
    public double Yesterday { get; init; }
    public double ThisMonth { get; init; }

    /// <summary>近 7 天日均用电（历史不足 7 天时按实际天数算）。</summary>
    public double? AvgDaily { get; init; }

    /// <summary>算日均时实际用到的天数。</summary>
    public double AvgSpanDays { get; init; }

    public double? DaysLeft { get; init; }
    public DateTime? RunOutDate { get; init; }

    /// <summary>攒了多少次抄表读数（同一次抄表被重复读到只算一次）。</summary>
    public int Points { get; init; }

    /// <summary>
    /// 用电量算不算得出来。增量要两条读数相减才有，只有一条时是"不知道"，
    /// 不是 0——卡片上该写"--"而不是"0.00"。
    /// </summary>
    public bool UsageKnown => Points >= 2;

    /// <summary>"还能用多久"拆成数字和单位两截：不到一天说小时，超过两个月说月。</summary>
    public (string Value, string Unit)? DaysLeftParts => DaysLeft switch
    {
        null => null,
        < 1 => ($"{DaysLeft.Value * 24:0.0}", "小时"),
        < 60 => ($"{DaysLeft.Value:0.0}", "天"),
        _ => ($"{DaysLeft.Value / 30.0:0.0}", "个月"),
    };

    /// <summary>同一件事写成一句话，界面、悬停卡和提醒邮件共用，免得三处写出三种说法。</summary>
    public string? DaysLeftText => DaysLeftParts is { } p ? $"{p.Value} {p.Unit}" : null;

    /// <summary>
    /// 照眼下的日均，剩下的电不到 <paramref name="days"/> 天就用完了。度数还在阈值以上也算紧急：
    /// 电扇空调一开日均能翻几倍，等跌到阈值可能已经半夜断电了。天数 ≤ 0 就是不看这一条。
    /// </summary>
    public bool RunsOutWithin(double days) => days > 0 && DaysLeft is { } d && d < days;
}

/// <summary>
/// 把"累计用电量"的整点快照换算成各时间粒度的用电量。
///
/// 关键一点：<b>用电量摊在两次抄表之间，不是两个整点之间</b>。电表多久上传一次读数是它自己的事
/// （<see cref="Reading.MeterTime"/>，有时一个钟好几次，有时好几个钟都不动），程序却是掐着钟点查的，
/// 中间几次查到的累计值会一模一样。直接拿相邻整点相减就会变成"这个钟 2 度、下个钟 0 度"的锯齿。
/// 所以先按抄表时间把重复的读数收拢掉（<c>Samples</c>），再把两次抄表之间的增量按时间长度
/// <b>按比例摊到</b>它跨过的每一格：小时曲线代表"这段时间的平均功率"，日/月/年的合计完全准确。
/// 整套算法不假设抄表间隔是多少，间隔忽长忽短也算得对。
///
/// 最后一次抄表之后的那几格标成未覆盖（画成空白），只盖到半格的标成 <see cref="Bucket.Partial"/>。
/// </summary>
public static class UsageAggregator
{
    public static DateTime Floor(DateTime t, Granularity g) => g switch
    {
        Granularity.Hour => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0),
        Granularity.Day => new DateTime(t.Year, t.Month, t.Day, 0, 0, 0),
        Granularity.Month => new DateTime(t.Year, t.Month, 1, 0, 0, 0),
        Granularity.Year => new DateTime(t.Year, 1, 1, 0, 0, 0),
        _ => t,
    };

    public static DateTime Step(DateTime start, Granularity g, int n = 1) => g switch
    {
        Granularity.Hour => start.AddHours(n),
        Granularity.Day => start.AddDays(n),
        Granularity.Month => start.AddMonths(n),
        Granularity.Year => start.AddYears(n),
        _ => start,
    };

    public static string Label(DateTime s, Granularity g) => g switch
    {
        Granularity.Hour => s.ToString("HH:mm"),
        Granularity.Day => s.ToString("MM-dd"),
        Granularity.Month => s.ToString("yyyy-MM"),
        _ => s.ToString("yyyy"),
    };

    public static string LongLabel(DateTime s, Granularity g) => g switch
    {
        Granularity.Hour => s.ToString("MM-dd HH:00"),
        Granularity.Day => s.ToString("yyyy-MM-dd"),
        Granularity.Month => s.ToString("yyyy年MM月"),
        _ => s.ToString("yyyy年"),
    };

    public static string UnitName(Granularity g) => g switch
    {
        Granularity.Hour => "小时",
        Granularity.Day => "天",
        Granularity.Month => "月",
        _ => "年",
    };

    /// <summary>默认一屏显示多少个区间。</summary>
    public static int DefaultWindow(Granularity g) => g switch
    {
        Granularity.Hour => 48,
        Granularity.Day => 30,
        Granularity.Month => 12,
        _ => 6,
    };

    /// <summary>一次真正的抄表：时刻取电表自己上传读数的那个时间。</summary>
    private readonly record struct Sample(DateTime At, double Used, double Remaining);

    /// <summary>
    /// 把整点读数收拢成"一次抄表一条"，时刻严格递增。抄表时间没往前走的都并到前一条上
    /// （值取后写的那条，可能被修正过），这样相邻两条之间必定是真的用掉了电。
    /// </summary>
    private static List<Sample> Samples(List<Reading> readings)
    {
        var list = new List<Sample>(readings.Count);
        foreach (Reading r in readings)
        {
            DateTime at = AtOf(r);
            if (list.Count > 0 && at <= list[^1].At)
            {
                list[^1] = new Sample(list[^1].At, r.Used, r.Remaining);
                continue;
            }
            list.Add(new Sample(at, r.Used, r.Remaining));
        }
        return list;
    }

    /// <summary>
    /// 这条读数挂在哪个时刻上：用抄表时间。老数据没这个字段、或它跟采集时间差出好几天
    /// （没解析对）时退回整点，至少不会把整段历史挤到一起。
    /// </summary>
    private static DateTime AtOf(Reading r)
    {
        if (r.MeterTime == default) return r.SlotTime;
        double lag = (r.SlotTime - r.MeterTime).TotalHours;
        return lag is >= -1 and <= 72 ? r.MeterTime : r.SlotTime;
    }

    /// <summary>生成 [from, toExclusive) 区间内的统计桶。from 会先向下对齐到粒度边界。</summary>
    public static List<Bucket> Build(
        List<Reading> readings, Granularity g, DateTime from, DateTime toExclusive,
        IEnumerable<RechargeRecord>? recharges = null)
    {
        from = Floor(from, g);
        var buckets = new List<Bucket>();
        for (var s = from; s < toExclusive; s = Step(s, g))
            buckets.Add(new Bucket { Start = s, End = Step(s, g), Label = Label(s, g) });

        if (buckets.Count == 0 || readings.Count == 0)
            return buckets;

        List<Sample> samples = Samples(readings);
        DateTime rangeEnd = buckets[^1].End;

        for (int i = 1; i < samples.Count; i++)
        {
            Sample a = samples[i - 1], b = samples[i];
            if (b.At <= from || a.At >= rangeEnd) continue;

            double delta = b.Used - a.Used;
            if (delta < 0) delta = 0; // 换表或清零，只标记覆盖，不记用电
            double span = (b.At - a.At).TotalSeconds;
            if (span <= 0) continue;  // Samples 已经保证递增，这里只是兜底

            // 增量落在两次抄表之间 [a.At, b.At)，按时间比例摊给它跨过的每一格
            int k = Math.Max(0, IndexOf(a.At, g, from));
            for (; k < buckets.Count && buckets[k].Start < b.At; k++)
            {
                double ov = Overlap(a.At, b.At, buckets[k].Start, buckets[k].End);
                if (ov <= 0) continue;
                buckets[k].Usage += delta * (ov / span);
                buckets[k].CoveredSeconds += ov;
                buckets[k].Covered = true;
            }
        }

        // 只有一次抄表（或区间里只落进一次）时上面那个成对相减的循环一次都跑不到，图表会整个
        // 空白，所以读数落在哪个区间就算哪个区间"有数据"。已经有覆盖了就不能再补：否则最后
        // 一次抄表所在的区间会凭空多出一个 0，小时曲线右端直接掉到底。
        if (!buckets.Any(b => b.Covered))
        {
            foreach (Sample sm in new[] { samples[0], samples[^1] })
            {
                int i = IndexOf(sm.At, g, from);
                if (i >= 0 && i < buckets.Count) buckets[i].Covered = true;
            }
        }

        // 每个区间末尾的剩余电量：取区间结束那一刻（含）之前最后一次抄表，向后顺延。
        // 含边界是有意的——2:00 那一格的"期末剩余"就是 3:00 那次抄表读到的数。
        int p = 0;
        double? carry = null;
        foreach (var bk in buckets)
        {
            while (p < samples.Count && samples[p].At <= bk.End)
                carry = samples[p++].Remaining;
            bk.Remaining = carry;
        }

        MarkRecharges(buckets, g, from, recharges);
        return buckets;
    }

    /// <summary>
    /// 充值标在"剩余电量涨上去的那一格"上：钱是这个钟付的，但要等电表下次上传读数才看得见，
    /// 柱子跳上去往往是后面某一格的事。月和年粒度直接标付款那一格——那点延迟在这个尺度上看不见，
    /// 往后找反倒会把月底那几笔挪到下个月去。
    /// </summary>
    private static void MarkRecharges(
        List<Bucket> buckets, Granularity g, DateTime from, IEnumerable<RechargeRecord>? recharges)
    {
        if (recharges is null) return;
        bool findJump = g is Granularity.Hour or Granularity.Day;

        foreach (RechargeRecord rc in recharges)
        {
            if (rc.PayCent <= 0) continue;

            int i = IndexOf(rc.PayTime, g, from);
            if (i < 0 || i >= buckets.Count) continue;   // 不在这张表画的范围里

            Bucket mark = buckets[findJump ? JumpBucket(buckets, i, rc.Kwh) : i];
            mark.RechargeYuan += rc.Yuan;
            mark.RechargeKwh += rc.Kwh;
        }
    }

    /// <summary>
    /// 从付款那一格往后找剩余电量涨上去的那一格。<b>按抄表次数找，不按格数找</b>：抄表间隔不固定，
    /// 写死"往后 N 格"会在间隔长的时候把三角丢回付款那一格。剩余电量没变的格子就是还没新读数，
    /// 跳过；只看付款之后头两次抄表——第一次可能正好抄在到账之前。
    /// 知道这一笔<b>该充进来多少度</b>（金额 ÷ 电价），就挑涨幅离它最近的那一格，
    /// 光看"涨了没有"会被半路的小波动骗走。涨幅不要求够数：同一格里用掉的电会先吃掉一部分。
    /// </summary>
    private static int JumpBucket(List<Bucket> buckets, int pay, double kwh)
    {
        int best = -1, reads = 0;
        double bestGap = double.MaxValue;

        double? prev = null;
        for (int j = pay - 1; j >= 0; j--)
            if (buckets[j].Remaining is { } pv) { prev = pv; break; }

        for (int k = pay; k < buckets.Count && reads < 2; k++)
        {
            if (buckets[k].Remaining is not { } cur) continue;
            if (prev is not { } p0) { prev = cur; continue; }   // 这一格就是这张表的开头，没得比
            if (Math.Abs(cur - p0) < 0.005) continue;           // 值没动，这一格没新读数

            reads++;
            prev = cur;
            double gap = Math.Abs(cur - p0 - kwh);
            if (cur > p0 && gap < bestGap) { bestGap = gap; best = k; }
        }

        return best >= 0 ? best : pay;
    }

    /// <summary>任意时间段内的用电量（度），同样摊在两次抄表之间。</summary>
    public static double UsageBetween(List<Reading> readings, DateTime from, DateTime to)
        => UsageBetween(Samples(readings), from, to);

    private static double UsageBetween(List<Sample> samples, DateTime from, DateTime to)
    {
        double sum = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            Sample a = samples[i - 1], b = samples[i];
            double delta = b.Used - a.Used;
            if (delta <= 0) continue;

            double span = (b.At - a.At).TotalSeconds;
            if (span <= 0) continue;

            double ov = Overlap(a.At, b.At, from, to);
            if (ov > 0) sum += delta * (ov / span);
        }
        return sum;
    }

    /// <summary>算出顶部卡片要的那几个数字。</summary>
    public static Summary Summarize(List<Reading> readings, DateTime now)
    {
        if (readings.Count == 0)
            return new Summary { Points = 0 };

        Reading last = readings[^1];
        List<Sample> samples = Samples(readings);
        DateTime today = now.Date;

        // 日均只按"抄到数的那一段"算：把还没抄到的那几个小时也算进分母，日均会一直被压低，
        // "还能用多久"跟着虚高
        DateTime firstAt = samples[0].At;
        DateTime knownTo = samples.Count > 1 ? samples[^1].At : now;
        if (knownTo > now) knownTo = now;

        double spanDays = Math.Min(7.0, (knownTo - firstAt).TotalDays);
        double? avgDaily = null;
        if (spanDays >= 0.5)
            avgDaily = UsageBetween(samples, knownTo.AddDays(-spanDays), knownTo) / spanDays;

        double? daysLeft = null;
        DateTime? runOut = null;
        if (avgDaily is > 0.05 && last.Remaining > 0)
        {
            daysLeft = last.Remaining / avgDaily.Value;
            if (daysLeft < 3650) runOut = now.AddDays(daysLeft.Value);
        }

        return new Summary
        {
            Remaining = last.Remaining,
            MeterTime = last.MeterTime,
            TotalUsed = last.Used,
            Today = UsageBetween(samples, today, now),
            Yesterday = UsageBetween(samples, today.AddDays(-1), today),
            ThisMonth = UsageBetween(samples, new DateTime(now.Year, now.Month, 1), now),
            AvgDaily = avgDaily,
            AvgSpanDays = spanDays,
            DaysLeft = daysLeft,
            RunOutDate = runOut,
            Points = samples.Count,
        };
    }

    private static int IndexOf(DateTime t, Granularity g, DateTime from)
    {
        DateTime f = Floor(t, g);
        return g switch
        {
            Granularity.Hour => (int)Math.Floor((f - from).TotalHours),
            Granularity.Day => (int)Math.Floor((f - from).TotalDays),
            Granularity.Month => (f.Year - from.Year) * 12 + (f.Month - from.Month),
            _ => f.Year - from.Year,
        };
    }

    private static double Overlap(DateTime s1, DateTime e1, DateTime s2, DateTime e2)
    {
        DateTime s = s1 > s2 ? s1 : s2;
        DateTime e = e1 < e2 ? e1 : e2;
        return e > s ? (e - s).TotalSeconds : 0;
    }
}
