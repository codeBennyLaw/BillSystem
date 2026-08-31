using System.Drawing.Imaging;
using BillSystem.Models;
using BillSystem.Services;
using BillSystem.UI;

namespace BillSystem.Dev;

/// <summary>
/// 开发用出图：<c>BillSystem.exe --screenshot [目录]</c>。
/// 用一份假数据把主窗口和任务栏组件各自渲染成 PNG，方便离屏检查排版，不截屏。
/// 数据全落在 <see cref="AppConfig.UseSandbox"/> 开的沙盒目录里，真实记录一个字都不碰。
/// </summary>
internal static class DevShot
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        using var api = new ElectricityApi();
        // 出图用假房号和假邮箱，别把真实数据和真邮箱印进 docs 里的图
        var dorm = new Dorm(999, 9999)
        {
            LowThreshold = 5,
            LowDaysThreshold = 0.5,
            NotifyEnabled = true,
            MailEnabled = true,
            MailTo = { "someone@example.com", "roommate@example.com" },
        };
        // 两间的阈值各配一套，宿舍页那行摘要才看得出提醒是按间算的
        var one = new Dorm(998, 9998) { LowThreshold = 15, NotifyEnabled = true };

        // 两间：主界面上就能看到宿舍切换器（只有一间时那儿是一行房间名）
        var session = new DormSession(dorm, api);
        var oneSession = new DormSession(one, api);

        List<DateTime> topUps = Seed(session.Readings);

        // 充值记录跟假读数里"剩余电量突然涨上去"的时刻对上，图上的标记才跟真程序一个意思
        RechargeStore payStore = session.Recharges;
        payStore.Merge(TopUpRecords(topUps));

        var cfg = new AppConfig
        {
            Dorms = { dorm, one },
            CurrentDorm = dorm.Key,
            Granularity = Granularity.Day,
            MailFrom = "someone@example.com",
        };

        var form = new MainForm(cfg)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
        };
        form.ClientSize = new Size(1080, 700);
        form.SetDorms(new[] { session, oneSession }, session);
        form.Show();
        Application.DoEvents();

        Reading last = session.Readings.Latest!;
        var status = new PollStatus { Latest = last, LastSuccess = DateTime.Now, LastAttempt = DateTime.Now };
        form.UpdateStatus(status);
        Save(form, Path.Combine(outDir, "main-day.png"));

        // 卡片折成钱的小窗：离屏渲染里没法真拿鼠标去悬停，让主窗口自己贴一张出来。
        // 下一张换粒度时 ApplyConfig 会把它收掉，不会跟着后面几张一起入镜
        Save(form, Path.Combine(outDir, "main-card-tip.png"), form.DevShowTip(1));

        cfg.Granularity = Granularity.Hour;
        form.ApplyConfig(cfg);
        form.UpdateStatus(status);
        Save(form, Path.Combine(outDir, "main-hour.png"));

        cfg.Granularity = Granularity.Month;
        form.ApplyConfig(cfg);
        form.UpdateStatus(status);
        Save(form, Path.Combine(outDir, "main-month.png"));

        cfg.Granularity = Granularity.Day;
        form.ApplyConfig(cfg);

        // 只有一条抄表记录的样子：用电量算不出来，但图表不该整块空白
        DateTime oneSlot = Reading.SlotOf(DateTime.Now);
        oneSession.Readings.TryAdd(new Reading
        {
            SlotTime = oneSlot,
            MeterTime = DateTime.Now.Date.AddHours(9).AddMinutes(45),
            FetchedAt = DateTime.Now,
            Used = 3343.10,
            Remaining = 31.68,
            Building = one.Building,
            Room = one.Room,
        });

        form.Bind(oneSession);
        form.UpdateStatus(new PollStatus
        {
            Latest = oneSession.Readings.Latest,
            LastSuccess = DateTime.Now,
            LastAttempt = DateTime.Now,
        });
        Save(form, Path.Combine(outDir, "main-single-reading.png"));

        form.Bind(session);
        form.UpdateStatus(status);
        form.Hide();

        // 任务栏组件：离屏冻结成实测的 43px 高，不去贴真任务栏，免得在用户桌面上闪一下
        var widget = new TaskbarWidget(cfg) { Location = new Point(-4000, -4000) };
        widget.SetDorm(dorm);
        widget.DevFreeze(43);
        widget.Show();
        widget.UpdateData(status, UsageAggregator.Summarize(session.Readings.Snapshot(), DateTime.Now));
        widget.DevFreeze(43);
        Application.DoEvents();
        Save(widget, Path.Combine(outDir, "widget.png"));
        widget.Hide();

        // 组件的悬停信息卡：直接拿组件自己那张（内容已经在 UpdateData 里填好），免得图跟真界面对不上
        WidgetTip tip = widget.DevTip();
        tip.Location = new Point(-4000, -4000);
        tip.Show();
        tip.DevFreeze();
        Application.DoEvents();
        Save(tip, Path.Combine(outDir, "widget-tip.png"));
        tip.Hide();

        // 设置窗口：四页各出一张。"数据"页列的是名单外的历史文件，先造一间不在名单里的假房号出来
        StrayFiles();
        var dlg = new SettingsForm(cfg, api)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
        };
        dlg.Show();
        Application.DoEvents();
        foreach ((int page, string name) in new[]
                 {
                     (0, "settings-dorms"), (1, "settings-alert"),
                     (2, "settings-widget"), (3, "settings-data"),
                 })
        {
            dlg.DevShowPage(page);
            Application.DoEvents();
            Save(dlg, Path.Combine(outDir, $"{name}.png"));
        }
        dlg.Hide();
        dlg.Dispose();

        // 提醒邮件的排版：两种触发条件各写一份 HTML，浏览器直接打开就能看，不用真发信。
        // 数字自己凑一套齐的——拿真读数改一个剩余度数出来的话，"剩 4.2 度"和"还能用 4.8 天"会对不上
        var lowNow = new Reading
        {
            SlotTime = last.SlotTime,
            MeterTime = last.MeterTime,
            FetchedAt = last.FetchedAt,
            Used = last.Used,
            Remaining = 4.2,
            Building = dorm.Building,
            Room = dorm.Room,
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
            MailAlert.LowLetter(dorm, lowNow, mailSummary, belowThreshold: true).Html, utf8);
        File.WriteAllText(Path.Combine(outDir, "mail-soon.html"),
            MailAlert.LowLetter(dorm, lowNow, mailSummary, belowThreshold: false).Html, utf8);

        // 充值窗口：offline 保证不联网、不下单。再补一批假记录，好把记录列表填满
        payStore.Merge(SeedRecharges());

        using var payApi = new RechargeApi();
        var pay = new RechargeForm(payApi, cfg, session, offline: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
        };
        pay.Show();
        Application.DoEvents();
        pay.DevPose("等待付款 · 这张码还有 4:37");
        Save(pay, Path.Combine(outDir, "recharge.png"));

        // 点"生成付款码"之后弹的那个码窗
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
        session.Dispose();
        oneSession.Dispose();
        return 0;
    }

    /// <summary>
    /// 造一间"名单里没有、文件还在"的假宿舍，好让设置的"数据"页有东西可列。
    /// 行数是照着真实文件的量级编的，内容只用来数行，不会被解析。
    /// </summary>
    private static void StrayFiles()
    {
        var stray = new Dorm(997, 9997);
        DateTime t = DateTime.Now.Date.AddDays(-30);

        var readings = new List<string>();
        for (int i = 0; i < 312; i++)
        {
            DateTime at = t.AddHours(i * 2);
            readings.Add($"{{\"SlotTime\":\"{at:s}\",\"MeterTime\":\"{at:s}\",\"FetchedAt\":\"{at:s}\","
                         + $"\"Used\":{2100 + i * 0.4:0.00},\"Remaining\":{60 - i % 55 * 0.9:0.00},"
                         + $"\"Building\":{stray.Building},\"Room\":{stray.Room}}}");
        }

        var pays = new List<string>();
        for (int i = 0; i < 12; i++)
        {
            DateTime at = t.AddDays(i * 2.5);
            pays.Add($"{{\"OrderCode\":\"OLD{i:00}\",\"PayTime\":\"{at:s}\",\"PayCent\":5000,"
                     + $"\"PayMethod\":\"code\",\"PayResult\":\"已完成\","
                     + $"\"Building\":{stray.Building},\"Room\":{stray.Room}}}");
        }

        File.WriteAllLines(Path.Combine(AppConfig.DataDir, $"readings-{stray.Key}.jsonl"), readings);
        File.WriteAllLines(Path.Combine(AppConfig.DataDir, $"recharges-{stray.Key}.jsonl"), pays);
    }

    /// <summary>假读数里每一次"剩余电量涨上去"都配一条充值记录，图表上才标得出那几笔。</summary>
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
    /// 照学校那边的真实节奏：<b>两个钟才抄一次表</b>，中间那个整点查到的是同一份读数——
    /// 出图正好能看出程序有没有把这种重复读数收拢掉。
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

    private static void Save(Form f, string path, Control? over = null)
    {
        Anim.FinishAll();   // 别截到过渡动画的中间帧，出图得每次都一样
        f.Refresh();
        Application.DoEvents();
        // DrawToBitmap 连标题栏一起画，位图按整窗尺寸开，否则客户区底部会被裁掉
        using var bmp = new Bitmap(Math.Max(1, f.Width), Math.Max(1, f.Height));
        f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));

        // 悬浮的小窗盖在别的控件上面，但 DrawToBitmap 是按控件挨个画的、不认 z 序，
        // 会被后画的兄弟控件糊掉一角——单独再画一遍，位置按客户区在整窗里的偏移算
        if (over is { Visible: true, Width: > 0, Height: > 0 })
        {
            Point client = f.PointToScreen(Point.Empty);
            var at = new Rectangle(
                client.X - f.Left + over.Left, client.Y - f.Top + over.Top, over.Width, over.Height);
            over.DrawToBitmap(bmp, at);
        }

        bmp.Save(path, ImageFormat.Png);
    }
}
