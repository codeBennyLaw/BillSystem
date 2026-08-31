using BillSystem.Models;
using BillSystem.Services;
using BillSystem.UI;

namespace BillSystem;

/// <summary>
/// 程序主体：托盘 + 任务栏组件 + 主窗口 + 每间宿舍一份后台轮询，都挂在这上面。
/// 主窗口关掉只是隐藏，后台还得继续记录用电。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _cfg;
    private readonly ElectricityApi _api = new();
    private readonly RechargeApi _rechargeApi = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _menuRecharge = new("充值…");
    private readonly ToolStripMenuItem _widgetToggle = new("显示任务栏组件") { CheckOnClick = true };
    private readonly MainForm _main;
    private readonly TaskbarWidget _widget;

    /// <summary>记录着的每一间宿舍，顺序跟设置里那份名单一致。各自轮询、各自提醒。</summary>
    private readonly List<DormSession> _sessions = new();

    /// <summary>主界面上正看着的那一间。一间都没加时是 null。</summary>
    private DormSession? _cur;

    private RechargeForm? _rechargeForm;
    private Icon? _trayIcon;
    private Color _trayColor = Color.Empty;

    public TrayContext(bool silent)
    {
        _cfg = AppConfig.Load();

        _main = new MainForm(_cfg);
        _ = _main.Handle; // 先建句柄，保证 SynchronizationContext 已就绪

        _widget = new TaskbarWidget(_cfg);
        _widget.LeftClicked += ToggleMain;

        BuildMenu();

        _tray.ContextMenuStrip = _menu;
        _widget.WidgetMenu = _menu;
        _tray.Text = "宿舍电费助手";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.BalloonTipClicked += (_, _) => ShowMain();
        SetTrayIcon(Theme.Accent);

        _main.RechargeRequested += OpenRecharge;
        _main.SettingsRequested += OpenSettings;
        _main.DormSwitched += SelectDorm;

        SyncSessions();
        if (!silent) _main.ShowAndFocus();
    }

    /// <summary>
    /// 把跑着的宿舍对齐到设置里那份名单：新加的建起来开始轮询，删掉的停掉，留下的把提醒抄成新填的。
    /// 停掉只是不再查了，<b>jsonl 一个字都不动</b>——想清理得到设置的"数据"页里手动删。
    /// </summary>
    private void SyncSessions()
    {
        var keep = new List<DormSession>(_cfg.Dorms.Count);
        foreach (Dorm d in _cfg.Dorms)
        {
            DormSession? had = _sessions.FirstOrDefault(s => s.Dorm.Key == d.Key);
            if (had is null)
            {
                var born = new DormSession(d, _api);
                born.Poll.StatusChanged += st => OnStatus(born, st);
                born.Poll.NewReading += r => OnNewReading(born, r);
                born.Poll.Start();
                _ = SyncRechargesAsync(born);
                had = born;
            }
            else
            {
                // 保存时配置里换的是一份新的 Dorm 对象，这间已经在跑了，只把设置抄过来
                had.Dorm.CopyAlertsFrom(d);
            }
            keep.Add(had);
        }

        foreach (DormSession gone in _sessions.Where(s => !keep.Contains(s)).ToList())
            gone.Dispose();

        _sessions.Clear();
        _sessions.AddRange(keep);

        _cur = _sessions.FirstOrDefault(s => s.Dorm.Key == _cfg.CurrentDorm) ?? _sessions.FirstOrDefault();
        if (_cur is not null) _cfg.CurrentDorm = _cur.Dorm.Key;

        _main.SetDorms(_sessions, _cur);
        _widget.SetDorm(_cur?.Dorm);

        // 正开着的充值窗口跟着换房间；那一间被删了就把窗口收掉
        if (_rechargeForm is { IsDisposed: false })
        {
            if (_cur is null)
            {
                _rechargeForm.Close();
                _rechargeForm.Dispose();
                _rechargeForm = null;
            }
            else
            {
                _rechargeForm.Bind(_cur);
            }
        }

        RefreshCurrent();
    }

    /// <summary>主界面切换器点了另一间。</summary>
    private void SelectDorm(DormSession s)
    {
        if (ReferenceEquals(s, _cur) || !_sessions.Contains(s)) return;

        _cur = s;
        _cfg.CurrentDorm = s.Dorm.Key;
        _cfg.Save();

        _widget.SetDorm(s.Dorm);
        _main.Bind(s);
        if (_rechargeForm is { IsDisposed: false }) _rechargeForm.Bind(s);
        RefreshCurrent();
        _ = SyncRechargesAsync(s);
    }

    /// <summary>把正看着那一间眼下的状态铺到组件、托盘图标和提示上。</summary>
    private void RefreshCurrent()
    {
        _menuRecharge.Enabled = _cur is not null;
        ApplyWidgetVisibility();

        if (_cur is null)
        {
            SetTrayIcon(Theme.TextSub);
            _tray.Text = "宿舍电费助手 · 还没有添加宿舍";
            return;
        }

        OnStatus(_cur, _cur.Poll.Status);
    }

    private void BuildMenu()
    {
        _menu.RenderMode = ToolStripRenderMode.System;
        _menu.BackColor = Theme.PanelHi;
        _menu.ForeColor = Theme.Text;
        _menu.Font = Theme.FontBase;

        _menu.Items.Add("打开主界面", null, (_, _) => ShowMain());
        _menuRecharge.Click += (_, _) => OpenRecharge();
        _menu.Items.Add(_menuRecharge);
        _menu.Items.Add(new ToolStripSeparator());

        _widgetToggle.Checked = _cfg.ShowWidget;
        _widgetToggle.CheckedChanged += (_, _) =>
        {
            if (_cfg.ShowWidget == _widgetToggle.Checked) return;
            _cfg.ShowWidget = _widgetToggle.Checked;
            _cfg.Save();
            ApplyWidgetVisibility();
        };
        _menu.Items.Add(_widgetToggle);
        _menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        _menu.Items.Add("打开数据目录", null, (_, _) => DormFiles.OpenDataDir());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => ExitApp());
    }

    /// <summary>一间宿舍都没加的时候组件没数可显示，那就先不摆。</summary>
    private void ApplyWidgetVisibility()
    {
        if (_cfg.ShowWidget && _cur is not null) _widget.Attach();
        else _widget.Detach();
        _widgetToggle.Checked = _cfg.ShowWidget;
    }

    private void ToggleMain()
    {
        if (_main.Visible && _main.WindowState != FormWindowState.Minimized) _main.Hide();
        else ShowMain();
    }

    private void ShowMain()
    {
        _main.ShowAndFocus();
        _main.RefreshData();
    }

    /// <summary>某一间查完了。只有正看着的那一间往界面上写，其它的在后台安静记账。</summary>
    private void OnStatus(DormSession s, PollStatus st)
    {
        if (!ReferenceEquals(s, _cur)) return;

        _widget.UpdateData(st, s.Summarize());
        _main.UpdateStatus(st);

        Color level = Theme.LevelColor(st.Latest?.Remaining, s.Dorm.LowThreshold);
        if (st.Latest is null || st.Error is not null) level = Theme.TextSub;
        SetTrayIcon(level);

        string room = s.Dorm.Short;
        string tip = st.Latest is { } r
            ? $"{room} 剩余 {r.Remaining:0.00} 度\n抄表 {r.MeterTime:MM-dd HH:mm}"
            : $"{room} · 等待首次查询";
        if (st.Error is not null) tip = $"{room} · 更新失败";
        _tray.Text = tip.Length > 62 ? tip[..62] : tip;
    }

    /// <summary>某一间有了新读数。提醒是按间算的：这间提醒过了不耽误另一间。</summary>
    private void OnNewReading(DormSession s, Reading r)
    {
        if (ReferenceEquals(s, _cur) && !_main.IsDisposed) _main.RefreshData();

        List<Reading> all = s.Readings.Snapshot();

        // 剩余电量突然涨了，说明刚充过（可能是在手机上充的），把充值记录也对一下
        if (all.Count >= 2 && r.Remaining > all[^2].Remaining + 0.5) _ = SyncRechargesAsync(s);

        Summary sum = UsageAggregator.Summarize(all, DateTime.Now);
        Dorm d = s.Dorm;
        bool low = r.Remaining <= d.LowThreshold;
        // 度数还够，但照这个用法撑不到这间设的那个天数（那项是 0 就不看这一条）
        bool soon = sum.RunsOutWithin(d.LowDaysThreshold);

        if (low || soon)
        {
            if (s.LowNotified) return;
            s.LowNotified = true;

            if (d.NotifyEnabled)
                Notify("电量不多了",
                    low
                        ? $"{d.Short} 只剩 {r.Remaining:0.00} 度（抄表 {r.MeterTime:MM-dd HH:mm}），记得充电费。"
                        : $"{d.Short} 只剩 {r.Remaining:0.00} 度，照现在的用法约还能用 {sum.DaysLeftText}，记得充电费。",
                    ToolTipIcon.Warning);

            if (d.MailEnabled && MailAlert.Configured(_cfg, d)) _ = SendLowMailAsync(s, r, sum, low);
        }
        else if (r.Remaining > d.LowThreshold * 1.2)
        {
            // 充过电了，下次再低再提醒
            s.LowNotified = false;
            s.MailFailed = false;
        }
    }

    /// <summary>低电量邮件。发不出去就弹一条通知说一声，但同一轮只说第一次。</summary>
    private async Task SendLowMailAsync(DormSession s, Reading r, Summary sum, bool belowThreshold)
    {
        try
        {
            await MailAlert.SendLowAsync(_cfg, s.Dorm, r, sum, belowThreshold);
            s.MailFailed = false;
        }
        catch (Exception ex)
        {
            if (s.MailFailed) return;
            s.MailFailed = true;
            Notify($"{s.Dorm.Short} 的低电量邮件没发出去",
                $"{ex.Message}\n可以到设置里点“试一封邮件”看看。", ToolTipIcon.Warning);
        }
    }

    /// <summary>把学校那边的充值记录合并到本地。拉不到就下次再说，不打扰用户。</summary>
    private async Task SyncRechargesAsync(DormSession s)
    {
        if (await s.SyncRechargesAsync(_rechargeApi) == 0) return;
        if (!ReferenceEquals(s, _cur)) return;

        if (_rechargeForm is { IsDisposed: false })
            _rechargeForm.ReloadLocal();   // 只重画列表，正扫着的码不能动
        // 图表上"这一格充过值"的标记也跟着补上
        if (!_main.IsDisposed) _main.RefreshData();
    }

    /// <summary>
    /// 发一条 Windows 通知。Win10/11 会把托盘气泡渲染成系统通知（进通知中心），
    /// 前提是图标此刻真的在托盘里——所以先确保 Visible 再发。
    /// </summary>
    private void Notify(string title, string text, ToolTipIcon icon)
    {
        if (!_tray.Visible) _tray.Visible = true;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(8000);
    }

    private void SetTrayIcon(Color color)
    {
        if (color == _trayColor) return;
        _trayColor = color;

        Icon? old = _trayIcon;
        _trayIcon = Glyphs.CreateTrayIcon(color);
        _tray.Icon = _trayIcon;
        _main.Icon = _trayIcon;
        Glyphs.DestroyIcon(old);
    }

    /// <summary>
    /// 打开充值窗口（同一个实例反复用，别叠一堆窗口）。充的是主界面上正看着的那一间。
    /// 付款成功后立刻查一次电量，剩余电量马上就能对上。
    /// </summary>
    private void OpenRecharge()
    {
        if (_cur is not { } cur) return;

        if (_rechargeForm is null || _rechargeForm.IsDisposed)
        {
            _rechargeForm = new RechargeForm(_rechargeApi, _cfg, cur) { Icon = _trayIcon };
            _rechargeForm.PaidSuccessfully += () =>
            {
                foreach (DormSession s in _sessions) { s.LowNotified = false; s.MailFailed = false; }
                _cur?.Poll.Wake();
            };
        }

        _rechargeForm.Show();
        if (_rechargeForm.WindowState == FormWindowState.Minimized)
            _rechargeForm.WindowState = FormWindowState.Normal;
        _rechargeForm.BringToFront();
        _rechargeForm.Activate();
    }

    private void OpenSettings()
    {
        // 试邮件里要带上"这一间眼下剩多少"，所以按房号把汇总现算给设置窗口
        using var dlg = new SettingsForm(_cfg, _api,
            key => _sessions.FirstOrDefault(s => s.Dorm.Key == key)?.Summarize());
        dlg.TestNotifyRequested += d => Notify("这就是低电量提醒的样子",
            $"{d.Short} 剩余电量低于阈值时，就会弹一条这样的通知。", ToolTipIcon.Info);

        // 点了保存就立刻落地，窗口还开着，不用为了看效果先把设置关掉
        dlg.Saved += () =>
        {
            _widget.ApplyConfig(_cfg);
            _main.ApplyConfig(_cfg);
            SyncSessions();
        };

        dlg.ShowDialog(_main.Visible ? _main : null);
    }

    private void ExitApp()
    {
        foreach (DormSession s in _sessions) s.Dispose();
        _sessions.Clear();
        _tray.Visible = false;
        _widget.Detach();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (DormSession s in _sessions) s.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _widget.Dispose();
            _rechargeForm?.Dispose();
            _main.Dispose();
            _api.Dispose();
            _rechargeApi.Dispose();
            Glyphs.DestroyIcon(_trayIcon);
        }
        base.Dispose(disposing);
    }
}
