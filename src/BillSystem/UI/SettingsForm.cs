using System.Drawing.Drawing2D;
using BillSystem.Models;
using BillSystem.Services;

namespace BillSystem.UI;

/// <summary>
/// 设置窗口。所有改动先记在一份配置副本上，点"保存"才写回真正的配置，
/// 点"取消"或直接关窗口就当什么都没发生。
///
/// 排版是 iOS 那种"分组卡"：每组一张玻璃卡，组名写在卡外面。卡是画在窗口底图上的
/// （见 <see cref="Theme.IBackdropHost"/>），里面的输入框贴的就是这张合成图，接缝才对得上。
/// </summary>
internal sealed class SettingsForm : Form, Theme.IBackdropHost
{
    private const int W = 520;
    private const int CardX = 10;               // 分组卡左右各留这么点边
    private const int CardR = W - CardX;
    private const int LabelX = 22;
    private const int InputX = 124;
    private const int RightX = W - 22;          // 内容右边界，右对齐的按钮贴着它
    private const int TestBtnW = 104;

    private readonly AppConfig _cfg;
    private readonly AppConfig _draft;
    private readonly ElectricityApi _api;
    private readonly Summary? _summary;

    private readonly UiSpin _threshold = new() { Minimum = 0, Maximum = 1000, Decimals = 1, Step = 0.5 };
    private readonly UiSpin _daysLeft = new() { Minimum = 0, Maximum = 30, Decimals = 1, Step = 0.5 };
    private readonly UiSpin _offsetX = new() { Minimum = 0, Maximum = 2000, Step = 2 };

    private readonly UiText _mailCode = new() { PasswordChar = '●', MaxLength = 64, Placeholder = "16 位授权码" };

    private readonly UiToggle _tglLow = new("低于阈值时发 Windows 通知");
    private readonly UiToggle _tglWidget = new("在任务栏显示剩余电量");
    private readonly UiToggle _tglExtra = new("多显示一列今日 / 日均");
    private readonly UiToggle _tglAutoStart = new("开机自动启动");

    private readonly UiButton _btnTest = new("测试查询");
    private readonly UiButton _btnNotifyTest = new("试一条通知") { Radius = 6f };
    private readonly UiButton _btnMailTest = new("试一封邮件") { Radius = 6f };
    private readonly UiButton _btnSave = new("保存", BtnKind.Primary);
    private readonly UiButton _btnCancel = new("取消");

    /// <summary>点"试一条通知"时发出，由托盘那边真的弹一条，用来确认系统通知没被关掉。</summary>
    public event Action? TestNotifyRequested;

    private readonly UiLabel _testResult = new();
    private readonly UiLabel _mailResult = new();

    private int _y = 16;

    /// <summary>每组卡片的位置（画进底图用），以及正在排的那一组从哪儿开始。</summary>
    private readonly List<RectangleF> _cards = new();
    private int _cardTop = -1;
    private int _barY;
    private Bitmap? _backdrop;

    public SettingsForm(AppConfig cfg, ElectricityApi api, Summary? summary = null)
    {
        _cfg = cfg;
        _draft = cfg.Clone();
        _api = api;
        _summary = summary;

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
        ClientSize = new Size(W, 600);
        Theme.ApplyDarkChrome(this);

        Build();
        LoadValues();
        SyncEnabled();
        TabOrder();
        Fade.In(this);
    }

    /// <summary>
    /// Tab 顺序按眼睛看到的先后来，不靠"谁先 Controls.Add"——那个顺序被
    /// 底部按钮栏最后加进来这件事打乱了，不排的话 Tab 会先跳到"保存"。
    /// </summary>
    private void TabOrder()
    {
        Control[] order =
        {
            _tglLow, _threshold, _btnNotifyTest, _daysLeft,
            _mailCode, _btnMailTest,
            _tglWidget, _tglExtra, _offsetX,
            _tglAutoStart,
            _btnTest, _btnCancel, _btnSave,
        };
        for (int i = 0; i < order.Length; i++)
        {
            order[i].TabStop = true;
            order[i].TabIndex = i;
        }
    }

    /// <summary>
    /// 排版就三列：标签靠左（<see cref="LabelX"/>），输入框都从 <see cref="InputX"/> 起，
    /// "试一下"这类验证按钮一律右对齐贴 <see cref="RightX"/>，跟它验证的那一行同高。
    /// 结果文字挂在对应行的下一行、跟输入框左对齐。
    /// </summary>
    private void Build()
    {
        Section("低电量提醒");
        Add(_tglLow);
        _btnNotifyTest.Click += (_, _) => TestNotifyRequested?.Invoke();
        Row("提醒阈值", _threshold, "度", 120, _btnNotifyTest);
        Row("预计可用", _daysLeft, "天以内", 120);

        Section("邮件提醒（QQ 邮箱）");
        // 收发地址写死在程序里，这儿只摆出来看一眼；填了授权码就发，没填就只发系统通知
        Info("发件", AppConfig.FixedMailFrom);
        Info("收件", AppConfig.MailToLine);
        _btnMailTest.Click += async (_, _) => await TestMailAsync();
        Row("授权码", _mailCode, null, 250, _btnMailTest);
        Result(_mailResult);

        Section("任务栏组件");
        Add(_tglWidget);
        Add(_tglExtra);
        Row("左侧偏移", _offsetX, "像素");

        Section("其它");
        Add(_tglAutoStart);
        _btnTest.Click += async (_, _) => await TestAsync();
        Row("查询接口", _btnTest, null, 96);
        Result(_testResult);

        _y += 6;
        CloseCard();
        BuildBottomBar();
        WireEvents();
    }

    /// <summary>只读的一行信息：标签 + 摆出来看的值（改不了的东西不做成输入框）。</summary>
    private void Info(string label, string value)
    {
        var lb = new UiLabel { Text = label, ForeColor = Theme.TextSub };
        lb.SetBounds(LabelX, _y, InputX - LabelX - 8, 24);
        Controls.Add(lb);

        int w = RightX - InputX;
        int h = Math.Max(24, TextRenderer.MeasureText(value, Theme.FontSmall, new Size(w, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 2);
        var val = new UiLabel
        {
            Text = value,
            Font = Theme.FontSmall,
            ForeColor = Theme.Text,
            Wrap = true,
        };
        val.SetBounds(InputX, _y, w, h);
        Controls.Add(val);

        _y += h + 4;
    }

    /// <summary>"试一下"之后那句结果，挂在上一行下面、跟输入框左对齐。</summary>
    private void Result(UiLabel lb)
    {
        lb.Font = Theme.FontSmall;
        lb.ForeColor = Theme.TextSub;
        lb.SetBounds(InputX, _y - 7, RightX - InputX, 20);
        Controls.Add(lb);
        _y += 16;
    }

    /// <summary>内容排完了才知道要多高，底部这条按钮栏最后放。</summary>
    private void BuildBottomBar()
    {
        int h = _y + 54;
        ClientSize = new Size(W, h);
        _barY = h - 54;

        _btnSave.SetBounds(W - 20 - 92, h - 43, 92, 34);
        _btnSave.Click += (_, _) =>
        {
            if (!SaveValues()) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(_btnSave);

        _btnCancel.SetBounds(_btnSave.Left - 10 - 84, h - 43, 84, 34);
        _btnCancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        Controls.Add(_btnCancel);
    }

    /// <summary>窗口底图：柔光背景 + 几张分组玻璃卡 + 按钮栏那条分隔线，只合成一次。</summary>
    public Bitmap BackdropImage => _backdrop ??= BuildBackdrop();

    private Bitmap BuildBackdrop()
    {
        var bmp = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        using Graphics g = Graphics.FromImage(bmp);
        g.DrawImageUnscaled(Theme.Backdrop(ClientSize), 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        foreach (RectangleF card in _cards) Theme.Glass(g, card, 16f, 0.05f);

        using var line = new Pen(Color.FromArgb(26, 255, 255, 255));
        g.DrawLine(line, 0, _barY + 0.5f, W, _barY + 0.5f);
        return bmp;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
        => e.Graphics.DrawImageUnscaled(BackdropImage, 0, 0);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _backdrop?.Dispose();
        base.Dispose(disposing);
    }

    private void WireEvents()
    {
        _tglLow.CheckedChanged += _ => SyncEnabled();
        _tglWidget.CheckedChanged += _ => SyncEnabled();
    }

    /// <summary>关掉的开关下面那些输入框跟着灰掉，免得填了半天不起作用。</summary>
    private void SyncEnabled()
    {
        bool low = _tglLow.Checked;
        _threshold.Enabled = low;
        _daysLeft.Enabled = low;
        _btnNotifyTest.Enabled = low;

        // 邮件挂在低电量提醒下面：总开关关了，那封邮件也就无从发起
        _mailCode.Enabled = low;
        _btnMailTest.Enabled = low;

        bool widget = _tglWidget.Checked;
        _offsetX.Enabled = widget;
        _tglExtra.Enabled = widget;
    }

    private void Section(string title)
    {
        CloseCard();

        var lb = new UiLabel
        {
            Text = title,
            Font = Theme.FontBold,
            ForeColor = Theme.Accent,
        };
        lb.SetBounds(CardX + 12, _y, CardR - CardX - 24, 18);
        Controls.Add(lb);

        _y += 24;
        _cardTop = _y;
        _y += 10;                 // 卡内上边距
    }

    /// <summary>把上一组的卡框住：内容排到哪儿，卡就画到哪儿。</summary>
    private void CloseCard()
    {
        if (_cardTop < 0)
        {
            _y += _y == 16 ? 0 : 14;
            return;
        }

        _cards.Add(new RectangleF(CardX, _cardTop, CardR - CardX, _y + 10 - _cardTop));
        _cardTop = -1;
        _y += 10 + 14;            // 卡内下边距 + 两组之间的空隙
    }

    /// <summary>
    /// 一行：标签 + 输入框（+ 单位）。<paramref name="trailing"/> 给了就右对齐摆在同一行末尾——
    /// 验证按钮跟它验证的那一行同高，才看得出是一件事。
    /// </summary>
    private void Row(string label, Control input, string? hint, int inputWidth = 120,
        Control? trailing = null)
    {
        var lb = new UiLabel { Text = label, ForeColor = Theme.TextSub };
        lb.SetBounds(LabelX, _y, InputX - LabelX - 8, 26);
        Controls.Add(lb);

        // 输入框比这一行的文字高 4 像素，往上挪 2 就跟标签一条中线了
        input.SetBounds(InputX, _y - 2, inputWidth, 30);
        Controls.Add(input);

        if (hint is not null)
        {
            // 单位文字给宽点，"天以内""像素"这种三个字的也要放得下
            var hintLabel = new UiLabel { Text = hint, ForeColor = Theme.TextDim };
            hintLabel.SetBounds(InputX + inputWidth + 10, _y, 72, 26);
            Controls.Add(hintLabel);
        }

        if (trailing is not null)
        {
            trailing.SetBounds(RightX - TestBtnW, _y - 3, TestBtnW, 32);
            Controls.Add(trailing);
        }

        _y += 36;
    }

    private void Add(UiToggle t)
    {
        t.SetBounds(LabelX, _y, CardR - LabelX - 12, 28);
        Controls.Add(t);
        _y += 32;
    }

    private void LoadValues()
    {
        _threshold.Value = _draft.LowThreshold;
        _daysLeft.Value = _draft.LowDaysThreshold;
        _offsetX.Value = _draft.WidgetOffsetX;
        _mailCode.Text = _draft.MailAuthCode;

        _tglLow.SetSilently(_draft.LowAlertEnabled);
        _tglWidget.SetSilently(_draft.ShowWidget);
        _tglExtra.SetSilently(_draft.WidgetShowExtra);
        _tglAutoStart.SetSilently(_draft.StartWithWindows || Startup.IsEnabled());
    }

    /// <summary>把界面上的值写进副本，再一次性覆盖到真配置。返回 false 表示别关窗口。</summary>
    private bool SaveValues()
    {
        _draft.LowAlertEnabled = _tglLow.Checked;
        _draft.LowThreshold = _threshold.Value;
        _draft.LowDaysThreshold = _daysLeft.Value;
        _draft.MailAuthCode = (_mailCode.Text ?? "").Trim();
        _draft.ShowWidget = _tglWidget.Checked;
        _draft.WidgetShowExtra = _tglExtra.Checked;
        _draft.WidgetOffsetX = (int)_offsetX.Value;

        bool wantAutoStart = _tglAutoStart.Checked;
        if (wantAutoStart != Startup.IsEnabled())
        {
            if (Startup.TrySet(wantAutoStart, out string? err))
            {
                _draft.StartWithWindows = wantAutoStart;
            }
            else
            {
                MessageBox.Show(this, $"设置开机自启失败：{err}\n其它设置已经保存。", "宿舍电费助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else
        {
            _draft.StartWithWindows = wantAutoStart;
        }

        _cfg.CopyFrom(_draft);
        _cfg.Save();
        return true;
    }

    private async Task TestAsync()
    {
        _btnTest.Enabled = false;
        _testResult.ForeColor = Theme.TextSub;
        _testResult.Text = "查询中…";
        try
        {
            Reading r = await _api.QueryAsync(AppConfig.FixedBuilding, AppConfig.FixedRoom);
            _testResult.ForeColor = Theme.Good;
            _testResult.Text = $"剩余 {r.Remaining:0.00} 度 · 抄表 {r.MeterTime:MM-dd HH:mm}";
        }
        catch (Exception ex)
        {
            _testResult.ForeColor = Theme.Bad;
            _testResult.Text = ex.Message.Length > 46 ? ex.Message[..46] + "…" : ex.Message;
        }
        finally
        {
            _btnTest.Enabled = true;
        }
    }

    /// <summary>拿现在填的授权码真发一封，不用先保存。</summary>
    private async Task TestMailAsync()
    {
        _btnMailTest.Enabled = false;
        _mailResult.ForeColor = Theme.TextSub;
        _mailResult.Text = "发送中…";

        AppConfig probe = _draft.Clone();
        probe.MailAuthCode = (_mailCode.Text ?? "").Trim();

        try
        {
            await MailAlert.SendTestAsync(probe, _summary);
            _mailResult.ForeColor = Theme.Good;
            _mailResult.Text = $"已发出，{probe.MailTo.Count} 个收件箱各收一下";
        }
        catch (Exception ex)
        {
            _mailResult.ForeColor = Theme.Bad;
            _mailResult.Text = ex.Message.Length > 40 ? ex.Message[..40] + "…" : ex.Message;
        }
        finally
        {
            _btnMailTest.Enabled = _tglLow.Checked;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return true;
        }

        // 回车相当于点保存，但焦点在数字框里时先让它自己收下这一下
        if (keyData == Keys.Enter && ActiveControl is not UiSpin && ActiveControl is not TextBox)
        {
            if (SaveValues())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }
}
