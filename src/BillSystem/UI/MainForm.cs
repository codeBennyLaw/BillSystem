using BillSystem.Models;
using BillSystem.Services;

namespace BillSystem.UI;

internal sealed class MainForm : Form, IMessageFilter
{
    private readonly StatCard _cardRemain = new();
    private readonly StatCard _cardToday = new();
    private readonly StatCard _cardMonth = new();
    private readonly StatCard _cardAvg = new();
    private readonly StatCard _cardLeft = new();
    private readonly ChartControl _chart = new();

    private readonly UiLabel _title = new();
    private readonly UiLabel _room = new();
    private readonly UiLabel _status = new();

    private readonly UiButton _btnRecharge = new("充值", BtnKind.Primary);
    private readonly UiButton _btnSettings = new("设置");
    private readonly Segment _segG = new();

    /// <summary>鼠标停在卡片上时贴出来的那张"折成多少钱"。</summary>
    private readonly CardTip _tip = new();

    /// <summary>每张卡片对应小窗里写什么。没得算（比如只有一条读数）的那张就不摆。</summary>
    private readonly Dictionary<StatCard, (string Title, List<CardTip.Row> Rows)> _tips = new();

    /// <summary>宿舍切换器。只记录一间时不摆（没什么可切的），那时候写一行房间名。</summary>
    private readonly Segment _segDorm = new() { AccessibleName = "宿舍" };

    /// <summary>
    /// 摆的是切换器还是那行房间名。<b>不能拿 <c>_segDorm.Visible</c> 判断</b>：
    /// 窗口还没 Show 出来时，子控件的 Visible 读回来一律是 false，
    /// 那时候排版就会摆错，切换器留在左上角 (0,0) 压着标题。
    /// </summary>
    private bool _manyDorms;

    private AppConfig _cfg;

    /// <summary>正在看哪一间。一间都没加时是 null——图表和卡片照样画得出来，只是没有数。</summary>
    private DormSession? _cur;

    private PollStatus _status0 = new();

    /// <summary>
    /// 只为了把"已更新 · 刚刚"这句话写对：查询是半小时一次的，中间这句话会越来越不准。
    /// 窗口收起来就停，不白转。
    /// </summary>
    private readonly System.Windows.Forms.Timer _ageTick = new() { Interval = 30_000 };

    public event Action? RechargeRequested;
    public event Action? SettingsRequested;

    /// <summary>切换器上点了另一间宿舍。</summary>
    public event Action<DormSession>? DormSwitched;

    public MainForm(AppConfig cfg)
    {
        _cfg = cfg;

        Text = "宿舍电费助手";
        ClientSize = new Size(1080, 700);
        MinimumSize = new Size(940, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.FontBase;
        DoubleBuffered = true;
        Theme.ApplyDarkChrome(this);

        Build();
        Layout1();
        TabOrder();
        RefreshData();
        Application.AddMessageFilter(this);

        _ageTick.Tick += (_, _) => ApplyStatusText();
        VisibleChanged += (_, _) =>
        {
            _ageTick.Enabled = Visible;
            // 收窗口时鼠标正停在卡片上的话不会有 MouseLeave，小窗得自己收，不然下次打开还挂着
            _tip.HideTip();
            if (!Visible) return;
            ApplyStatusText();
            RevealCards();
        };
    }

    /// <summary>
    /// 窗口每次露脸时让五张卡片依次浮上来。隐藏着的时候动画会自己跳到终值，不白转。
    /// </summary>
    private void RevealCards()
    {
        StatCard[] cards = Cards;
        for (int i = 0; i < cards.Length; i++) cards[i].Reveal(i * 55);
    }

    /// <summary>
    /// 卡片和图表不进 Tab 环——它们没有可操作的东西，Tab 停在上面只是让人多按两下。
    /// </summary>
    private void TabOrder()
    {
        Control[] order = { _segDorm, _btnRecharge, _btnSettings, _segG };
        for (int i = 0; i < order.Length; i++)
        {
            order[i].TabStop = true;
            order[i].TabIndex = i;
        }
    }

    private void Build()
    {
        _title.Font = Theme.FontTitle;
        _title.ForeColor = Theme.Text;
        _title.Text = "宿舍电费助手";
        Controls.Add(_title);

        _room.Font = Theme.FontSmall;
        _room.ForeColor = Theme.TextDim;
        Controls.Add(_room);

        _segDorm.Visible = false;
        _segDorm.SelectionChanged += tag =>
        {
            if (tag is DormSession s && !ReferenceEquals(s, _cur)) DormSwitched?.Invoke(s);
        };
        Controls.Add(_segDorm);

        _status.ForeColor = Theme.TextSub;
        _status.TextAlign = ContentAlignment.MiddleRight;
        Controls.Add(_status);

        foreach ((string text, Granularity g) in new[]
                 {
                     ("小时", Granularity.Hour), ("日", Granularity.Day),
                     ("月", Granularity.Month), ("年", Granularity.Year),
                 })
            _segG.Add(text, g);
        _segG.Select(_cfg.Granularity);
        _segG.SelectionChanged += tag =>
        {
            _cfg.Granularity = (Granularity)tag;
            _cfg.Save();
            _chart.ScrollToEnd();
            RefreshData();
        };
        Controls.Add(_segG);

        _btnRecharge.Click += (_, _) => RechargeRequested?.Invoke();
        _btnSettings.Click += (_, _) => SettingsRequested?.Invoke();

        foreach (UiButton b in new[] { _btnSettings, _btnRecharge })
            Controls.Add(b);

        foreach (StatCard c in Cards)
        {
            c.Hovered += CardHovered;
            Controls.Add(c);
        }

        _chart.Granularity = _cfg.Granularity;
        Controls.Add(_chart);

        Controls.Add(_tip);

        Resize += (_, _) => { _tip.HideTip(); Layout1(); Invalidate(true); };
    }

    /// <summary>卡片上那几个度数折成钱：算得出来才贴小窗，贴的时候顺手把它挪到卡片底下。</summary>
    private void CardHovered(StatCard card, bool on)
    {
        if (!on) { _tip.HideTip(); return; }
        if (!_tips.TryGetValue(card, out (string Title, List<CardTip.Row> Rows) t)) { _tip.HideTip(); return; }
        _tip.ShowFor(card, t.Title, t.Rows);
    }

    private static string Money(double kwh) => $"{AppConfig.YuanOf(kwh):0.00} 元";

    /// <summary>
    /// 出图用：把某张卡片的"折成多少钱"小窗贴出来（没法在离屏渲染里真去悬停）。
    /// 返回小窗本身——<see cref="Control.DrawToBitmap"/> 不认 z 序，出图那边得让它最后再画一遍。
    /// </summary>
    internal Control DevShowTip(int card)
    {
        CardHovered(Cards[Math.Clamp(card, 0, Cards.Length - 1)], true);
        return _tip;
    }

    /// <summary>五张卡片，顺序就是界面上从左到右。拖窗口时每一帧都要遍历它，别每次都新开一个数组。</summary>
    private StatCard[] Cards => _cards ??= new[] { _cardRemain, _cardToday, _cardMonth, _cardAvg, _cardLeft };

    private StatCard[]? _cards;

    /// <summary>拖窗口改大小时整块一起画完再上屏，不然卡片和图表会各自闪一下。</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Interop.Win32.WS_EX_COMPOSITED;
            return cp;
        }
    }

    private const int Pad = 20;

    /// <summary>
    /// 窗口自己也要铺那张柔光底图：只填实色的话，卡片周围会露出一圈明显的接缝。
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
        => e.Graphics.DrawImageUnscaled(Theme.Backdrop(ClientSize), 0, 0);

    private void Layout1()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        int right = w - Pad;

        _btnRecharge.SetBounds(right - 92, 20, 92, 34);
        _btnSettings.SetBounds(_btnRecharge.Left - 10 - 80, 20, 80, 34);

        _title.SetBounds(Pad, 16, 420, 28);
        _status.SetBounds(Pad + 570, 24, Math.Max(60, _btnSettings.Left - Pad - 582), 26);

        // 标题底下这一行：两间以上摆切换器，只有一间（或一间都没有）就写一行字
        if (_manyDorms)
        {
            // 间数多了得掐住宽度，别顶到按钮上
            _segDorm.FitWidth(76);
            _segDorm.SetBounds(Pad, 42, Math.Min(_segDorm.Width, Math.Max(80, _btnSettings.Left - 12 - Pad)), 32);
        }
        else
        {
            _room.SetBounds(Pad + 2, 44, 560, 18);
        }

        // 五张卡片等分一行
        const int cardsY = 86, cardH = 104, gap = 14;
        int cardW = Math.Max(80, (w - Pad * 2 - gap * 4) / 5);
        StatCard[] cards = Cards;
        for (int i = 0; i < cards.Length; i++)
            cards[i].SetBounds(Pad + (cardW + gap) * i, cardsY, cardW, cardH);

        int rowB = cardsY + cardH + 16;
        _segG.AutoWidth(58);
        _segG.SetBounds(Pad, rowB, _segG.Width, 34);

        int chartTop = rowB + 34 + 14;
        _chart.SetBounds(Pad, chartTop, Math.Max(100, w - Pad * 2),
            Math.Max(80, h - Pad - chartTop));
    }

    private const int WmMouseWheel = 0x020A;

    /// <summary>
    /// 滚轮消息是发给焦点控件的，不是鼠标底下那个。图表不去抢焦点（点一下就抢走焦点很讨厌），
    /// 所以在这儿按鼠标位置把滚轮转给它。
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel || !Visible || ActiveForm != this) return false;
        if (_chart.IsDisposed || !_chart.Visible) return false;

        Point pt = _chart.PointToClient(Cursor.Position);
        if (!_chart.ClientRectangle.Contains(pt)) return false;

        _chart.ScrollByWheel((short)(m.WParam.ToInt64() >> 16));
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            _ageTick.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>宿舍名单变了（加了、删了、刚启动）就整条重摆切换器。</summary>
    public void SetDorms(IReadOnlyList<DormSession> sessions, DormSession? current)
    {
        // 名单没变就一格都不动：每保存一次设置都重排的话，滑块会先弹回第一格再滑回来
        _segDorm.SetItems(sessions.Select(s => (s.Dorm.Short, (object)s)), current);

        bool many = sessions.Count > 1;
        _manyDorms = many;
        _segDorm.Visible = many;
        _room.Visible = !many;
        Bind(current);
    }

    /// <summary>换到某一间。一间都没加时传 null，界面照样立得住，只是没有数。</summary>
    public void Bind(DormSession? s)
    {
        bool switched = !ReferenceEquals(s, _cur);
        _cur = s;
        if (s is not null) _segDorm.Select(s);

        _status0 = s?.Poll.Status ?? new PollStatus();
        _btnRecharge.Enabled = s is not null;
        _room.Text = s?.Dorm.Label ?? "还没有添加宿舍：到设置里加一间";

        _tip.HideTip();
        Layout1();
        _chart.ScrollToEnd();
        ApplyStatusText();
        RefreshData();

        // 换了一间就当重新进来一次：卡片再铺一遍，图表也跟着重新长（换粒度是同一套动效）
        if (switched) RevealCards();
        Invalidate(true);
    }

    /// <summary>把整段历史（有上限）交给图表，之后左右滑动都由图表自己管。</summary>
    public void RefreshData()
    {
        Granularity g = _cfg.Granularity;
        DateTime now = DateTime.Now;
        List<Reading> all = _cur?.Readings.Snapshot() ?? new List<Reading>();

        int span = UsageAggregator.DefaultWindow(g);
        DateTime end = UsageAggregator.Step(UsageAggregator.Floor(now, g), g, 1);

        // 从第一条读数画到现在；至少凑够一屏，也别回看到没边（小时粒度尤其容易爆桶数）
        DateTime start = all.Count > 0 ? UsageAggregator.Floor(all[0].SlotTime, g) : UsageAggregator.Step(end, g, -span);
        DateTime oneScreen = UsageAggregator.Step(end, g, -span);
        if (start > oneScreen) start = oneScreen;

        DateTime limit = g switch
        {
            Granularity.Hour => UsageAggregator.Step(end, g, -24 * 90),
            Granularity.Day => UsageAggregator.Step(end, g, -365 * 3),
            Granularity.Month => UsageAggregator.Step(end, g, -12 * 10),
            _ => UsageAggregator.Step(end, g, -40),
        };
        if (start < limit) start = limit;

        _chart.Granularity = g;
        _chart.Span = span;
        _chart.SeriesKey = $"{_cur?.Dorm.Key}|{g}";
        _chart.EmptyText = _cur is null ? "还没有添加宿舍"
            : all.Count == 0 ? "还没有历史数据"
            : "这段时间没有数据";
        _chart.Data = UsageAggregator.Build(all, g, start, end, _cur?.Recharges.Snapshot());

        UpdateCards(UsageAggregator.Summarize(all, now));
    }

    /// <summary>后台查询状态变化时调用（只有正看着的那一间会传进来）。</summary>
    public void UpdateStatus(PollStatus st)
    {
        _status0 = st;
        ApplyStatusText();
        UpdateCards(UsageAggregator.Summarize(_cur?.Readings.Snapshot() ?? new List<Reading>(), DateTime.Now));
    }

    /// <summary>右上角那句状态。单独拎出来是因为"多久之前"要随时间自己走。</summary>
    private void ApplyStatusText()
    {
        PollStatus st = _status0;
        _status.Say(
            st.Busy ? "正在查询…"
                : st.Error is not null ? $"更新失败：{Clip(st.Error, 34)}"
                : st.LastSuccess is { } t ? $"已更新 · {HumanAge(t)}"
                : "等待首次查询",
            st.Error is not null ? Theme.Bad
                : st.Busy ? Theme.Accent
                : Theme.TextSub);
    }

    private void UpdateCards(Summary s)
    {
        // 只有一条读数时用电量算不出来（要两条相减），那几格写"--"，别摆个 0.00 让人以为没用电
        bool none = !s.UsageKnown;
        _tips.Clear();

        _cardRemain.Title = "剩余电量";
        _cardRemain.Unit = "度";
        // 查询失败右上角已经写明了，这儿照旧写抄表时间——它说明数据停在哪一刻，
        // 换成"上次更新失败"反倒把唯一有用的那个时间盖掉
        _cardRemain.Set(
            s.Remaining is { } rem ? rem.ToString("0.00") : "--",
            s.MeterTime is { } mt ? $"抄表 {mt:MM-dd HH:mm}" : "",
            // 阈值是这一间自己的
            Theme.LevelColor(s.Remaining, _cur?.Dorm.LowThreshold ?? 0));

        _cardToday.Title = "今日用电";
        _cardToday.Unit = "度";
        _cardToday.Set(none ? "--" : s.Today.ToString("0.00"),
            none ? "" : $"昨日 {s.Yesterday:0.00} 度", Theme.Accent);

        _cardMonth.Title = $"{DateTime.Now.Month} 月用电";
        _cardMonth.Unit = "度";
        _cardMonth.Set(none ? "--" : s.ThisMonth.ToString("0.00"),
            s.TotalUsed is { } tu ? $"累计 {tu:0.0} 度" : "", Theme.Accent);

        _cardAvg.Title = "日均用电";
        _cardAvg.Unit = "度";
        _cardAvg.Set(s.AvgDaily is { } avg ? avg.ToString("0.00") : "--",
            s.AvgDaily is null ? "" : $"近 {s.AvgSpanDays:0.0} 天",
            Theme.Text);

        // 鼠标停上去就把度数折成钱：卡片上写多少度，小窗里就一行一行对着写多少钱
        if (!none)
        {
            _tips[_cardToday] = ("今日电费", new List<CardTip.Row>
            {
                new("今日", Money(s.Today)),
                new("昨日", Money(s.Yesterday)),
            });

            var monthRows = new List<CardTip.Row> { new("本月", Money(s.ThisMonth)) };
            if (s.TotalUsed is { } tu2) monthRows.Add(new CardTip.Row("累计", Money(tu2)));
            _tips[_cardMonth] = ($"{DateTime.Now.Month} 月电费", monthRows);
        }

        if (s.AvgDaily is { } avg2)
            _tips[_cardAvg] = ("日均电费", new List<CardTip.Row>
            {
                new("每天", Money(avg2)),
                new($"近 {s.AvgSpanDays:0.0} 天", Money(avg2 * s.AvgSpanDays)),
            });

        _cardLeft.Title = "预计可用";
        // 不到一天就换成小时说：剩 0.4 天不如"9.6 小时"来得有数
        (string leftValue, string leftUnit) = s.DaysLeftParts ?? ("--", "天");
        _cardLeft.Unit = leftUnit;
        _cardLeft.Set(leftValue,
            s.RunOutDate is { } ro ? $"约到 {ro:MM-dd HH:mm}" : "",
            s.DaysLeft is { } d2 ? (d2 <= 3 ? Theme.Bad : d2 <= 7 ? Theme.Warn : Theme.Good) : Theme.TextDim);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string HumanAge(DateTime t)
    {
        TimeSpan d = DateTime.Now - t;
        if (d < TimeSpan.FromSeconds(45)) return "刚刚";
        if (d < TimeSpan.FromMinutes(60)) return $"{d.TotalMinutes:0} 分钟前";
        if (d < TimeSpan.FromHours(24)) return $"{d.TotalHours:0} 小时前";
        return t.ToString("MM-dd HH:mm");
    }

    /// <summary>关窗口只是收进托盘，后台还得继续记用电。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>Esc 跟点右上角那个 × 一样：收进托盘，不退程序。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void ShowAndFocus()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    /// <summary>设置窗口点了保存之后同步过来。</summary>
    public void ApplyConfig(AppConfig cfg)
    {
        _cfg = cfg;
        _segG.Select(cfg.Granularity);
        _tip.HideTip();
        _chart.ScrollToEnd();
        Layout1();
        RefreshData();
    }
}
