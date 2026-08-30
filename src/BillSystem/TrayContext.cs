using System.Diagnostics;
using BillSystem.Models;
using BillSystem.Services;
using BillSystem.UI;

namespace BillSystem;

/// <summary>
/// 程序主体：托盘 + 任务栏组件 + 主窗口 + 后台轮询，都挂在这上面。
/// 主窗口关掉只是隐藏，后台还得继续记录用电。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _cfg;
    private readonly ElectricityApi _api = new();
    private readonly RechargeApi _rechargeApi = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _widgetToggle = new("显示任务栏组件") { CheckOnClick = true };
    private readonly MainForm _main;
    private readonly TaskbarWidget _widget;
    private readonly PollService _poll;

    private readonly ReadingStore _store;
    private readonly RechargeStore _recharges;
    private RechargeForm? _rechargeForm;
    private Icon? _trayIcon;
    private Color _trayColor = Color.Empty;
    private bool _lowNotified;
    private bool _syncing;
    private bool _mailFailed;   // 上一封发失败了，别每个整点都弹一次同样的错

    public TrayContext(bool silent)
    {
        _cfg = AppConfig.Load();
        _store = new ReadingStore(AppConfig.FixedBuilding, AppConfig.FixedRoom);
        _recharges = new RechargeStore(AppConfig.FixedBuilding, AppConfig.FixedRoom);

        _main = new MainForm(_cfg, _store, _recharges);
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

        _poll = new PollService(_api, _store, _cfg);
        _poll.StatusChanged += OnStatus;
        _poll.NewReading += OnNewReading;

        _main.RechargeRequested += OpenRecharge;
        _main.SettingsRequested += OpenSettings;

        ApplyWidgetVisibility();
        OnStatus(_poll.Status);

        if (!silent) _main.ShowAndFocus();
        _poll.Start();
        _ = SyncRechargesAsync();   // 开着的时候顺手把充值记录对一遍
    }

    private void BuildMenu()
    {
        _menu.RenderMode = ToolStripRenderMode.System;
        _menu.BackColor = Theme.PanelHi;
        _menu.ForeColor = Theme.Text;
        _menu.Font = Theme.FontBase;

        _menu.Items.Add("打开主界面", null, (_, _) => ShowMain());
        _menu.Items.Add("充值…", null, (_, _) => OpenRecharge());
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
        // 主界面原来在底下摆了一行数据目录的路径，那行小字删了，入口挪到这儿
        _menu.Items.Add("打开数据目录", null, (_, _) => OpenDataDir());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => ExitApp());
    }

    /// <summary>在资源管理器里打开数据目录。打不开就算了，不值得为这个弹个框。</summary>
    private static void OpenDataDir()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppConfig.DataDir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    private void ApplyWidgetVisibility()
    {
        if (_cfg.ShowWidget) _widget.Attach();
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

    private void OnStatus(PollStatus st)
    {
        Summary summary = UsageAggregator.Summarize(_store.Snapshot(), DateTime.Now);
        _widget.UpdateData(st, summary);
        _main.UpdateStatus(st);

        Color level = Theme.LevelColor(st.Latest?.Remaining, _cfg.LowThreshold);
        if (st.Latest is null || st.Error is not null) level = Theme.TextSub;
        SetTrayIcon(level);

        string room = $"{AppConfig.FixedBuilding}栋{AppConfig.FixedRoom}";
        string tip = st.Latest is { } r
            ? $"{room} 剩余 {r.Remaining:0.00} 度\n抄表 {r.MeterTime:MM-dd HH:mm}"
            : "宿舍电费助手 · 等待首次查询";
        if (st.Error is not null) tip = $"{room} · 更新失败";
        _tray.Text = tip.Length > 62 ? tip[..62] : tip;
    }

    private void OnNewReading(Reading r)
    {
        _main.RefreshData();

        // 剩余电量突然涨了，说明刚充过（可能是在手机上充的），把充值记录也对一下
        List<Reading> all = _store.Snapshot();
        if (all.Count >= 2 && r.Remaining > all[^2].Remaining + 0.5)
            _ = SyncRechargesAsync();

        if (!_cfg.LowAlertEnabled) return;

        Summary s = UsageAggregator.Summarize(all, DateTime.Now);
        bool low = r.Remaining <= _cfg.LowThreshold;
        // 度数还够，但照这个用法撑不到设置里那个天数（默认半天）就见底
        bool soon = s.RunsOutWithin(_cfg.LowDaysThreshold);

        if ((low || soon) && !_lowNotified)
        {
            _lowNotified = true;
            string room = $"{AppConfig.FixedBuilding}栋{AppConfig.FixedRoom}";
            Notify("电量不多了",
                low
                    ? $"{room} 只剩 {r.Remaining:0.00} 度（抄表 {r.MeterTime:MM-dd HH:mm}），记得充电费。"
                    : $"{room} 只剩 {r.Remaining:0.00} 度，照现在的用法约还能用 {s.DaysLeftText}，记得充电费。",
                ToolTipIcon.Warning);
            if (MailAlert.Configured(_cfg)) _ = SendLowMailAsync(r, s, low);
        }
        else if (!soon && r.Remaining > _cfg.LowThreshold * 1.2)
        {
            _lowNotified = false; // 充过电了，下次再低再提醒
            _mailFailed = false;
        }
    }

    /// <summary>低电量邮件。发不出去就弹一条通知说一声，但只说第一次。</summary>
    private async Task SendLowMailAsync(Reading r, Summary s, bool belowThreshold)
    {
        try
        {
            // 日均和"还能用多久"一起发，收到信就知道急不急
            await MailAlert.SendLowAsync(_cfg, r, s, belowThreshold);
            _mailFailed = false;
        }
        catch (Exception ex)
        {
            if (_mailFailed) return;
            _mailFailed = true;
            Notify("低电量邮件没发出去", $"{ex.Message}\n可以到设置里点“试一封邮件”看看。", ToolTipIcon.Warning);
        }
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
    /// 打开充值窗口（同一个实例反复用，别叠一堆窗口）。
    /// 付款成功后立刻查一次电量，剩余电量马上就能对上。
    /// </summary>
    private void OpenRecharge()
    {
        if (_rechargeForm is null || _rechargeForm.IsDisposed)
        {
            _rechargeForm = new RechargeForm(_cfg, _rechargeApi, _recharges);
            _rechargeForm.Icon = _trayIcon;
            _rechargeForm.PaidSuccessfully += () =>
            {
                _lowNotified = false;
                _mailFailed = false;
                _poll.Wake();
            };
        }

        _rechargeForm.Show();
        if (_rechargeForm.WindowState == FormWindowState.Minimized)
            _rechargeForm.WindowState = FormWindowState.Normal;
        _rechargeForm.BringToFront();
        _rechargeForm.Activate();
    }

    /// <summary>后台把学校那边的充值记录合并到本地一份。失败就下次再说，不打扰用户。</summary>
    private async Task SyncRechargesAsync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            List<RechargeRecord> list =
                await _rechargeApi.QueryHistoryAsync(_recharges.Building, _recharges.Room).ConfigureAwait(true);
            if (_recharges.Merge(list) == 0) return;

            if (_rechargeForm is { IsDisposed: false })
                _rechargeForm.ReloadLocal();   // 只重画列表，正扫着的码不能动
            // 图表上"这一格充过值"的绿柱子也跟着补上
            if (!_main.IsDisposed) _main.RefreshData();
        }
        catch (Exception)
        {
            // 充值记录拉不到不影响主功能
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OpenSettings()
    {
        Summary s = UsageAggregator.Summarize(_store.Snapshot(), DateTime.Now);
        using var dlg = new SettingsForm(_cfg, _api, s);
        dlg.TestNotifyRequested += () => Notify("这就是低电量提醒的样子",
            $"{AppConfig.FixedBuilding}栋{AppConfig.FixedRoom} 剩余电量低于阈值时，就会弹一条这样的通知。",
            ToolTipIcon.Info);
        if (dlg.ShowDialog(_main.Visible ? _main : null) != DialogResult.OK)
            return;

        _widget.ApplyConfig(_cfg);
        ApplyWidgetVisibility();
        _main.ApplyConfig(_cfg);
    }

    private void ExitApp()
    {
        _poll.Dispose();
        _tray.Visible = false;
        _widget.Detach();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
