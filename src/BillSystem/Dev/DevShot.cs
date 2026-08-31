using System.Drawing.Imaging;
using BillSystem.Models;
using BillSystem.Services;
using BillSystem.UI;

namespace BillSystem;

/// <summary>
/// 开发用出图：<c>BillSystem.exe --screenshot [目录]</c>。
/// 用一份假数据把主窗口和任务栏组件各自渲染成 PNG，方便离屏检查排版，不截屏。
/// </summary>
internal static class DevShot
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // 出图用假房号，别污染真实数据；也别动用户的 config.json
        string seedFile = Path.Combine(AppConfig.DataDir, "readings-B999-R9999.jsonl");
        if (File.Exists(seedFile)) File.Delete(seedFile);
        byte[]? cfgBackup = File.Exists(AppConfig.ConfigPath) ? File.ReadAllBytes(AppConfig.ConfigPath) : null;

        var store = new ReadingStore(999, 9999);
        List<DateTime> topUps = Seed(store);

        // 充值记录跟上面那份假读数里"剩余电量突然涨上去"的时刻对上，
        // 图表上那几根绿柱子才跟真程序一个意思
        string payFile = Path.Combine(AppConfig.DataDir, "recharges-B999-R9999.jsonl");
        if (File.Exists(payFile)) File.Delete(payFile);
        var payStore = new RechargeStore(999, 9999);
        payStore.Merge(TopUpRecords(topUps));

        var cfg = new AppConfig
        {
            Granularity = Granularity.Day,
        };

        var form = new MainForm(cfg, store, payStore)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
        };
        form.ClientSize = new Size(1080, 700);
        form.Show();
        Application.DoEvents();

        Reading last = store.Latest!;
        var status = new PollStatus { Latest = last, LastSuccess = DateTime.Now, LastAttempt = DateTime.Now };
        form.UpdateStatus(status);
        Save(form, Path.Combine(outDir, "main-day.png"));

        cfg.Granularity = Granularity.Hour;
        form.ApplyConfig(cfg);
        form.UpdateStatus(status);
        Save(form, Path.Combine(outDir, "main-hour.png"));

        cfg.Granularity = Granularity.Month;
        form.ApplyConfig(cfg);
        form.UpdateStatus(status);
        Save(form, Path.Combine(outDir, "main-month.png"));

        form.Hide();

        // 只有一条抄表记录的样子：用电量算不出来，但图表不该整块空白
        string oneFile = Path.Combine(AppConfig.DataDir, "readings-B998-R9998.jsonl");
        if (File.Exists(oneFile)) File.Delete(oneFile);
        var oneStore = new ReadingStore(998, 9998);
        DateTime oneSlot = Reading.SlotOf(DateTime.Now);
        oneStore.TryAdd(new Reading
        {
            SlotTime = oneSlot,
            MeterTime = DateTime.Now.Date.AddHours(9).AddMinutes(45),
            FetchedAt = DateTime.Now,
            Used = 3343.10,
            Remaining = 31.68,
            Building = 998,
            Room = 9998,
        });

        var oneCfg = cfg.Clone();
        oneCfg.Granularity = Granularity.Day;
        var oneForm = new MainForm(oneCfg, oneStore)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
        };
        oneForm.ClientSize = new Size(1080, 700);
        oneForm.Show();
        Application.DoEvents();
        oneForm.UpdateStatus(new PollStatus
        {
            Latest = oneStore.Latest,
            LastSuccess = DateTime.Now,
            LastAttempt = DateTime.Now,
        });
        Save(oneForm, Path.Combine(outDir, "main-single-reading.png"));
        oneForm.Hide();
        oneForm.Dispose();

        // 任务栏组件：离屏冻结成实测的 43px 高，不去贴真任务栏，免得在用户桌面上闪一下
        var widget = new TaskbarWidget(cfg) { Location = new Point(-4000, -4000) };
        widget.DevFreeze(43);
        widget.Show();
        widget.UpdateData(status, UsageAggregator.Summarize(store.Snapshot(), DateTime.Now));
        widget.DevFreeze(43);
        Application.DoEvents();
        Save(widget, Path.Combine(outDir, "widget.png"));
        widget.Hide();

        // 组件的悬停信息卡：直接拿组件自己那张（内容已经在 UpdateData 里填好），
        // 出图这边不另抄一份，免得图跟真界面对不上
        WidgetTip tip = widget.DevTip();
        tip.Location = new Point(-4000, -4000);
        tip.Show();
        tip.DevFreeze();
        Application.DoEvents();
        Save(tip, Path.Combine(outDir, "widget-tip.png"));
        tip.Hide();

        // 设置窗口：只出图，不真的弹出来（Show 之后立刻画进位图再关掉）
        using var api = new ElectricityApi();
        var dlg = new SettingsForm(cfg, api)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
        };
        dlg.Show();
        Application.DoEvents();
        Save(dlg, Path.Combine(outDir, "settings.png"));
        dlg.Hide();
        dlg.Dispose();

        // 提醒邮件的排版：两种触发条件各写一份 HTML，浏览器直接打开就能看，不用真发信。
        // 样张的数字自己凑一套齐的（4.2 度、日均 9.8 度 → 还能用十来个小时），
        // 拿真读数改一个剩余度数出来的话，"剩 4.2 度"和"还能用 4.8 天"会对不上
        var mailCfg = new AppConfig { LowThreshold = 5, LowDaysThreshold = 0.5 };
        var lowNow = new Reading
        {
            SlotTime = last.SlotTime,
            MeterTime = last.MeterTime,
            FetchedAt = last.FetchedAt,
            Used = last.Used,
            Remaining = 4.2,
            Building = AppConfig.FixedBuilding,
            Room = AppConfig.FixedRoom,
        };
        var mailSummary = new Summary
        {
            Remaining = lowNow.Remaining,
            MeterTime = lowNow.MeterTime,
            TotalUsed = lowNow.Used,
            Today = 6.14,
            ThisMonth = 225.06,
            AvgDaily = 9.8,
            AvgSpanDays = 7,
            DaysLeft = lowNow.Remaining / 9.8,
            RunOutDate = DateTime.Now.AddDays(lowNow.Remaining / 9.8),
            Points = 40,
        };
        var utf8 = new System.Text.UTF8Encoding(false);
        File.WriteAllText(Path.Combine(outDir, "mail-low.html"),
            MailAlert.LowLetter(mailCfg, lowNow, mailSummary, belowThreshold: true).Html, utf8);
        File.WriteAllText(Path.Combine(outDir, "mail-soon.html"),
            MailAlert.LowLetter(mailCfg, lowNow, mailSummary, belowThreshold: false).Html, utf8);

        // 充值窗口：假记录 + 一张现成的付款码，offline 保证不联网、不下单。
        // 上面主界面用过的那几笔之外再补一批，好把记录列表填满
        payStore.Merge(SeedRecharges());

        using var payApi = new RechargeApi();
        var pay = new RechargeForm(cfg, payApi, payStore, offline: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
        };
        pay.Show();
        Application.DoEvents();
        pay.DevPose("等待付款 · 这张码还有 4:37");
        Save(pay, Path.Combine(outDir, "recharge.png"));

        // 点"生成付款码"之后弹的那个码窗，同样离屏画
        QrDialog qrDlg = pay.DevQrDialog();
        qrDlg.StartPosition = FormStartPosition.Manual;
        qrDlg.Location = new Point(-4000, -4000);
        qrDlg.Show();
        qrDlg.DevPose(30, "weixin://wxpay/bizpayurl?pr=DevShotSample01", "等待付款 · 这张码还有 4:37");
        Application.DoEvents();
        Save(qrDlg, Path.Combine(outDir, "recharge-qr.png"));
        qrDlg.Hide();
        qrDlg.Dispose();

        pay.Hide();
        pay.Dispose();

        form.Dispose();
        widget.Dispose();
        if (File.Exists(seedFile)) File.Delete(seedFile);
        if (File.Exists(oneFile)) File.Delete(oneFile);
        if (File.Exists(payFile)) File.Delete(payFile);
        if (cfgBackup is not null) File.WriteAllBytes(AppConfig.ConfigPath, cfgBackup);
        return 0;
    }

    /// <summary>假读数里每一次"剩余电量涨上去"都配一条充值记录，图表上就是那几根绿柱子。</summary>
    private static List<RechargeRecord> TopUpRecords(List<DateTime> times)
    {
        var list = new List<RechargeRecord>();
        for (int i = 0; i < times.Count; i++)
            list.Add(new RechargeRecord
            {
                OrderCode = $"TOP{i:00}{times[i]:yyyyMMddHHmmss}",
                PayTime = times[i],
                PayCent = 5000,
                PayMethod = "code",
                PayResult = "已完成",
                Building = 999,
                Room = 9999,
            });
        return list;
    }

    /// <summary>出图用的假充值记录，金额和间隔照真实历史的样子编。</summary>
    private static List<RechargeRecord> SeedRecharges()
    {
        var rnd = new Random(422);
        var list = new List<RechargeRecord>();
        DateTime t = DateTime.Now.AddHours(-3);

        for (int i = 0; i < 14; i++)
        {
            int yuan = new[] { 20, 30, 30, 50, 100 }[rnd.Next(5)];
            list.Add(new RechargeRecord
            {
                OrderCode = $"DEV{i:00}{t:yyyyMMddHHmmss}",
                PayTime = t,
                PayCent = yuan * 100,
                PayMethod = i % 5 == 4 ? "card" : "code",
                PayResult = "已完成",
                Building = 999,
                Room = 9999,
            });
            t = t.AddDays(-2 - rnd.Next(4)).AddHours(-rnd.Next(6));
        }
        return list;
    }

    /// <summary>
    /// 铺一份 40 天的假读数，返回每一次"剩余电量涨上去"（也就是充了值）的时刻。
    ///
    /// 照着学校那边的真实节奏来：<b>两个钟才抄一次表</b>，所以中间那个整点查到的是
    /// 同一个抄表时间、同一个累计值——出图正好能看出程序有没有把这种重复读数收拢掉。
    /// </summary>
    private static List<DateTime> Seed(ReadingStore store)
    {
        var rnd = new Random(20260829);
        var topUps = new List<DateTime>();
        double used = 3100, remain = 42;
        DateTime t = Reading.SlotOf(DateTime.Now).AddDays(-40);
        DateTime end = Reading.SlotOf(DateTime.Now);

        DateTime meterAt = t, nextMeter = t;
        double meterUsed = used, meterRemain = remain;

        while (t <= end)
        {
            // 到点了才换一份新读数，中间那一个整点查到的跟上次一模一样
            if (t >= nextMeter)
            {
                meterAt = t.AddMinutes(-rnd.Next(6, 40));   // 抄表比整点早一点
                meterUsed = Math.Round(used, 2);
                meterRemain = Math.Round(remain, 2);
                nextMeter = t.AddHours(2);
            }

            store.TryAdd(new Reading
            {
                SlotTime = t,
                MeterTime = meterAt,
                FetchedAt = t,
                Used = meterUsed,
                Remaining = meterRemain,
                Building = 999,
                Room = 9999,
            });

            // 每一格的用电量夜里少、白天多
            double step = (t.Hour is >= 21 or < 6 ? 0.10 : 0.24) + rnd.NextDouble() * 0.26;
            used += step;
            remain -= step;
            if (remain < 9)
            {
                remain += 60;                                // 模拟充了一次电费
                topUps.Add(t.AddMinutes(rnd.Next(0, 55)));
            }
            t = t.AddHours(1);
        }
        return topUps;
    }

    private static void Save(Form f, string path)
    {
        Anim.FinishAll();   // 别截到过渡动画的中间帧，出图得每次都一样
        f.Refresh();
        Application.DoEvents();
        // DrawToBitmap 连标题栏一起画，位图按整窗尺寸开，否则客户区底部会被裁掉
        using var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height));
        f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        bmp.Save(path, ImageFormat.Png);
    }
}
