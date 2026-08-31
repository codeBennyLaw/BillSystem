using System.Drawing.Drawing2D;
using BillSystem.Models;
using BillSystem.Services;

namespace BillSystem.UI;

/// <summary>
/// 设置窗口，分四页：宿舍 / 提醒 / 组件 / 数据。所有改动先记在一份配置副本上，
/// 点"保存"才写回真正的配置并立刻生效——窗口留在原来那一页上，接着改接着存。
///
/// 提醒页上只有发件邮箱是几间共用的，<b>什么时候提醒、怎么提醒都是按间配的</b>。
/// 每页的东西都是切页时重新摆的，输入控件本身是常驻字段（切页只从 Controls 里摘下来，
/// 不销毁），填的值一直留着。分组卡画在窗口底图上（见 <see cref="Theme.IBackdropHost"/>），
/// 里面的控件贴的就是这张合成图，接缝才对得上。
/// </summary>
internal sealed class SettingsForm : Form, Theme.IBackdropHost
{
    private const int W = 560;
    private const int CardX = 10;               // 分组卡左右各留这么点边
    private const int CardR = W - CardX;
    private const int LabelX = 22;
    private const int InputX = 140;
    private const int RightX = W - 22;          // 内容右边界，右对齐的按钮贴着它
    private const int TestBtnW = 104;
    private const int DelBtnW = 60;
    private const int ContentTop = 58;          // 分页条底下从这儿开始排
    private const int SwapTop = 50;             // 换页交接图从这儿盖起（分页条不跟着淡）

    /// <summary>
    /// 底图一次按这个高度画好。窗口高度是跟着页里的内容变的，要是底图跟着窗口尺寸走，
    /// 每换一页都得重画一整张（几团 <see cref="PathGradientBrush"/>，还顺手把缓存挤空）。
    /// 比最高那一页再高一截，用多少裁多少。
    /// </summary>
    private const int BackdropH = 760;

    private enum Page
    {
        Dorms,
        Alert,
        Widget,
        Data,
    }

    private readonly AppConfig _cfg;
    private readonly AppConfig _draft;
    private readonly ElectricityApi _api;

    /// <summary>按房号取那一间眼下的汇总数字（试邮件里带上）。查不到就是还没数据。</summary>
    private readonly Func<string, Summary?>? _summaryOf;

    private readonly Segment _tabs = new() { AccessibleName = "设置分页" };
    private readonly UiButton _btnSave = new("保存", BtnKind.Primary);
    private readonly UiButton _btnClose = new("关闭");

    /// <summary>存过之后在按钮栏上留一句"已保存 HH:mm"，不然窗口不关就看不出存没存。</summary>
    private readonly UiLabel _saved = new()
    {
        Font = Theme.FontSmall,
        ForeColor = Theme.Good,
        TextAlign = ContentAlignment.MiddleRight,
    };

    private readonly UiText _newB = new()
        { DigitsOnly = true, MaxLength = 4, Placeholder = "楼栋", TextAlign = HorizontalAlignment.Center };

    private readonly UiText _newR = new()
        { DigitsOnly = true, MaxLength = 5, Placeholder = "房号", TextAlign = HorizontalAlignment.Center };

    private readonly UiButton _btnAddDorm = new("添加", BtnKind.Primary) { Radius = 6f };
    private readonly UiLabel _dormMsg = new();

    private readonly UiSpin _threshold = new() { Minimum = 0, Maximum = 1000, Decimals = 1, Step = 0.5 };
    private readonly UiSpin _daysLeft = new() { Minimum = 0, Maximum = 30, Decimals = 1, Step = 0.5 };
    private readonly UiText _mailFrom = new() { MaxLength = 64, Placeholder = "QQ 邮箱" };
    private readonly UiText _mailCode = new() { PasswordChar = '●', MaxLength = 64, Placeholder = "16 位授权码" };

    /// <summary>提醒页上"配哪一间"的切换器。通知 / 邮件 / 收件人都是跟着它换的。</summary>
    private readonly Segment _segAlertDorm = new() { AccessibleName = "配置哪一间" };

    /// <summary>正在配哪一间（<see cref="Dorm.Key"/>）。那一间被删了就落回第一间。</summary>
    private string _alertKey = "";

    /// <summary>
    /// 两个阈值输入框眼下显示的是哪一间的。<see cref="UiSpin"/> 没有"值变了"的事件，
    /// 所以只能在重摆页面和保存之前主动收一次（<see cref="StoreAlertSpins"/>），
    /// 不然换个宿舍、加个收件人，刚调好的阈值就跟着重摆没了。
    /// </summary>
    private string _spinsKey = "";

    private readonly UiToggle _tglNotify = new("弹 Windows 通知");
    private readonly UiButton _btnNotifyTest = new("试一条通知") { Radius = 6f };
    private readonly UiToggle _tglMail = new("发邮件到下面这些邮箱");
    private readonly UiButton _btnMailTest = new("试一封邮件") { Radius = 6f };
    private readonly UiLabel _mailResult = new();
    private readonly UiText _newMail = new() { MaxLength = 64, Placeholder = "收件邮箱" };
    private readonly UiButton _btnAddMail = new("添加", BtnKind.Primary) { Radius = 6f };

    private readonly UiToggle _tglWidget = new("在任务栏显示剩余电量");
    private readonly UiToggle _tglExtra = new("多显示一列今日 / 日均");
    private readonly UiSpin _offsetX = new() { Minimum = 0, Maximum = 2000, Step = 2 };
    private readonly UiToggle _tglAutoStart = new("开机自动启动");

    /// <summary>切页时只摘不销毁的那些（填过的值、试出来的结果都留着）。</summary>
    private readonly HashSet<Control> _keep = new();

    /// <summary>当前页上摆着的控件，切页时整批收走。</summary>
    private readonly List<Control> _shown = new();

    /// <summary>点"试一条通知"时发出，由托盘那边真的弹一条，用来确认系统通知没被关掉。</summary>
    public event Action<Dorm>? TestNotifyRequested;

    /// <summary>点了保存、配置已经写回去了。外面靠它立刻让新设置生效（窗口还开着）。</summary>
    public event Action? Saved;

    /// <summary>每组卡片的位置（画进底图用），以及正在排的那一组从哪儿开始。</summary>
    private readonly List<RectangleF> _cards = new();

    private int _y = ContentTop;
    private int _cardTop = -1;
    private int _barY;
    private Bitmap? _backdrop;

    /// <summary>眼下摆的是哪一页。换页才做那下交接动画，同一页重摆不做。</summary>
    private Page _page = Page.Dorms;

    /// <summary>正在淡的那张交接图。</summary>
    private PageSwap? _swap;

    /// <summary>窗口高度：换页时滑过去。</summary>
    private readonly Anim _hA;

    public SettingsForm(AppConfig cfg, ElectricityApi api, Func<string, Summary?>? summaryOf = null)
    {
        _cfg = cfg;
        _draft = cfg.Clone();
        _api = api;
        _summaryOf = summaryOf;
        _alertKey = _draft.CurrentDorm;

        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.FontBase;
        DoubleBuffered = true;
        ClientSize = new Size(W, 520);
        Theme.ApplyDarkChrome(this);
        _hA = new Anim(this, 520, 260, ApplyHeight);

        foreach (Control c in new Control[]
                 {
                     _newB, _newR, _btnAddDorm, _dormMsg,
                     _threshold, _daysLeft, _mailFrom, _mailCode,
                     _segAlertDorm, _tglNotify, _btnNotifyTest,
                     _tglMail, _btnMailTest, _mailResult, _newMail, _btnAddMail,
                     _tglWidget, _tglExtra, _offsetX, _tglAutoStart,
                 })
            _keep.Add(c);

        BuildFrame();
        LoadValues();
        WireEvents();
        ShowPage(Page.Dorms);
        Fade.In(this);
    }

    /// <summary>分页条和底部按钮栏是一直在的，只有中间那块跟着页换。</summary>
    private void BuildFrame()
    {
        foreach ((string text, Page p) in new[]
                 {
                     ("宿舍", Page.Dorms), ("提醒", Page.Alert),
                     ("组件", Page.Widget), ("数据", Page.Data),
                 })
            _tabs.Add(text, p);
        _tabs.AutoWidth(84);
        _tabs.SetBounds(CardX + 2, 14, _tabs.Width, 32);
        _tabs.SelectionChanged += tag => ShowPage((Page)tag);
        Controls.Add(_tabs);

        _btnSave.Click += (_, _) => Apply();
        Controls.Add(_btnSave);

        _btnClose.Click += (_, _) => Close();
        Controls.Add(_btnClose);

        Controls.Add(_saved);
    }

    /// <summary>保存：值写回真配置、外面立刻跟上，<b>窗口留在当前页</b>。</summary>
    private void Apply()
    {
        if (!SaveValues()) return;

        _saved.Flash($"已保存 {DateTime.Now:HH:mm}", Theme.Good, 3200);
        Saved?.Invoke();
    }

    private void WireEvents()
    {
        // 通知 / 邮件这两个开关是按间存的：拨一下就写进副本，换到另一间才不会丢
        _tglNotify.CheckedChanged += _ =>
        {
            if (AlertDorm is { } d) d.NotifyEnabled = _tglNotify.Checked;
            SyncEnabled();
        };
        _tglMail.CheckedChanged += _ =>
        {
            if (AlertDorm is { } d) d.MailEnabled = _tglMail.Checked;
            SyncEnabled();
        };
        _tglWidget.CheckedChanged += _ => SyncEnabled();

        _segAlertDorm.SelectionChanged += tag =>
        {
            if (tag is not string key || key == _alertKey) return;
            _alertKey = key;
            BeginInvoke(() => ShowPage(Page.Alert));
        };

        _btnAddDorm.Click += (_, _) => AddDorm();
        _newB.Submitted += AddDorm;
        _newR.Submitted += AddDorm;

        _btnAddMail.Click += (_, _) => AddMail();
        _newMail.Submitted += AddMail;

        _btnNotifyTest.Click += (_, _) =>
        {
            if (AlertDorm is { } d) TestNotifyRequested?.Invoke(d);
        };
        _btnMailTest.Click += async (_, _) => await TestMailAsync();
    }

    /// <summary>换页：上一页的控件收走，重新排这一页，窗口高度按内容定。</summary>
    private void ShowPage(Page p)
    {
        StoreAlertSpins();        // 重摆之前先把阈值收进副本，不然刚调的数就没了

        // 留着不销毁的那两条提示（上一页填错的话、上一封试信的结果）别跟着带到新页上
        _dormMsg.Text = "";
        _mailResult.Text = "";

        // 真换页才做交接（同一页只是换了间宿舍就不做：那时候切换器自己正滑着，盖上一张图会把它冻住）
        bool turning = p != _page && Visible;
        _swap?.Kill();
        _swap = null;
        int from = _barY;
        Bitmap? before = turning ? RenderPage(from) : null;

        SuspendLayout();
        _tabs.Select(p);

        foreach (Control c in _shown)
        {
            Controls.Remove(c);
            if (!_keep.Contains(c)) c.Dispose();
        }
        _shown.Clear();
        _cards.Clear();
        _cardTop = -1;
        _y = ContentTop;
        _page = p;

        switch (p)
        {
            case Page.Dorms: BuildDorms(); break;
            case Page.Alert: BuildAlert(); break;
            case Page.Widget: BuildWidget(); break;
            default: BuildData(); break;
        }

        _y += 6;
        CloseCard();
        LayoutBottomBar();
        SyncEnabled();
        ResumeLayout();
        Invalidate(true);

        if (before is not null)
        {
            int h = Math.Max(from, _barY);
            _swap = PageSwap.Play(this, new Rectangle(0, SwapTop, W, h - SwapTop),
                before, RenderPage(from));
        }
    }

    /// <summary>
    /// 把眼下这一页整个画成一张图：底图 + 这一页摆着的控件。
    /// 兄弟控件之间没法互相透出来，换页想淡入淡出就只能先各画一张，见 <see cref="PageSwap"/>。
    /// <paramref name="barY"/> 是那一页按钮栏的位置，图只画到那儿——按钮栏两边都一样，不用跟着淡。
    /// </summary>
    private Bitmap? RenderPage(int barY)
    {
        int h = Math.Max(barY, _barY) - SwapTop;
        if (h < 1) return null;
        try
        {
            var bmp = new Bitmap(W, h);
            using (Graphics g = Graphics.FromImage(bmp))
                g.DrawImage(BackdropImage, new Rectangle(0, 0, W, h),
                    new Rectangle(0, SwapTop, W, h), GraphicsUnit.Pixel);

            foreach (Control c in _shown)
            {
                var at = new Rectangle(c.Left, c.Top - SwapTop, c.Width, c.Height);
                if (c.Width < 1 || c.Height < 1 || at.Top < 0 || at.Bottom > h) continue;
                c.DrawToBitmap(bmp, at);
            }
            return bmp;
        }
        catch
        {
            // 画不出来就不做交接动画，页面照样能换
            return null;
        }
    }

    private void Put(Control c)
    {
        _shown.Add(c);
        Controls.Add(c);
    }

    /// <summary>出图用：切到第几页（0 宿舍 / 1 提醒 / 2 组件 / 3 数据）。</summary>
    internal void DevShowPage(int index) => ShowPage((Page)Math.Clamp(index, 0, 3));

    private void LayoutBottomBar()
    {
        int h = Math.Max(320, _y + 54);

        // 高度滑过去，不是"啪"地跳一下（第一次摆页面时窗口还没显示，直接就位）
        if (Visible && IsHandleCreated) _hA.To(h);
        else _hA.Set(h);

        _backdrop?.Dispose();
        _backdrop = null;
    }

    /// <summary>窗口高度动画的每一帧：按钮栏跟着底边走，那条分隔线也跟着挪。</summary>
    private void ApplyHeight(double v)
    {
        int h = Math.Max(1, (int)Math.Round(v));
        _barY = h - 54;

        _btnSave.SetBounds(W - 20 - 92, h - 43, 92, 34);
        _btnClose.SetBounds(_btnSave.Left - 10 - 84, h - 43, 84, 34);
        _saved.SetBounds(LabelX, h - 38, _btnClose.Left - 12 - LabelX, 24);

        if (ClientSize.Height != h) ClientSize = new Size(W, h);
        Invalidate();
    }

    // ---------- 宿舍 ----------

    private void BuildDorms()
    {
        Section("正在记录的宿舍");
        if (_draft.Dorms.Count == 0) Hint("还没有宿舍，在下面填楼栋和房号加一间。");
        else foreach (Dorm d in _draft.Dorms.ToList()) DormRow(d);

        Section("添加宿舍");
        AddDormRow();
    }

    /// <summary>一间一行：房号 + 查一下（真去接口试一次）+ 删除。</summary>
    private void DormRow(Dorm d)
    {
        var name = new UiLabel { Text = d.Label, ForeColor = Theme.Text };
        name.SetBounds(LabelX, _y, 136, 26);
        Put(name);

        var result = new UiLabel
        {
            Text = AlertSummary(d),
            Font = Theme.FontSmall,
            ForeColor = Theme.TextDim,
        };
        result.SetBounds(LabelX + 142, _y + 1, RightX - DelBtnW - 8 - 68 - 8 - LabelX - 142, 24);
        Put(result);

        var del = new UiButton("删除") { Radius = 6f };
        del.SetBounds(RightX - DelBtnW, _y - 3, DelBtnW, 32);
        del.Click += (_, _) => RemoveDorm(d);
        Put(del);

        var test = new UiButton("查一下") { Radius = 6f };
        test.SetBounds(del.Left - 8 - 68, _y - 3, 68, 32);
        test.Click += async (_, _) => await TestDormAsync(d, test, result);
        Put(test);

        _y += 36;
    }

    /// <summary>这一间眼下的提醒设置，写在房号后面，不用翻到提醒页去看。</summary>
    private static string AlertSummary(Dorm d)
    {
        var how = new List<string>(2);
        if (d.NotifyEnabled) how.Add("通知");
        if (d.MailEnabled) how.Add($"邮件 {d.MailTo.Count} 个");
        if (how.Count == 0) return "没开提醒";

        // 一行只有 230 像素，写紧一点
        string when = d.LowDaysThreshold > 0
            ? $"低于 {d.LowThreshold:0.##} 度 / {d.LowDaysThreshold:0.##} 天"
            : $"低于 {d.LowThreshold:0.##} 度";
        return $"{when} · {string.Join(" · ", how)}";
    }

    private void AddDormRow()
    {
        var lb = new UiLabel { Text = "楼栋 / 房号", ForeColor = Theme.TextSub };
        lb.SetBounds(LabelX, _y, InputX - LabelX - 8, 26);
        Put(lb);

        _newB.SetBounds(InputX, _y - 2, 76, 30);
        Put(_newB);
        var u1 = new UiLabel { Text = "栋", ForeColor = Theme.TextDim };
        u1.SetBounds(InputX + 82, _y, 20, 26);
        Put(u1);

        _newR.SetBounds(InputX + 106, _y - 2, 88, 30);
        Put(_newR);
        var u2 = new UiLabel { Text = "房间", ForeColor = Theme.TextDim };
        u2.SetBounds(InputX + 200, _y, 40, 26);
        Put(u2);

        _btnAddDorm.SetBounds(RightX - 76, _y - 3, 76, 32);
        Put(_btnAddDorm);
        _y += 36;

        Result(_dormMsg);
    }

    private void AddDorm()
    {
        if (!int.TryParse((_newB.Text ?? "").Trim(), out int b)
            || !int.TryParse((_newR.Text ?? "").Trim(), out int r))
        {
            Warn(_dormMsg, "楼栋和房号都要填");
            return;
        }

        var d = new Dorm(b, r);
        if (!d.Valid)
        {
            Warn(_dormMsg, "这个楼栋或房号不对");
            return;
        }
        if (_draft.Dorms.Any(x => x.Key == d.Key))
        {
            Warn(_dormMsg, "这间已经在名单里了");
            return;
        }

        _draft.Dorms.Add(d);
        if (_draft.CurrentDorm.Length == 0) _draft.CurrentDorm = d.Key;
        _alertKey = d.Key;          // 刚加的这间就是接下来要配提醒的那间
        _newB.Text = "";
        _newR.Text = "";
        BeginInvoke(() => ShowPage(Page.Dorms));
    }

    /// <summary>从名单里去掉一间。<b>它的 jsonl 一个字都不动</b>，要清理到"数据"页里来。</summary>
    private void RemoveDorm(Dorm d)
    {
        _draft.Dorms.RemoveAll(x => x.Key == d.Key);
        if (_draft.CurrentDorm == d.Key)
            _draft.CurrentDorm = _draft.Dorms.Count > 0 ? _draft.Dorms[0].Key : "";
        if (_alertKey == d.Key) _alertKey = _draft.CurrentDorm;
        BeginInvoke(() => ShowPage(Page.Dorms));
    }

    private async Task TestDormAsync(Dorm d, UiButton btn, UiLabel result)
    {
        btn.Enabled = false;
        result.ForeColor = Theme.TextSub;
        result.Text = "查询中…";
        try
        {
            Reading r = await _api.QueryAsync(d.Building, d.Room);
            if (result.IsDisposed) return;
            result.Flash($"剩余 {r.Remaining:0.00} 度 · 抄表 {r.MeterTime:MM-dd HH:mm}", Theme.Good, 6000);
        }
        catch (Exception ex)
        {
            if (result.IsDisposed) return;
            result.Flash(Clip(ex.Message, 30), Theme.Bad, 4000);
        }
        finally
        {
            if (!btn.IsDisposed) btn.Enabled = true;
        }
    }

    // ---------- 提醒 ----------

    /// <summary>正在配的那一间。名单空了是 null，那一间被删了就落回第一间。</summary>
    private Dorm? AlertDorm =>
        _draft.Dorms.FirstOrDefault(d => d.Key == _alertKey) ?? _draft.Dorms.FirstOrDefault();

    private void BuildAlert()
    {
        Dorm? d = AlertDorm;
        if (d is null)
        {
            Section("提醒");
            Hint("还没有宿舍，先到“宿舍”页加一间。");
            MailFromCard();
            return;
        }

        _alertKey = d.Key;
        LoadDormAlert(d);

        Section("配哪一间");
        DormPicker(d);

        Section("什么时候提醒");
        Row("剩余低于", _threshold, "度", 110);
        // 这一项是 0 就不看"还能用几天"
        Row("预计可用不足", _daysLeft, "天", 110);

        Section("怎么提醒");
        AddToggle(_tglNotify, _btnNotifyTest);
        AddToggle(_tglMail, _btnMailTest);
        Result(_mailResult);

        if (d.MailTo.Count == 0) Hint("还没有收件人，在下面加一个。");
        else foreach (string to in d.MailTo.ToList()) MailRow(d, to);
        AddMailRow();

        MailFromCard();
    }

    /// <summary>发件箱只有一个：一个授权码只对应一个邮箱，几间宿舍都从这儿发出去。</summary>
    private void MailFromCard()
    {
        Section("发件邮箱（几间共用）");
        Row("QQ 邮箱", _mailFrom, null, 250);
        Row("授权码", _mailCode, null, 250);
    }

    /// <summary>配哪一间：两间以上摆切换器，只有一间就写一行房间名。</summary>
    private void DormPicker(Dorm d)
    {
        if (_draft.Dorms.Count < 2)
        {
            var only = new UiLabel { Text = d.Label, ForeColor = Theme.Text };
            only.SetBounds(LabelX, _y, RightX - LabelX, 26);
            Put(only);
            _y += 32;
            return;
        }

        // 名单没变就一格都不动：每换一间宿舍这一页都要重摆，清空重来的话滑块会先弹回第一格
        _segAlertDorm.SetItems(_draft.Dorms.Select(x => (x.Short, (object)x.Key)), d.Key);
        _segAlertDorm.FitWidth(84);
        _segAlertDorm.SetBounds(LabelX, _y - 2, Math.Min(_segAlertDorm.Width, RightX - LabelX), 30);
        Put(_segAlertDorm);
        _y += 38;
    }

    private void LoadDormAlert(Dorm d)
    {
        _threshold.Value = d.LowThreshold;
        _daysLeft.Value = d.LowDaysThreshold;
        _spinsKey = d.Key;
        _tglNotify.SetSilently(d.NotifyEnabled);
        _tglMail.SetSilently(d.MailEnabled);
    }

    /// <summary>
    /// 把两个阈值收回它们属于的那一间：重摆页面前、保存前各收一次
    /// （<see cref="_spinsKey"/> 说了为什么）。
    /// </summary>
    private void StoreAlertSpins()
    {
        if (_draft.Dorms.FirstOrDefault(x => x.Key == _spinsKey) is not { } d) return;

        d.LowThreshold = _threshold.Value;
        d.LowDaysThreshold = _daysLeft.Value;
    }

    private void MailRow(Dorm d, string to)
    {
        var lb = new UiLabel { Text = to, ForeColor = Theme.Text };
        lb.SetBounds(LabelX, _y, RightX - DelBtnW - 12 - LabelX, 26);
        Put(lb);

        var del = new UiButton("删除") { Radius = 6f };
        del.SetBounds(RightX - DelBtnW, _y - 3, DelBtnW, 32);
        del.Click += (_, _) =>
        {
            d.MailTo.RemoveAll(x => string.Equals(x, to, StringComparison.OrdinalIgnoreCase));
            BeginInvoke(() => ShowPage(Page.Alert));
        };
        Put(del);

        _y += 34;
    }

    private void AddMailRow()
    {
        var lb = new UiLabel { Text = "新增收件", ForeColor = Theme.TextSub };
        lb.SetBounds(LabelX, _y, InputX - LabelX - 8, 26);
        Put(lb);

        _newMail.SetBounds(InputX, _y - 2, RightX - 76 - 10 - InputX, 30);
        Put(_newMail);

        _btnAddMail.SetBounds(RightX - 76, _y - 3, 76, 32);
        Put(_btnAddMail);
        _y += 36;
    }

    private void AddMail()
    {
        if (AlertDorm is not { } d) return;

        string s = (_newMail.Text ?? "").Trim();
        if (s.Length == 0) return;

        // 只挡明显不是邮箱的：真的能不能收到，点"试一封邮件"最清楚
        if (s.IndexOf('@') <= 0 || s.IndexOf('.', s.IndexOf('@')) < 0)
        {
            Warn(_mailResult, "这个地址不像邮箱");
            return;
        }
        if (d.MailTo.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase)))
        {
            Warn(_mailResult, "这个收件人已经有了");
            return;
        }

        d.MailTo.Add(s);
        _newMail.Text = "";
        BeginInvoke(() => ShowPage(Page.Alert));
    }

    /// <summary>拿现在填的发件邮箱和授权码，给正配的这一间真发一封，不用先保存。</summary>
    private async Task TestMailAsync()
    {
        StoreAlertSpins();        // 信里那句"提醒条件"要照眼下调的阈值写
        if (AlertDorm is not { } d) return;

        _btnMailTest.Enabled = false;
        _mailResult.ForeColor = Theme.TextSub;
        _mailResult.Text = "发送中…";

        AppConfig probe = _draft.Clone();
        probe.MailFrom = (_mailFrom.Text ?? "").Trim();
        probe.MailAuthCode = (_mailCode.Text ?? "").Trim();

        try
        {
            await MailAlert.SendTestAsync(probe, d, _summaryOf?.Invoke(d.Key));
            _mailResult.Flash($"已发出，{d.MailTo.Count} 个收件箱各收一下", Theme.Good, 5000);
        }
        catch (Exception ex)
        {
            _mailResult.Flash(Clip(ex.Message, 40), Theme.Bad, 5000);
        }
        finally
        {
            _btnMailTest.Enabled = _tglMail.Checked;
        }
    }

    // ---------- 组件 ----------

    private void BuildWidget()
    {
        Section("任务栏组件");
        AddToggle(_tglWidget);
        AddToggle(_tglExtra);
        Row("左侧偏移", _offsetX, "像素", 110);

        Section("其它");
        AddToggle(_tglAutoStart);
    }

    // ---------- 数据 ----------

    private void BuildData()
    {
        Section("没在记录名单里的数据");
        List<DormFiles> orphans = DormFiles.Orphans(_draft);
        if (orphans.Count == 0) Hint("数据目录里没有多余的记录文件。");
        else foreach (DormFiles f in orphans) OrphanRow(f);

        Section("数据目录");
        DirRow();
    }

    private void OrphanRow(DormFiles f)
    {
        var name = new UiLabel { Text = f.Dorm.Label, ForeColor = Theme.Text };
        name.SetBounds(LabelX, _y, 150, 26);
        Put(name);

        var detail = new UiLabel { Text = f.Detail, Font = Theme.FontSmall, ForeColor = Theme.TextDim };
        detail.SetBounds(LabelX + 156, _y + 1, RightX - DelBtnW - 12 - LabelX - 156, 24);
        Put(detail);

        var del = new UiButton("删除") { Radius = 6f };
        del.SetBounds(RightX - DelBtnW, _y - 3, DelBtnW, 32);
        del.Click += (_, _) => DeleteOrphan(f);
        Put(del);

        _y += 34;
    }

    /// <summary>真删文件，删了找不回来，所以先问一句。</summary>
    private void DeleteOrphan(DormFiles f)
    {
        if (MessageBox.Show(this,
                $"删掉 {f.Dorm.Label} 的记录文件？\n{f.Detail}\n删了就找不回来了。",
                "宿舍电费助手", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        if (!f.TryDelete(out string? err))
            MessageBox.Show(this, $"没删掉：{err}", "宿舍电费助手",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

        BeginInvoke(() => ShowPage(Page.Data));
    }

    private void DirRow()
    {
        var path = new UiLabel
        {
            Text = AppConfig.DataDir,
            Font = Theme.FontSmall,
            ForeColor = Theme.TextSub,
            PathEllipsis = true,
        };
        path.SetBounds(LabelX, _y, RightX - 76 - 10 - LabelX, 26);
        Put(path);

        var open = new UiButton("打开") { Radius = 6f };
        open.SetBounds(RightX - 76, _y - 3, 76, 32);
        open.Click += (_, _) => DormFiles.OpenDataDir();
        Put(open);

        _y += 36;
    }

    // ---------- 排版零件 ----------

    private void Section(string title)
    {
        CloseCard();

        var lb = new UiLabel { Text = title, Font = Theme.FontBold, ForeColor = Theme.Accent };
        lb.SetBounds(CardX + 12, _y, CardR - CardX - 24, 18);
        Put(lb);

        _y += 24;
        _cardTop = _y;
        _y += 10;                 // 卡内上边距
    }

    /// <summary>把上一组的卡框住：内容排到哪儿，卡就画到哪儿。</summary>
    private void CloseCard()
    {
        if (_cardTop < 0)
        {
            _y += _y == ContentTop ? 0 : 14;
            return;
        }

        _cards.Add(new RectangleF(CardX, _cardTop, CardR - CardX, _y + 10 - _cardTop));
        _cardTop = -1;
        _y += 10 + 14;            // 卡内下边距 + 两组之间的空隙
    }

    /// <summary>
    /// 一行：标签 + 输入框（+ 单位）。<paramref name="trailing"/> 给了就右对齐摆在同一行末尾。
    /// </summary>
    private void Row(string label, Control input, string? hint, int inputWidth = 120,
        Control? trailing = null)
    {
        var lb = new UiLabel { Text = label, ForeColor = Theme.TextSub };
        lb.SetBounds(LabelX, _y, InputX - LabelX - 8, 26);
        Put(lb);

        // 输入框比这一行的文字高 4 像素，往上挪 2 就跟标签一条中线了
        input.SetBounds(InputX, _y - 2, inputWidth, 30);
        Put(input);

        if (hint is not null)
        {
            var hintLabel = new UiLabel { Text = hint, ForeColor = Theme.TextDim };
            hintLabel.SetBounds(InputX + inputWidth + 10, _y, 72, 26);
            Put(hintLabel);
        }

        if (trailing is not null)
        {
            trailing.SetBounds(RightX - TestBtnW, _y - 3, TestBtnW, 32);
            Put(trailing);
        }

        _y += 36;
    }

    private void AddToggle(UiToggle t, Control? trailing = null)
    {
        int w = trailing is null ? CardR - LabelX - 12 : RightX - TestBtnW - 10 - LabelX;
        t.SetBounds(LabelX, _y, w, 28);
        Put(t);

        if (trailing is not null)
        {
            trailing.SetBounds(RightX - TestBtnW, _y - 2, TestBtnW, 32);
            Put(trailing);
        }

        _y += 32;
    }

    /// <summary>空的时候写一句，别让一张卡里什么都没有。</summary>
    private void Hint(string text)
    {
        var lb = new UiLabel { Text = text, Font = Theme.FontSmall, ForeColor = Theme.TextDim };
        lb.SetBounds(LabelX, _y, RightX - LabelX, 22);
        Put(lb);
        _y += 26;
    }

    /// <summary>"试一下"之后那句结果，挂在上一行下面、跟输入框左对齐。</summary>
    private void Result(UiLabel lb)
    {
        lb.Font = Theme.FontSmall;
        lb.SetBounds(InputX, _y - 7, RightX - InputX, 20);
        Put(lb);
        _y += 16;
    }

    /// <summary>填错时那句话：说完自己淡走，不赖在页面上。</summary>
    private static void Warn(UiLabel lb, string text) => lb.Flash(text, Theme.Warn, 3000);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---------- 底图 / 取值 ----------

    /// <summary>
    /// 窗口底图：柔光背景 + 几张分组玻璃卡，换页时重合成一次。
    /// 按钮栏那条分隔线不画进来——它跟着窗口高度动，画进图里等于每一帧都要重合成一张。
    /// </summary>
    public Bitmap BackdropImage => _backdrop ??= BuildBackdrop();

    private Bitmap BuildBackdrop()
    {
        var size = new Size(W, BackdropH);
        var bmp = new Bitmap(size.Width, size.Height);
        using Graphics g = Graphics.FromImage(bmp);
        g.DrawImageUnscaled(Theme.Backdrop(size), 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        foreach (RectangleF card in _cards) Theme.Glass(g, card, 16f, 0.05f);
        return bmp;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.DrawImageUnscaled(BackdropImage, 0, 0);
        using var line = new Pen(Color.FromArgb(26, 255, 255, 255));
        g.DrawLine(line, 0, _barY + 0.5f, W, _barY + 0.5f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _backdrop?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>关掉的开关下面那些输入框跟着灰掉，免得填了半天不起作用。</summary>
    private void SyncEnabled()
    {
        bool hasDorm = _draft.Dorms.Count > 0;
        _tglNotify.Enabled = hasDorm;
        _tglMail.Enabled = hasDorm;
        _btnNotifyTest.Enabled = hasDorm && _tglNotify.Checked;

        bool mail = hasDorm && _tglMail.Checked;
        _newMail.Enabled = mail;
        _btnAddMail.Enabled = mail;
        _btnMailTest.Enabled = mail;

        bool widget = _tglWidget.Checked;
        _offsetX.Enabled = widget;
        _tglExtra.Enabled = widget;
    }

    private void LoadValues()
    {
        _offsetX.Value = _draft.WidgetOffsetX;
        _mailFrom.Text = _draft.MailFrom;
        _mailCode.Text = _draft.MailAuthCode;

        _tglWidget.SetSilently(_draft.ShowWidget);
        _tglExtra.SetSilently(_draft.WidgetShowExtra);
        _tglAutoStart.SetSilently(_draft.StartWithWindows || Startup.IsEnabled());
        if (AlertDorm is { } d) LoadDormAlert(d);
    }

    /// <summary>
    /// 把界面上的值写进副本，再一次性覆盖到真配置。按间存的那几项拨的时候就已经写进副本了，
    /// 两个阈值靠 <see cref="StoreAlertSpins"/> 收。
    /// </summary>
    private bool SaveValues()
    {
        StoreAlertSpins();
        _draft.MailFrom = (_mailFrom.Text ?? "").Trim();
        _draft.MailAuthCode = (_mailCode.Text ?? "").Trim();
        _draft.ShowWidget = _tglWidget.Checked;
        _draft.WidgetShowExtra = _tglExtra.Checked;
        _draft.WidgetOffsetX = (int)_offsetX.Value;

        bool wantAutoStart = _tglAutoStart.Checked;
        if (wantAutoStart != Startup.IsEnabled() && !Startup.TrySet(wantAutoStart, out string? err))
            MessageBox.Show(this, $"设置开机自启失败：{err}\n其它设置已经保存。", "宿舍电费助手",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            _draft.StartWithWindows = wantAutoStart;

        _cfg.CopyFrom(_draft);
        _cfg.Save();
        return true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        // 回车相当于点保存，但焦点在输入框里时先让它自己收下这一下
        if (keyData == Keys.Enter && ActiveControl is not UiSpin && ActiveControl is not UiText)
        {
            Apply();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }
}
