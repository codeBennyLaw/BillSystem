using System.Text;
using System.Text.Json;
using BillSystem.Models;
using BillSystem.Services;

namespace BillSystem.Dev;

/// <summary>
/// 开发用自检：<c>BillSystem.exe --selftest</c>，结果写到 %TEMP%\billsystem-selftest.txt。
/// 校验的是"累计读数 → 各粒度用电量"这段换算，出错了图表就全是错的。
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var log = new StringBuilder();
        int failed = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failed++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  → " + detail : "")}");
        }

        var t0 = new DateTime(2026, 8, 20, 6, 0, 0);
        var readings = new List<Reading>
        {
            R(t0, used: 100, remain: 60),
            R(t0.AddHours(6), used: 106, remain: 54),   // 6 度 / 6 小时
            R(t0.AddHours(30), used: 130, remain: 30),  // 24 度 / 24 小时
        };

        List<Bucket> hours = UsageAggregator.Build(readings, Granularity.Hour, t0, t0.AddHours(30));
        Check("小时数=30", hours.Count == 30, hours.Count.ToString());
        Check("前 6 小时各 1.00 度", hours.Take(6).All(b => Math.Abs(b.Usage - 1.0) < 1e-9),
            string.Join(",", hours.Take(6).Select(b => b.Usage.ToString("0.###"))));
        Check("后 24 小时各 1.00 度", hours.Skip(6).All(b => Math.Abs(b.Usage - 1.0) < 1e-9));
        Check("小时合计=30 度", Math.Abs(hours.Sum(b => b.Usage) - 30) < 1e-9,
            hours.Sum(b => b.Usage).ToString("0.###"));
        Check("每个小时都算已覆盖", hours.All(b => b.Covered));

        List<Bucket> days = UsageAggregator.Build(readings, Granularity.Day, t0, t0.AddHours(30));
        Check("天数=2", days.Count == 2, days.Count.ToString());
        Check("8-20 用电=18 度", Math.Abs(days[0].Usage - 18) < 1e-9, days[0].Usage.ToString("0.###"));
        Check("8-21 用电=12 度", Math.Abs(days[1].Usage - 12) < 1e-9, days[1].Usage.ToString("0.###"));
        Check("日合计=小时合计", Math.Abs(days.Sum(b => b.Usage) - hours.Sum(b => b.Usage)) < 1e-9);
        Check("剩余电量顺延到区间末", days[1].Remaining is { } rr && Math.Abs(rr - 30) < 1e-9);

        Check("UsageBetween 全区间=30",
            Math.Abs(UsageAggregator.UsageBetween(readings, t0, t0.AddHours(30)) - 30) < 1e-9);
        Check("UsageBetween 半区间=3",
            Math.Abs(UsageAggregator.UsageBetween(readings, t0, t0.AddHours(3)) - 3) < 1e-9);

        // 换表/清零：累计读数变小
        var reset = new List<Reading> { R(t0, 100, 20), R(t0.AddHours(2), 5, 200) };
        Check("换表不产生负数用电",
            UsageAggregator.Build(reset, Granularity.Hour, t0, t0.AddHours(2)).All(b => b.Usage == 0));

        List<Bucket> gap = UsageAggregator.Build(readings, Granularity.Day, t0.AddDays(-3), t0.AddHours(30));
        Check("无数据区间标记为未覆盖", gap.Take(3).All(b => !b.Covered));

        // 补成 0 的话小时曲线画到右端会直接掉到底
        List<Bucket> tail = UsageAggregator.Build(readings, Granularity.Hour, t0, t0.AddHours(34));
        Check("最后一次抄表之后未覆盖", tail.Count == 34 && tail.Skip(30).All(b => !b.Covered),
            string.Join(",", tail.Skip(30).Select(b => b.Covered ? "1" : "0")));

        // 只有一条抄表记录：算不出用电量，但那一天/那一月得算"有数据"，否则图表整块空白
        var single = new List<Reading> { R(t0.AddHours(3), 100, 31.68) };
        List<Bucket> one = UsageAggregator.Build(single, Granularity.Day, t0.Date, t0.Date.AddDays(1));
        Check("单条记录当天算已覆盖", one.Count == 1 && one[0].Covered && one[0].Usage == 0);
        Check("单条记录带出剩余电量",
            one[0].Remaining is { } o1 && Math.Abs(o1 - 31.68) < 1e-9);
        List<Bucket> oneMonth = UsageAggregator.Build(single, Granularity.Month,
            new DateTime(2026, 8, 1), new DateTime(2026, 9, 1));
        Check("单条记录当月算已覆盖", oneMonth.Count == 1 && oneMonth[0].Covered);

        Summary s = UsageAggregator.Summarize(readings, t0.AddHours(30));
        Check("Summary 剩余=30", s.Remaining is { } sr && Math.Abs(sr - 30) < 1e-9);
        Check("Summary 日均>0", s.AvgDaily is > 0, s.AvgDaily?.ToString("0.###") ?? "null");

        // 预计可用不足设定天数：度数还在阈值以上也要发提醒
        Check("剩不到设定天数算紧急",
            new Summary { DaysLeft = 0.4 }.RunsOutWithin(0.5)
            && !new Summary { DaysLeft = 0.6 }.RunsOutWithin(0.5));
        Check("天数调到 0 就是不看这一条", !new Summary { DaysLeft = 0.1 }.RunsOutWithin(0));
        Check("算不出预计可用就不算紧急", !new Summary().RunsOutWithin(0.5));

        // ---------- 整点归属：3:00 的读数减 2:00 的读数，整段记在"两点"那一格 ----------

        var day = new DateTime(2026, 8, 20);
        var pair = new List<Reading> { R(day.AddHours(2), 200, 40), R(day.AddHours(3), 201.5, 38.5) };
        List<Bucket> slots = UsageAggregator.Build(pair, Granularity.Hour, day.AddHours(1), day.AddHours(5));
        Check("整点格数=4", slots.Count == 4, slots.Count.ToString());
        Check("两点那格=1.5 度", Math.Abs(slots[1].Usage - 1.5) < 1e-9, slots[1].Usage.ToString("0.###"));
        Check("两点那格算已覆盖", slots[1].Covered);
        Check("三点那格还没数据", !slots[2].Covered);
        Check("一点那格没有数据", !slots[0].Covered);
        Check("两点期末剩余=38.5", slots[1].Remaining is { } sq && Math.Abs(sq - 38.5) < 1e-9,
            slots[1].Remaining?.ToString("0.##") ?? "null");

        Check("SlotOf 差 20 毫秒也算下一个整点",
            Reading.SlotOf(new DateTime(2026, 8, 20, 2, 59, 59, 980)) == day.AddHours(3));
        Check("SlotOf 整点后几秒还是这个整点",
            Reading.SlotOf(new DateTime(2026, 8, 20, 3, 0, 3)) == day.AddHours(3));
        Check("SlotOf 不到半点归本整点",
            Reading.SlotOf(new DateTime(2026, 8, 20, 3, 20, 0)) == day.AddHours(3));
        Check("SlotOf 过了半点还是归本整点",
            Reading.SlotOf(new DateTime(2026, 8, 20, 3, 42, 0)) == day.AddHours(3));
        Check("SlotOf 差 5 分钟内算下一格",
            Reading.SlotOf(new DateTime(2026, 8, 20, 3, 56, 0)) == day.AddHours(4));

        // ---------- 轮询节奏：整点和半点各一次，半点落回本整点那一格 ----------

        Check("整点过后等到半点",
            PollService.NextDelay(new DateTime(2026, 8, 20, 3, 0, 0)) == TimeSpan.FromMinutes(30),
            PollService.NextDelay(new DateTime(2026, 8, 20, 3, 0, 0)).ToString());
        Check("半点前等到半点",
            PollService.NextDelay(new DateTime(2026, 8, 20, 3, 5, 0)) == TimeSpan.FromMinutes(25));
        Check("半点过后等到下一个整点",
            PollService.NextDelay(new DateTime(2026, 8, 20, 3, 30, 0)) == TimeSpan.FromMinutes(30));
        Check("快到整点时等到整点",
            PollService.NextDelay(new DateTime(2026, 8, 20, 3, 59, 0)) == TimeSpan.FromMinutes(1));
        Check("早醒几十毫秒也不空转",
            PollService.NextDelay(new DateTime(2026, 8, 20, 3, 29, 59, 950))
                >= TimeSpan.FromMilliseconds(200));
        Check("半点那次归本整点",
            Reading.SlotOf(new DateTime(2026, 8, 20, 3, 30, 0)) == day.AddHours(3));

        // 漏采（程序没开）的那几个小时：增量按时间比例摊回去，不是全压在最后一格
        var skipped = new List<Reading> { R(day.AddHours(2), 200, 40), R(day.AddHours(5), 203, 37) };
        List<Bucket> spread = UsageAggregator.Build(skipped, Granularity.Hour, day.AddHours(2), day.AddHours(6));
        Check("漏采三格各 1.00 度", spread.Take(3).All(b => Math.Abs(b.Usage - 1.0) < 1e-9),
            string.Join(",", spread.Take(3).Select(b => b.Usage.ToString("0.###"))));

        // ---------- 间隔里的整点读数跟上次一模一样 ----------
        // 直接相减会变成"一个钟 2 度、下一个钟 0 度"的锯齿，增量得摊在两次抄表之间

        var lag = new List<Reading>
        {
            Rt(day.AddHours(1), day.AddHours(1), 100, 30),
            Rt(day.AddHours(2), day.AddHours(1), 100, 30),   // 电表还没上传新读数
            Rt(day.AddHours(3), day.AddHours(3), 102, 28),
        };
        List<Bucket> even = UsageAggregator.Build(lag, Granularity.Hour, day.AddHours(1), day.AddHours(5));
        Check("隔了两个钟才抄表时两个小时各 1.00 度",
            Math.Abs(even[0].Usage - 1.0) < 1e-9 && Math.Abs(even[1].Usage - 1.0) < 1e-9,
            string.Join(",", even.Select(b => b.Usage.ToString("0.###"))));
        Check("不会出现 0 用电的锯齿",
            even.Take(2).All(b => b.Covered && b.Usage > 0.5));
        Check("整段合计还是 2 度", Math.Abs(even.Sum(b => b.Usage) - 2) < 1e-9);
        Check("抄表之后那两格没数据", !even[2].Covered && !even[3].Covered);
        Check("重复读数只算一次抄表",
            UsageAggregator.Summarize(lag, day.AddHours(3)).Points == 2,
            UsageAggregator.Summarize(lag, day.AddHours(3)).Points.ToString());

        // 抄表落在格子中间：那一格只盖到一半，得标出来（图上画虚线 + 空心点）
        var half = new List<Reading>
        {
            Rt(day.AddHours(1), day.AddHours(1), 100, 30),
            Rt(day.AddHours(3), day.AddHours(2).AddMinutes(30), 101, 29),
        };
        List<Bucket> part = UsageAggregator.Build(half, Granularity.Hour, day.AddHours(1), day.AddHours(4));
        Check("整格不算半格", part[0].Covered && !part[0].Partial);
        Check("只抄到一半的那格标成 Partial", part[1].Covered && part[1].Partial);
        Check("半格分到三分之一度", Math.Abs(part[1].Usage - 1.0 / 3) < 1e-9,
            part[1].Usage.ToString("0.####"));

        // ---------- 电价换算 ----------

        Check("一度三分之二元（0.666…）", Math.Abs(AppConfig.PricePerKwh - 2.0 / 3.0) < 1e-12,
            AppConfig.PricePerKwh.ToString("0.########"));
        Check("2 元换 3 度", Math.Abs(AppConfig.KwhOf(2) - 3) < 1e-9,
            AppConfig.KwhOf(2).ToString("0.####"));
        Check("60 元换 90 度", Math.Abs(AppConfig.KwhOf(60) - 90) < 1e-9,
            AppConfig.KwhOf(60).ToString("0.####"));
        Check("3 度换 2 元", Math.Abs(AppConfig.YuanOf(3) - 2) < 1e-9);
        Check("换过去再换回来还是原数", Math.Abs(AppConfig.YuanOf(AppConfig.KwhOf(37.5)) - 37.5) < 1e-9);
        Check("一笔充值折得出度数",
            Math.Abs(new RechargeRecord { PayCent = 6000 }.Kwh - 90) < 1e-9,
            new RechargeRecord { PayCent = 6000 }.Kwh.ToString("0.####"));

        // ---------- 充值标在剩余电量涨上去的那一格 ----------

        var afterPay = new List<Reading>
        {
            R(day.AddHours(1), 100, 10),
            R(day.AddHours(2), 101, 9),
            R(day.AddHours(3), 102, 99),   // 充进来 90 度（60 元 ÷ ⅔）
        };
        var paid = new List<RechargeRecord>
        {
            new() { OrderCode = "P1", PayTime = day.AddHours(2).AddMinutes(30), PayCent = 6000 },
        };
        List<Bucket> marked = UsageAggregator.Build(
            afterPay, Granularity.Hour, day.AddHours(1), day.AddHours(5), paid);
        Check("充值标在剩余涨上去那一格", marked[1].Recharged && !marked[0].Recharged,
            string.Join(",", marked.Select(b => b.RechargeYuan.ToString("0.#"))));
        Check("那一格记的是 60 元", Math.Abs(marked[1].RechargeYuan - 60) < 1e-9);
        Check("那一格折合 90 度", Math.Abs(marked[1].RechargeKwh - 90) < 1e-9,
            marked[1].RechargeKwh.ToString("0.####"));
        Check("没充值的格子不标",
            marked.Where((_, i) => i != 1).All(b => !b.Recharged));
        Check("不给充值记录也照样画得出来",
            UsageAggregator.Build(afterPay, Granularity.Hour, day.AddHours(1), day.AddHours(5))
                .All(b => !b.Recharged));
        List<Bucket> byMonth = UsageAggregator.Build(afterPay, Granularity.Month,
            new DateTime(2026, 8, 1), new DateTime(2026, 9, 1), paid);
        Check("月粒度也标充值", byMonth.Count == 1 && Math.Abs(byMonth[0].RechargeYuan - 60) < 1e-9,
            byMonth[0].RechargeYuan.ToString("0.#"));
        List<Bucket> byYear = UsageAggregator.Build(afterPay, Granularity.Year,
            new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), paid);
        Check("年粒度也标充值", byYear.Count == 1 && Math.Abs(byYear[0].RechargeKwh - 90) < 1e-9,
            byYear[0].RechargeKwh.ToString("0.#"));

        // 同一格里又用掉一些电：净涨幅只有 81 度，但这一笔该记成 90 度
        var payAndUse = new List<Reading>
        {
            R(day.AddHours(1), 100, 10),
            R(day.AddHours(2), 101, 9),
            R(day.AddHours(3), 110, 90),
        };
        List<Bucket> eaten = UsageAggregator.Build(
            payAndUse, Granularity.Hour, day.AddHours(1), day.AddHours(5), paid);
        Check("充完又用掉一些也标得对", eaten[1].Recharged, string.Join(",",
            eaten.Select(b => b.RechargeKwh.ToString("0.#"))));
        Check("折合度数按金额算，不按净涨幅",
            Math.Abs(eaten[1].RechargeKwh - 90) < 1e-9
            && eaten[1].Remaining is { } er && Math.Abs(er - 90) < 1e-6,
            eaten[1].RechargeKwh.ToString("0.####"));

        // 付款那一格只是电表修正读数涨了 0.5 度，真的那一跳在下一格：挑涨幅接近应充度数的那格
        var lateJump = new List<Reading>
        {
            R(day.AddHours(1), 100, 10),
            R(day.AddHours(2), 100, 10),
            R(day.AddHours(3), 101, 10.5),
            R(day.AddHours(4), 102, 100),
        };
        List<Bucket> late = UsageAggregator.Build(
            lateJump, Granularity.Hour, day.AddHours(1), day.AddHours(6), paid);
        Check("小波动骗不走那一笔充值", late[2].Recharged && !late[1].Recharged,
            string.Join(",", late.Select(b => b.RechargeYuan.ToString("0.#"))));

        // 抄表间隔说不准：这次隔了 8 个钟才抄，三角要跟到真涨上去那一格，不能因为隔得远就落回付款格
        var slowMeter = new List<Reading>
        {
            Rt(day.AddHours(1), day.AddHours(1), 100, 10),
            Rt(day.AddHours(9), day.AddHours(9), 101, 100),
        };
        List<Bucket> slow = UsageAggregator.Build(
            slowMeter, Granularity.Hour, day.AddHours(1), day.AddHours(11), paid);
        Check("隔很久才抄表也标在涨上去那一格", slow[7].Recharged && !slow[1].Recharged,
            string.Join(",", slow.Select(b => b.RechargeYuan.ToString("0.#"))));

        CheckStore(Check);
        CheckRechargeStore(Check);
        CheckConfig(Check);
        CheckOrphans(Check);

        // 真实返回样本
        const string sample = """
            {"success":true,"code":10000,"message":"查询成功","data":{"id":"2138","schoolId":"1",
            "schoolName":"五邑大学本校区","apartID":"227","apartName":"43栋","roomID":"2410",
            "roomName":"43422","usedamp":3343.10,"resamp":31.68,"status":0,
            "updatedt":"2026-08-29 09:45:36","userTypeID":1,"userTypeName":"学生用电充值"}}
            """;
        try
        {
            Reading parsed = ElectricityApi.Parse(sample.Replace("\r", "").Replace("\n", ""), 43, 422);
            Check("解析剩余电量=31.68", Math.Abs(parsed.Remaining - 31.68) < 1e-9);
            Check("解析累计用电=3343.10", Math.Abs(parsed.Used - 3343.10) < 1e-9);
            Check("解析抄表时间", parsed.MeterTime == new DateTime(2026, 8, 29, 9, 45, 36));
            Check("解析时盖上整点", parsed.SlotTime == Reading.SlotOf(parsed.FetchedAt),
                Reading.FormatTime(parsed.SlotTime));
        }
        catch (Exception ex)
        {
            Check("解析接口返回", false, ex.Message);
        }

        // 金额单位是分
        var rec = new RechargeRecord
        {
            OrderCode = "T1", PayTime = new DateTime(2026, 8, 29, 10, 47, 58),
            PayCent = 2000, PayMethod = "code", PayResult = "已完成", Building = 43, Room = 422,
        };
        Check("分转元=20", Math.Abs(rec.Yuan - 20) < 1e-9, rec.Yuan.ToString("0.##"));
        Check("扫码充值标签", rec.MethodLabel == "扫码充值", rec.MethodLabel);
        Check("学生卡标签", new RechargeRecord { PayMethod = "card" }.MethodLabel == "学生卡");

        CheckMail(Check, UsageAggregator.Summarize(readings, t0.AddHours(30)));

        QrSelfTest.Run(Check);

        log.AppendLine();
        log.AppendLine(failed == 0 ? "全部通过" : $"{failed} 项失败");

        string outPath = Path.Combine(Path.GetTempPath(), "billsystem-selftest.txt");
        File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(false));
        return failed;
    }

    /// <summary>
    /// 仓库的整点语义：同一个整点写第二次是覆盖（值变了才返回 true，半点那一次走的就是这条路），
    /// 老数据没有 SlotTime 时按采集时间折算成整点补上。用一对假房号，跑完删文件。
    /// </summary>
    private static void CheckStore(Action<string, bool, string> check)
    {
        string path = Path.Combine(AppConfig.DataDir, "readings-B997-R9997.jsonl");
        try
        {
            if (File.Exists(path)) File.Delete(path);

            var slot = new DateTime(2026, 8, 20, 2, 0, 0);
            var store = new ReadingStore(997, 9997);
            check("同一格第一次写入算新数据", store.TryAdd(Mk(slot, 200, 40)), "");
            check("同一格原样再写一次不算变化", !store.TryAdd(Mk(slot, 200, 40)), "");
            check("原样再写一次不往文件里加行", Lines(path) == 1, Lines(path).ToString());
            check("同一格数值变了算变化", store.TryAdd(Mk(slot, 200.5, 39.5)), "");
            check("同一格只留一条", store.Count == 1, store.Count.ToString());
            check("留下的是后写的那条",
                store.Latest is { } l && Math.Abs(l.Used - 200.5) < 1e-9,
                store.Latest?.Used.ToString("0.##") ?? "null");
            check("下一个整点是新的一格", store.TryAdd(Mk(slot.AddHours(1), 201.5, 38.5)) && store.Count == 2,
                store.Count.ToString());

            // 半点那一次查询：SlotTime 空着，按采集时间落回本整点那一格
            check("半点读数原样不动就什么都不做",
                !store.TryAdd(Half(slot.AddHours(1), 201.5, 38.5)) && store.Count == 2,
                store.Count.ToString());
            check("半点值变了就顶替掉整点那条",
                store.TryAdd(Half(slot.AddHours(1), 202, 38)) && store.Count == 2,
                store.Count.ToString());
            check("顶替后留下的是半点那条",
                store.Latest is { } h && Math.Abs(h.Used - 202) < 1e-9
                && h.SlotTime == slot.AddHours(1),
                store.Latest?.Used.ToString("0.##") ?? "null");

            // 重新载入：文件里那一格有两行，后写的应该赢
            var again = new ReadingStore(997, 9997);
            check("重载后同一格还是一条", again.Count == 2, again.Count.ToString());
            check("重载后留的是后写的那条",
                again.Snapshot()[0] is { } f && Math.Abs(f.Used - 200.5) < 1e-9,
                again.Snapshot()[0].Used.ToString("0.##"));
            check("重载后半点那条也还在",
                again.Latest is { } h2 && Math.Abs(h2.Used - 202) < 1e-9,
                again.Latest?.Used.ToString("0.##") ?? "null");
            check("载入时把文件收拢成一格一行", Lines(path) == 2, Lines(path).ToString());

            // 老格式：只有 MeterTime / FetchedAt，没有 SlotTime
            File.AppendAllText(path,
                """{"MeterTime":"2026-08-20T05:41:00","FetchedAt":"2026-08-20T06:00:00","Used":210,"Remaining":30,"Building":997,"Room":9997}""" + Environment.NewLine);
            var migrated = new ReadingStore(997, 9997);
            check("老数据按采集时间补上整点",
                migrated.Latest is { } m && m.SlotTime == new DateTime(2026, 8, 20, 6, 0, 0),
                migrated.Latest is { } m2 ? Reading.FormatTime(m2.SlotTime) : "null");
        }
        catch (Exception ex)
        {
            check("仓库整点语义", false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就算了 */ }
        }
    }

    /// <summary>
    /// 提醒邮件的内容：两份正文（纯文本 / HTML）都得带上关键数字，末尾写清眼下的提醒条件。
    /// 发件人显示名必须是<b>触发那一间</b>的房号——几间共用一个发件箱，收件人靠这行分得清是谁。
    /// 只查生成出来的文字，不真的发信。
    /// </summary>
    private static void CheckMail(Action<string, bool, string> check, Summary s)
    {
        var cfg = new AppConfig { MailFrom = "a@qq.com", MailAuthCode = "0123456789abcdef" };
        var dorm = new Dorm(43, 422)
        {
            LowThreshold = 5, LowDaysThreshold = 0.5,
            MailTo = { "me@qq.com", "you@wyu.edu.cn" },
        };
        Reading r = R(new DateTime(2026, 8, 30, 12, 0, 0), 3352.2, 4.2);

        MailAlert.Letter low = MailAlert.LowLetter(dorm, r, s, true);
        MailAlert.Letter soon = MailAlert.LowLetter(dorm, r, s, false);

        check("两种触发的标题都是那句告急",
            low.Subject == "电量告急！将军救急！" && soon.Subject == low.Subject, low.Subject);
        check("开头那句说明是哪一种触发",
            low.Text.Contains("已经低于 5 度") && soon.Text.Contains("撑不到 0.5 天"), "");
        check("正文带剩余 / 预计可用 / 日均",
            low.Text.Contains("剩余电量：4.20 度")
            && low.Text.Contains("预计可用：约 ")
            && low.Text.Contains("日均用电："), "");
        check("末尾写清两个提醒条件",
            low.Text.Contains("剩余低于 5 度，或预计可用不足 0.5 天"), "");
        check("天数调到 0 时只写度数那一条",
            MailAlert.LowLetter(new Dorm(43, 422) { LowThreshold = 5 }, r, s, true)
                .Text.Contains("提醒条件：剩余低于 5 度。"), "");
        check("HTML 那份也带着度数和抄表时间",
            low.Html.Contains("4.20") && low.Html.Contains("抄表时间"), "");
        check("HTML 没有没闭合的花括号占位",
            !low.Html.Contains('{') && !low.Html.Contains('}'), "");
        check("测试信带上眼下的数字和这一间的收件人",
            MailAlert.TestLetter(cfg, dorm, s) is { } t
            && t.Text.Contains("剩余电量：")
            && t.Text.Contains(dorm.MailToLine), "");
        check("没有汇总数字时也生成得出来",
            MailAlert.LowLetter(dorm, r, null, true).Text.Contains("剩余电量：4.20 度"), "");
        check("发件人显示的是那一间的房号", low.FromName == "43栋422", low.FromName);
        check("测试信的标题和发件人跟真的那封一样",
            MailAlert.TestLetter(cfg, dorm, s) is { } t2
            && t2.Subject == low.Subject && t2.FromName == low.FromName,
            $"{MailAlert.TestLetter(cfg, dorm, s).Subject} / {MailAlert.TestLetter(cfg, dorm, s).FromName}");

        // 两间互不相干
        var other = new Dorm(12, 108) { LowThreshold = 20, MailTo = { "other@qq.com" } };
        MailAlert.Letter otherLow = MailAlert.LowLetter(other, r, s, true);
        check("换一间就换发件人", otherLow.FromName == "12栋108", otherLow.FromName);
        check("信里说的是那一间", otherLow.Text.Contains("12栋108 的剩余电量"), "");
        check("信里的阈值也是那一间自己的",
            otherLow.Text.Contains("已经低于 20 度")
            && otherLow.Text.Contains("提醒条件：剩余低于 20 度。"), "");
        check("测试信收件人只列这一间的",
            MailAlert.TestLetter(cfg, other, s).Text.Contains("other@qq.com")
            && !MailAlert.TestLetter(cfg, other, s).Text.Contains("me@qq.com"), "");

        // 发得出去的条件是按间算的：共用的发件箱填齐了，还得这一间自己有收件人
        check("这一间没收件人就发不出去", !MailAlert.Configured(cfg, new Dorm(43, 422)), "");
        check("填齐了就能发", MailAlert.Configured(cfg, dorm), "");
        check("没填发件箱谁都发不出去",
            !MailAlert.Configured(new AppConfig { MailAuthCode = "x" }, dorm), "");
        check("没填授权码谁都发不出去",
            !MailAlert.Configured(new AppConfig { MailFrom = "a@qq.com" }, dorm), "");
    }

    /// <summary>
    /// 配置和宿舍名单：房号认得回来、加重了要去掉、当前那间没了要落回第一间，提醒是一间一套，
    /// 还有设置窗口拿的那份副本必须是真副本（改副本不能动到正在跑的配置）。
    /// </summary>
    private static void CheckConfig(Action<string, bool, string> check)
    {
        check("房号拼得出文件名那一段", Dorm.KeyOf(43, 422) == "B43-R422", Dorm.KeyOf(43, 422));
        check("文件名那一段认得回来",
            Dorm.Parse("B43-R422") is { Building: 43, Room: 422 }, "");
        check("认不出来的返回空",
            Dorm.Parse("readings") is null && Dorm.Parse("B43") is null && Dorm.Parse("BX-RY") is null, "");
        check("房号超范围不算有效", !new Dorm(0, 422).Valid && !new Dorm(43, 0).Valid, "");

        var cfg = new AppConfig
        {
            Dorms =
            {
                new Dorm(43, 422)
                {
                    LowThreshold = 5000, LowDaysThreshold = -3,
                    MailEnabled = true, MailTo = { " me@qq.com ", "me@QQ.com", "" },
                },
                new Dorm(43, 422),
                new Dorm(0, 0),
                new Dorm(12, 108) { LowThreshold = 25, LowDaysThreshold = 1.5 },
            },
            CurrentDorm = "B99-R9",
        };
        cfg.Normalize();

        check("同一间只留一份", cfg.Dorms.Count == 2, cfg.Dorms.Count.ToString());
        check("房号不对的那间被扔掉", cfg.Dorms.All(d => d.Valid), "");
        check("当前那间不在名单里就落回第一间", cfg.CurrentDorm == "B43-R422", cfg.CurrentDorm);
        check("收件人去空去重、顺手 trim",
            cfg.Dorms[0].MailTo.Count == 1 && cfg.Dorms[0].MailTo[0] == "me@qq.com",
            string.Join("|", cfg.Dorms[0].MailTo));
        check("阈值夹回范围内",
            Math.Abs(cfg.Dorms[0].LowThreshold - 1000) < 1e-9 && cfg.Dorms[0].LowDaysThreshold == 0,
            $"{cfg.Dorms[0].LowThreshold} / {cfg.Dorms[0].LowDaysThreshold}");
        check("阈值是按间存的，一间一套",
            Math.Abs(cfg.Dorms[1].LowThreshold - 25) < 1e-9
            && Math.Abs(cfg.Dorms[1].LowDaysThreshold - 1.5) < 1e-9,
            $"{cfg.Dorms[1].LowThreshold} / {cfg.Dorms[1].LowDaysThreshold}");
        check("没配过的那间用默认的 10 度、不看天数",
            Math.Abs(new Dorm(43, 422).LowThreshold - 10) < 1e-9
            && new Dorm(43, 422).LowDaysThreshold == 0, "");

        // 设置窗口在副本上改，点取消就该什么都没变
        AppConfig copy = cfg.Clone();
        copy.Dorms[0].MailTo.Add("new@qq.com");
        copy.Dorms[0].NotifyEnabled = true;
        copy.Dorms[0].LowThreshold = 30;
        copy.Dorms.Add(new Dorm(7, 701));
        check("副本加收件人不影响原来那份", cfg.Dorms[0].MailTo.Count == 1,
            cfg.Dorms[0].MailTo.Count.ToString());
        check("副本改开关不影响原来那份", !cfg.Dorms[0].NotifyEnabled, "");
        check("副本改阈值不影响原来那份",
            Math.Abs(cfg.Dorms[0].LowThreshold - 1000) < 1e-9, cfg.Dorms[0].LowThreshold.ToString());
        check("副本加宿舍不影响原来那份", cfg.Dorms.Count == 2, cfg.Dorms.Count.ToString());

        // 点了保存：整份抄回来，宿舍还是各自独立的对象
        cfg.CopyFrom(copy);
        check("保存后抄回了新加的那间", cfg.Dorms.Count == 3, cfg.Dorms.Count.ToString());
        check("保存后抄回了按间配的提醒",
            cfg.Dorms[0].NotifyEnabled && cfg.Dorms[0].MailTo.Count == 2
            && Math.Abs(cfg.Dorms[0].LowThreshold - 30) < 1e-9,
            string.Join("|", cfg.Dorms[0].MailTo));
        copy.Dorms[0].MailTo.Clear();
        check("抄回来之后两份还是分开的", cfg.Dorms[0].MailTo.Count == 2,
            cfg.Dorms[0].MailTo.Count.ToString());

        // 主界面随手存下来的那些（粒度、充值窗口位置）不该被设置窗口那份旧副本盖回去。
        // 另起一对，别动上面那两份——下面还要接着拿 cfg 验"在跑的那间怎么跟上新设置"
        var mine = new AppConfig { Granularity = Granularity.Hour, RechargeX = 120, RechargeY = 80 };
        AppConfig stale = mine.Clone();
        stale.Granularity = Granularity.Year;
        stale.RechargeX = int.MinValue;
        stale.RechargeY = int.MinValue;
        mine.CopyFrom(stale);
        check("保存设置不会盖掉主界面选的粒度", mine.Granularity == Granularity.Hour, mine.Granularity.ToString());
        check("保存设置不会盖掉充值窗口记下的位置",
            mine.HasRechargePos && mine.RechargeX == 120 && mine.RechargeY == 80, mine.RechargeX.ToString());

        // 已经在跑的那间靠这个跟上新设置，不重建、不重读 jsonl
        var running = new Dorm(43, 422);
        running.CopyAlertsFrom(cfg.Dorms[0]);
        check("在跑的那间跟得上新设置",
            running.NotifyEnabled && running.MailTo.Count == 2
            && Math.Abs(running.LowThreshold - 30) < 1e-9, "");
        running.MailTo.Clear();
        check("跟上之后收件人也是各自一份", cfg.Dorms[0].MailTo.Count == 2, "");
    }

    /// <summary>
    /// 设置里"数据"页那份名单：数据目录里认得出房号的 jsonl，按宿舍归拢，
    /// <b>已经在记录名单里的不算多余</b>。用一对假房号，只删自己造的那两个文件。
    /// </summary>
    private static void CheckOrphans(Action<string, bool, string> check)
    {
        string mine = Path.Combine(AppConfig.DataDir, "readings-B995-R9995.jsonl");
        string strayRead = Path.Combine(AppConfig.DataDir, "readings-B994-R9994.jsonl");
        string strayPay = Path.Combine(AppConfig.DataDir, "recharges-B994-R9994.jsonl");
        try
        {
            File.WriteAllText(mine, "{}" + Environment.NewLine);
            File.WriteAllText(strayRead, "{}" + Environment.NewLine + "{}" + Environment.NewLine);
            File.WriteAllText(strayPay, "{}" + Environment.NewLine);

            var cfg = new AppConfig { Dorms = { new Dorm(995, 9995) } };
            cfg.Normalize();
            List<DormFiles> orphans = DormFiles.Orphans(cfg);

            check("在记录名单里的不算多余",
                orphans.All(f => f.Dorm.Key != "B995-R9995"), "");

            DormFiles? stray = orphans.FirstOrDefault(f => f.Dorm.Key == "B994-R9994");
            check("名单外的那间列得出来", stray is not null, "");
            check("同一间的两个文件归到一起", stray?.Paths.Count == 2,
                stray?.Paths.Count.ToString() ?? "null");
            check("读数和充值分开数行数",
                stray is { ReadingLines: 2, RechargeLines: 1 },
                $"{stray?.ReadingLines} / {stray?.RechargeLines}");
            check("那一行说明写得出来", stray?.Detail.Contains("2 条读数") == true, stray?.Detail ?? "null");

            check("删掉就把这一间的文件都清了",
                stray is not null && stray.TryDelete(out _)
                && !File.Exists(strayRead) && !File.Exists(strayPay), "");
        }
        catch (Exception ex)
        {
            check("多余数据文件的名单", false, ex.Message);
        }
        finally
        {
            foreach (string p in new[] { mine, strayRead, strayPay })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* 删不掉就算了 */ }
        }
    }

    /// <summary>
    /// 落到文件里得是正序（最早的在第一行）。老文件是倒序的，载入时应该自己理顺。
    /// 顺带查同一单状态从"处理中"翻成"已完成"时本地跟不跟着改。用一对假房号，跑完删文件。
    /// </summary>
    private static void CheckRechargeStore(Action<string, bool, string> check)
    {
        string path = Path.Combine(AppConfig.DataDir, "recharges-B996-R9996.jsonl");
        try
        {
            if (File.Exists(path)) File.Delete(path);

            var early = new DateTime(2026, 7, 1, 8, 0, 0);
            var mid = new DateTime(2026, 8, 1, 9, 0, 0);
            var late = new DateTime(2026, 8, 20, 10, 0, 0);

            // 服务器返回的顺序：最近的在前
            var store = new RechargeStore(996, 9996);
            check("合并进来算新增 3 笔",
                store.Merge(new[] { Pay("C", late), Pay("B", mid), Pay("A", early) }) == 3,
                store.Count.ToString());
            check("同一批再合并一次不重复", store.Merge(new[] { Pay("B", mid) }) == 0, store.Count.ToString());
            check("内存里最近一笔在最前面", store.Snapshot()[0].OrderCode == "C", store.Snapshot()[0].OrderCode);
            check("文件里最早那笔在第一行", FirstOrder(path) == "A", FirstOrder(path));
            check("文件里最新那笔在最后一行", LastOrder(path) == "C", LastOrder(path));

            // 中间补一笔更早的：追加写会排到最后，所以这里必须整份重写
            check("补一笔更早的也算新增", store.Merge(new[] { Pay("Z", early.AddDays(-10)) }) == 1, "");
            check("补完文件第一行换成那笔更早的", FirstOrder(path) == "Z", FirstOrder(path));
            check("补完文件最后一行还是最新那笔", LastOrder(path) == "C", LastOrder(path));

            // 老版本留下的倒序文件
            File.WriteAllText(path, string.Join(Environment.NewLine, new[]
            {
                Line("C", late), Line("B", mid), Line("A", early), Line("B", mid),
            }) + Environment.NewLine, new UTF8Encoding(false));

            var again = new RechargeStore(996, 9996);
            check("重载后按订单号去重", again.Count == 3, again.Count.ToString());
            check("载入时把倒序文件理成正序", FirstOrder(path) == "A", FirstOrder(path));
            check("载入时把重复行清掉", Lines(path) == 3, Lines(path).ToString());

            // ---------- 状态翻了要跟着改 ----------

            // 刚付完那一刻学校那边写的还是"处理中"，过一会儿才翻成"已完成"
            RechargeRecord pending = Pay("P", late.AddDays(1));
            pending.PayResult = "处理中";
            check("先收下一笔处理中的", again.Merge(new[] { pending }) == 1,
                again.Snapshot()[0].PayResult);

            RechargeRecord settled = Pay("P", late.AddDays(1));
            check("同一单状态翻了算有变动", again.Merge(new[] { settled }) == 1, "");
            check("不会多出一笔", again.Count == 4, again.Count.ToString());
            check("列表里跟着改成已完成",
                again.Snapshot().First(r => r.OrderCode == "P").PayResult == "已完成",
                again.Snapshot().First(r => r.OrderCode == "P").PayResult);
            check("落盘的也是已完成",
                new RechargeStore(996, 9996).Snapshot().First(r => r.OrderCode == "P").PayResult == "已完成",
                "");
            check("状态没变就不算有变动", again.Merge(new[] { settled }) == 0, "");
        }
        catch (Exception ex)
        {
            check("充值记录仓库", false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就算了 */ }
        }
    }

    private static RechargeRecord Pay(string code, DateTime at) => new()
    {
        OrderCode = code, PayTime = at, PayCent = 3000,
        PayMethod = "code", PayResult = "已完成", Building = 996, Room = 9996,
    };

    private static string Line(string code, DateTime at) => JsonSerializer.Serialize(Pay(code, at));

    private static string FirstOrder(string path) => OrderAt(path, 0);
    private static string LastOrder(string path) => OrderAt(path, -1);

    private static string OrderAt(string path, int index)
    {
        string[] lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0) return "";
        return JsonSerializer.Deserialize<RechargeRecord>(lines[index < 0 ? lines.Length - 1 : index])?.OrderCode ?? "";
    }

    /// <summary>jsonl 里有几行真数据（末尾那个空行不算）。</summary>
    private static int Lines(string path) =>
        File.Exists(path) ? File.ReadAllLines(path).Count(l => l.Trim().Length > 0) : 0;

    private static Reading Mk(DateTime slot, double used, double remain) => new()
    {
        SlotTime = slot,
        MeterTime = slot.AddMinutes(-19),
        FetchedAt = slot,
        Used = used,
        Remaining = remain,
        Building = 997,
        Room = 9997,
    };

    /// <summary>
    /// 半点那一次查询：<c>SlotTime</c> 空着（交给仓库按采集时间折算），采集时间落在 xx:30，
    /// 抄表时间跟 <see cref="Mk"/> 一样，这样"值没变"的情况才真的判成没变。
    /// </summary>
    private static Reading Half(DateTime hour, double used, double remain) => new()
    {
        MeterTime = hour.AddMinutes(-19),
        FetchedAt = hour.AddMinutes(30),
        Used = used,
        Remaining = remain,
        Building = 997,
        Room = 9997,
    };

    private static Reading R(DateTime t, double used, double remain) => new()
    {
        SlotTime = t,
        MeterTime = t,
        FetchedAt = t,
        Used = used,
        Remaining = remain,
        Building = 43,
        Room = 422,
    };

    /// <summary>整点和抄表时间分开给：抄表间隔不固定，这两个时间平时是不一样的。</summary>
    private static Reading Rt(DateTime slot, DateTime meter, double used, double remain) => new()
    {
        SlotTime = slot,
        MeterTime = meter,
        FetchedAt = slot,
        Used = used,
        Remaining = remain,
        Building = 43,
        Room = 422,
    };
}
