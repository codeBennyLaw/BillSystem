using System.Drawing.Drawing2D;
using BillSystem.Models;
using BillSystem.Services;

namespace BillSystem.UI;

/// <summary>
/// 充值窗口：上面一张玻璃卡里选金额 + 生成付款码，下面整块是这个房间的充值记录。
/// 码不画在这个窗口里，而是弹一个 <see cref="QrDialog"/>。学校那边的码只活几分钟，过期就自动
/// 重新下一单换一张；取消（或关掉弹窗）之后再申请就是全新的一单。下单之后每 3 秒查一次订单。
/// </summary>
internal sealed class RechargeForm : Form, Theme.IBackdropHost
{
    private static readonly int[] Presets = { 10, 30, 50, 100, 200, 300, 500 };

    private const int W = 860;
    private const int H = 560;
    private const int Pad = 20;
    private const int CardBottom = 152;      // 上面那张卡到哪儿

    private readonly RechargeApi _api;

    /// <summary>关窗口时把位置记在这儿。</summary>
    private readonly AppConfig _cfg;

    /// <summary>充的是这一间。主界面切了宿舍就 <see cref="Bind"/> 换过来。</summary>
    private DormSession _s;

    private RechargeStore Store => _s.Recharges;

    private readonly UiLabel _who = new();
    private readonly List<UiButton> _presetBtns = new();
    private readonly UiText _custom = new()
    {
        DigitsOnly = true,
        MaxLength = 4,
        Placeholder = "自定义",
        TextAlign = HorizontalAlignment.Center,
    };
    private readonly UiButton _btnPay = new("生成付款码", BtnKind.Primary);
    private readonly UiLabel _state = new();

    private readonly UiLabel _histTitle = new();
    private readonly RecordList _list = new();
    private readonly UiLabel _histSum = new();

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    private QrDialog? _dlg;
    private int _yuan = 30;
    private int _preset = 30;       // 最近点过的预设，输入框清空时回到它
    private bool _amountOk = true;
    private bool _busy;             // 正在下单，别重入
    private int _orderGen;          // 第几次下单：旧的那次收尾时别去动新的那次的状态
    private RechargeOrder? _order;
    private CancellationTokenSource? _work;
    private DateTime _lastPoll = DateTime.MinValue;
    private bool _paid;

    /// <summary>出图用：一次都不许联网。</summary>
    private readonly bool _offline;

    /// <summary>上一次从学校那边拉记录是什么时候，用来挡住重复拉。</summary>
    private DateTime _lastHist = DateTime.MinValue;

    /// <summary>
    /// 窗口活着期间的总闸：关掉窗口就把还在飞的下单/查单/拉历史一起取消。
    /// 链出去的子 CTS 可能比它活得久，所以这个不 Dispose。
    /// </summary>
    private readonly CancellationTokenSource _life = new();

    /// <summary>充值成功后触发，让外面立刻查一次电量。</summary>
    public event Action? PaidSuccessfully;

    public RechargeForm(RechargeApi api, AppConfig cfg, DormSession session, bool offline = false)
    {
        _api = api;
        _cfg = cfg;
        _s = session;
        _offline = offline;

        Text = "电费充值";
        ClientSize = new Size(W, H);
        MinimumSize = new Size(W, H);
        MaximumSize = new Size(W + 400, H + 400);
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.FontBase;
        DoubleBuffered = true;
        Theme.ApplyDarkChrome(this);
        RestorePlace();

        Build();
        Layout1();
        TabOrder();
        Resize += (_, _) => Layout1();

        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        ShowLocal();
        if (!offline) _ = ReloadHistoryAsync();   // offline 是出图用的，别联网

        // 窗口是同一个实例反复用的，再露脸时对一遍状态：刚付完那一刻学校那边常常还写着"处理中"
        VisibleChanged += (_, _) =>
        {
            if (Visible && (DateTime.Now - _lastHist).TotalSeconds > 5) _ = ReloadHistoryAsync();
        };
        Fade.In(this);
    }

    /// <summary>Tab 顺序照界面来：先挑金额，再下单。</summary>
    private void TabOrder()
    {
        int i = 0;
        foreach (UiButton b in _presetBtns) b.TabIndex = i++;
        _custom.TabIndex = i++;
        _btnPay.TabIndex = i;
    }

    /// <summary>
    /// 摆回上次关掉时的位置。第一次打开（还没记过）才摆在主窗口正中；
    /// 记下的位置可能已经不在屏幕上了（换了显示器、拔了扩展屏），夹回可见区域再用。
    /// </summary>
    private void RestorePlace()
    {
        if (!_cfg.HasRechargePos)
        {
            StartPosition = FormStartPosition.CenterParent;
            return;
        }

        StartPosition = FormStartPosition.Manual;
        var at = new Point(_cfg.RechargeX, _cfg.RechargeY);
        Rectangle work = Screen.FromPoint(at).WorkingArea;
        Location = new Point(
            Math.Clamp(at.X, work.Left, Math.Max(work.Left, work.Right - Width)),
            Math.Clamp(at.Y, work.Top, Math.Max(work.Top, work.Bottom - Height)));
    }

    /// <summary>关窗口时记下位置，下次打开还在这儿。最小化/最大化着关就记还原后的那个位置。</summary>
    private void RememberPlace()
    {
        Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        if (_cfg.RechargeX == b.X && _cfg.RechargeY == b.Y) return;
        _cfg.RechargeX = b.X;
        _cfg.RechargeY = b.Y;
        _cfg.Save();
    }

    /// <summary>出图用：主窗口摆一句状态，不下单、不联网。</summary>
    internal void DevPose(string state)
    {
        _tick.Stop();
        SetStateText(state, Theme.TextSub);
        ShowLocal();
    }

    /// <summary>出图用：单独造一个付款码弹窗，同样不联网。</summary>
    internal QrDialog DevQrDialog() => new(_s.Dorm.Label);

    /// <summary>主界面换了宿舍：手上那一单立刻作废（充的已经是另一个房间了），记录整条换掉。</summary>
    public void Bind(DormSession session)
    {
        if (ReferenceEquals(session, _s)) return;

        CancelOrder(null);
        DropDialog();
        _paid = false;
        _s = session;

        _who.Text = session.Dorm.Label;
        SetStateText("", Theme.TextSub);
        UpdatePayButton();
        ShowLocal();
        _ = ReloadHistoryAsync();
        Invalidate(true);
    }

    // ---------- 搭界面 ----------

    private void Build()
    {
        _who.Font = Theme.FontTitle;
        _who.ForeColor = Theme.Text;
        _who.Text = _s.Dorm.Label;
        Controls.Add(_who);

        foreach (int y in Presets)
        {
            var b = new UiButton($"{y} 元");
            b.Click += (_, _) => { _preset = y; _custom.Text = ""; SetYuan(y); };
            _presetBtns.Add(b);
            Controls.Add(b);
        }

        // 输入框里有东西就以它为准，不用先点预设
        _custom.TextEdited += ApplyCustom;
        _custom.Submitted += () => { if (_amountOk && !_busy) _ = StartOrderAsync(); };
        Controls.Add(_custom);

        _btnPay.Click += (_, _) => _ = StartOrderAsync();
        Controls.Add(_btnPay);

        _state.ForeColor = Theme.TextSub;
        Controls.Add(_state);

        _histTitle.Font = Theme.FontBold;
        _histTitle.ForeColor = Theme.Accent;
        _histTitle.Text = "充值记录";
        Controls.Add(_histTitle);

        Controls.Add(_list);

        _histSum.Font = Theme.FontSmall;
        _histSum.ForeColor = Theme.TextDim;
        Controls.Add(_histSum);

        SetYuan(_yuan);
    }

    /// <summary>窗口底图：柔光背景 + 上面那张玻璃卡，缩放时重合成一张。</summary>
    public Bitmap BackdropImage => _backdrop ??= BuildBackdrop();

    private Bitmap? _backdrop;

    private Bitmap BuildBackdrop()
    {
        var bmp = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        using Graphics g = Graphics.FromImage(bmp);
        g.DrawImageUnscaled(Theme.Backdrop(ClientSize), 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.Glass(g, new RectangleF(10, 8, Math.Max(1, ClientSize.Width - 20), CardBottom - 8), 18f, 0.05f);
        return bmp;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
        => e.Graphics.DrawImageUnscaled(BackdropImage, 0, 0);

    private void Layout1()
    {
        int w = ClientSize.Width, h = ClientSize.Height;

        // 底图上那张玻璃卡是按窗口宽度画死的，宽高真变了才重画——Layout1 还会因为换房间之类的走一遍
        if (_backdrop is not null && _backdrop.Size != ClientSize)
        {
            _backdrop.Dispose();
            _backdrop = null;
        }

        _who.SetBounds(Pad, 18, 320, 28);

        // 预设一行排开，最后跟一个自定义输入框
        const int bw = 80, bh = 34, gap = 8;
        for (int i = 0; i < _presetBtns.Count; i++)
            _presetBtns[i].SetBounds(Pad + (bw + gap) * i, 60, bw, bh);
        _custom.SetBounds(Pad + (bw + gap) * _presetBtns.Count, 60, 92, bh);

        _btnPay.SetBounds(Pad, 104, 168, 36);
        _state.SetBounds(_btnPay.Right + 14, 104, Math.Max(80, w - _btnPay.Right - 14 - Pad), 36);

        _histTitle.SetBounds(Pad, CardBottom + 16, 80, 22);
        _histSum.SetBounds(Pad + 88, CardBottom + 17, Math.Max(80, w - Pad * 2 - 88), 20);
        _list.SetBounds(Pad, CardBottom + 44, w - Pad * 2, Math.Max(80, h - CardBottom - 44 - Pad));

        Invalidate(true);
    }

    // ---------- 金额 ----------

    /// <summary>输入框内容变了。空了就回到上次点的预设；填了但不在范围内就拦住下单。</summary>
    private void ApplyCustom(string s)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            _amountOk = true;
            SetYuan(_preset);
            ClearRangeWarning();
            return;
        }

        if (!int.TryParse(s, out int v) || v < RechargeApi.MinYuan || v > RechargeApi.MaxYuan)
        {
            _amountOk = false;
            foreach (UiButton b in _presetBtns) b.Kind = BtnKind.Ghost;
            _btnPay.Enabled = false;
            _btnPay.Text = "生成付款码";
            SetStateText(RangeWarning, Theme.Warn);
            return;
        }

        _amountOk = true;
        SetYuan(v);
        ClearRangeWarning();
    }

    private static string RangeWarning => $"金额要在 {RechargeApi.MinYuan}~{RechargeApi.MaxYuan} 元之间";

    /// <summary>金额改回正常就把那句话撤掉。状态那儿要是正说着订单的事，别盖。</summary>
    private void ClearRangeWarning()
    {
        if (_state.Text == RangeWarning) SetStateText("", Theme.TextSub);
    }

    private void SetYuan(int yuan)
    {
        _yuan = Math.Clamp(yuan, RechargeApi.MinYuan, RechargeApi.MaxYuan);
        // 只有一个按钮该是亮的
        foreach (UiButton b in _presetBtns)
            b.Kind = b.Text == $"{_yuan} 元" ? BtnKind.Primary : BtnKind.Ghost;
        _dlg?.SetAmount(_yuan);
        UpdatePayButton();
    }

    /// <summary>按钮上那行字就是金额本身，不用另摆一句"充值 N 元"。</summary>
    private void UpdatePayButton()
    {
        _btnPay.Text = $"生成 {_yuan} 元付款码";
        _btnPay.Enabled = _amountOk && !_busy;
    }

    private void SetStateText(string text, Color color) => _state.Say(text, color);

    /// <summary>一单走完了才说的那句话（成功 / 失败 / 取消）：说完自己淡走，不赖在窗口上。</summary>
    private void FlashState(string text, Color color, int holdMs = 6000) => _state.Flash(text, color, holdMs);

    // ---------- 付款码弹窗 ----------

    /// <summary>拿到那个弹窗（没有就新建），顺手摆到前面。</summary>
    private QrDialog EnsureDialog()
    {
        if (_dlg is null || _dlg.IsDisposed)
        {
            var d = new QrDialog(_s.Dorm.Label);
            d.Regenerate += () => { if (!_busy) _ = StartOrderAsync(); };
            d.FormClosed += (s, _) =>
            {
                if (!ReferenceEquals(s, _dlg)) return;   // 已经被程序换掉了，别管
                _dlg = null;
                // 关掉弹窗就是不付了：作废这一单，下次进来重新申请一张新码
                if (!_paid && (_order is not null || _busy)) CancelOrder("已取消这一单");
            };
            _dlg = d;
        }

        _dlg.SetAmount(_yuan);
        if (!_dlg.Visible) _dlg.Show(this);
        _dlg.BringToFront();
        return _dlg;
    }

    /// <summary>不走取消逻辑地把弹窗丢掉（换房间、关窗口时用）。</summary>
    private void DropDialog()
    {
        QrDialog? d = _dlg;
        _dlg = null;
        if (d is { IsDisposed: false }) d.Close();
    }

    // ---------- 下单 / 轮询 ----------

    private async Task StartOrderAsync()
    {
        if (_busy || !_amountOk) return;

        CancelOrder(null);        // 手上那一单先作废，接下来拿到的一定是新码
        int gen = ++_orderGen;
        _paid = false;
        _busy = true;
        UpdatePayButton();
        SetStateText("正在下单…", Theme.TextSub);

        QrDialog dlg = EnsureDialog();
        dlg.Waiting("正在向学校那边下单…");
        dlg.SetState("正在下单…", Theme.TextSub);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _work = cts;
        try
        {
            RechargeOrder order = await _api.CreateWeixinOrderAsync(
                _s.Dorm.Building, _s.Dorm.Room, _yuan, cts.Token);
            if (gen != _orderGen || IsDisposed) return;

            _order = order;
            _lastPoll = DateTime.MinValue;
            _dlg?.ShowQr(order.QrPayload);
            _busy = false;
            OnTick();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen != _orderGen || IsDisposed) return;
            _order = null;
            string msg = ex is ElectricityApiException ? ex.Message : $"下单失败：{ex.Message}";
            FlashState(msg, Theme.Bad);
            _dlg?.Finish("下单失败", msg, Theme.Bad, allowAgain: true);
        }
        finally
        {
            // 已经有新的一单了就什么都别动，免得把新那单的"正在下单"给擦掉
            if (gen == _orderGen)
            {
                _busy = false;
                _work = null;
                UpdatePayButton();
            }
            cts.Dispose();
        }
    }

    /// <summary>把手上这一单作废。<paramref name="note"/> 给了就在主窗口留一句话。</summary>
    private void CancelOrder(string? note)
    {
        _orderGen++;              // 在飞的那次回来后自己闭嘴
        _work?.Cancel();          // 收尾时自己 Dispose，这儿只按下取消
        _work = null;
        _order = null;
        _busy = false;
        if (note is not null) FlashState(note, Theme.TextSub, 4000);
    }

    /// <summary>一秒一跳：更新倒计时、到期就换码、顺便每 3 秒查一次订单。</summary>
    private void OnTick()
    {
        if (_order is null) return;

        double left = (_order.ExpiresAt - DateTime.Now).TotalSeconds;
        if (left <= 0)
        {
            // 网页也是这么干的：过期就重新下一单换张新码
            SetStateText("付款码过期了，正在换一张…", Theme.TextSub);
            _dlg?.SetState("付款码过期了，正在换一张…", Theme.TextSub);
            _ = StartOrderAsync();
            return;
        }

        if (!_paid)
        {
            string text = $"等待付款 · 这张码还有 {(int)left / 60:0}:{(int)left % 60:00}";
            Color c = left <= 30 ? Theme.Warn : Theme.TextSub;
            SetStateText(text, c);
            _dlg?.SetState(text, c);
        }

        if ((DateTime.Now - _lastPoll).TotalSeconds >= 3)
        {
            _lastPoll = DateTime.Now;
            _ = PollOrderAsync(_order);
        }
    }

    private async Task PollOrderAsync(RechargeOrder order)
    {
        try
        {
            PayResult r = await _api.CheckOrderAsync(order.OrderCode, _life.Token);
            if (!ReferenceEquals(order, _order) || IsDisposed) return;

            switch (r)
            {
                case PayResult.Paid:
                    _paid = true;
                    _order = null;
                    FlashState($"充值成功 · {_yuan} 元", Theme.Good, 8000);
                    _dlg?.Finish($"{_yuan} 元已到账", $"充值成功 · {_yuan} 元", Theme.Good, allowAgain: false);
                    PaidSuccessfully?.Invoke();
                    await ReloadHistoryAsync();
                    break;

                case PayResult.Failed:
                    _order = null;
                    FlashState("这一单支付失败，可以再生成一张", Theme.Bad);
                    _dlg?.Finish("这一单失败了", "支付失败，可以再生成一张", Theme.Bad, allowAgain: true);
                    break;
            }
        }
        catch (Exception)
        {
            // 查单失败不打断：下一次 tick 再试，二维码还在那儿
        }
    }

    // ---------- 历史 ----------

    private void ShowLocal()
    {
        _list.SetItems(Store.Snapshot());
        _histSum.Say(Store.Count == 0
            ? "还没有充值记录"
            : $"共 {Store.Count} 笔 · 累计 {Store.TotalYuan():0.##} 元 · 本月 {Store.MonthTotalYuan():0.##} 元",
            Theme.TextDim);
    }

    private async Task ReloadHistoryAsync()
    {
        if (_offline) return;
        _lastHist = DateTime.Now;
        try
        {
            List<RechargeRecord> fromServer =
                await _api.QueryHistoryAsync(Store.Building, Store.Room, ct: _life.Token);
            if (IsDisposed) return;

            Store.Merge(fromServer);
            ShowLocal();      // 新增了几笔不用另说一句，列表和"共 N 笔"自己就变了
        }
        catch (OperationCanceledException)
        {
            // 窗口关了，不用管
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            ShowLocal();
            // 说一声就好，几秒后自己让回"共 N 笔 · 累计…"，本地那份记录照样能翻
            _histSum.Flash($"记录没拉到：{ex.Message}", Theme.Bad, 5000, _histSum.Text, Theme.TextDim);
        }
    }

    /// <summary>
    /// 外面（后台同步）往本地仓库里合进了新记录，重画一下列表。
    /// <b>不能碰手上那一单</b>：后台同步随时会发生，正扫着的码不该因此作废。
    /// </summary>
    public void ReloadLocal() => ShowLocal();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RememberPlace();
        _life.Cancel();
        CancelOrder(null);
        DropDialog();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _life.Cancel();
            _work?.Cancel();
            DropDialog();
            _backdrop?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
