namespace BillSystem.UI;

/// <summary>
/// 付款码弹窗：一张码 + 底下三行字 + 两个按钮。
///
/// 它自己不下单也不查单——那些还是充值窗口管着，这儿只负责显示。关掉就等于取消这一单，
/// 再点"生成付款码"会重新向学校那边申请一张新的。
/// </summary>
internal sealed class QrDialog : Form
{
    private const int Side = 280;   // 码画多大
    private const int Pad = 24;

    private readonly UiLabel _amount = new();
    private readonly UiLabel _who = new();
    private readonly QrView _qr = new() { EmptyText = "正在向学校那边下单…" };
    private readonly UiLabel _state = new();
    private readonly UiButton _btnAgain = new("换一张", BtnKind.Ghost);
    private readonly UiButton _btnClose = new("取消这一单", BtnKind.Quiet);

    /// <summary>点"换一张"：作废手上这一单，重新申请一张码。</summary>
    public event Action? Regenerate;

    public QrDialog(string who)
    {
        Text = "扫码付款";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.FontBase;
        DoubleBuffered = true;
        ClientSize = new Size(Side + Pad * 2, 448);
        Theme.ApplyDarkChrome(this);

        Build(who);
        Layout1();
        Fade.In(this);
    }

    private void Build(string who)
    {
        foreach (UiLabel l in new[] { _amount, _who, _state })
        {
            l.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(l);
        }

        _amount.Font = Theme.FontTitle;
        _amount.ForeColor = Theme.Remain;

        _who.Font = Theme.FontSmall;
        _who.ForeColor = Theme.TextSub;
        _who.Text = who;

        _state.ForeColor = Theme.TextSub;

        Controls.Add(_qr);

        _btnAgain.Click += (_, _) => Regenerate?.Invoke();
        Controls.Add(_btnAgain);

        _btnClose.Click += (_, _) => Close();
        Controls.Add(_btnClose);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
        => e.Graphics.DrawImageUnscaled(Theme.Backdrop(ClientSize), 0, 0);

    private void Layout1()
    {
        int w = ClientSize.Width;
        _amount.SetBounds(0, 14, w, 32);
        _who.SetBounds(0, 48, w, 18);
        _qr.SetBounds(Pad, 74, Side, Side);
        _state.SetBounds(0, _qr.Bottom + 12, w, 22);
        _btnAgain.SetBounds(Pad, _qr.Bottom + 46, 132, 34);
        _btnClose.SetBounds(w - Pad - 132, _qr.Bottom + 46, 132, 34);
    }

    /// <summary>标题上那个金额。</summary>
    public void SetAmount(int yuan) => _amount.Text = $"{yuan} 元";

    /// <summary>码还没拿到，先在方框里摆一句话。</summary>
    public void Waiting(string text)
    {
        _qr.Payload = null;
        _qr.EmptyText = text;
        _btnAgain.Enabled = false;
        _btnClose.Text = "取消这一单";
        _btnClose.Kind = BtnKind.Quiet;
    }

    /// <summary>把码摆上去。</summary>
    public void ShowQr(string payload)
    {
        _qr.Payload = payload;
        _btnAgain.Enabled = true;
        _btnClose.Text = "取消这一单";
        _btnClose.Kind = BtnKind.Quiet;
    }

    public void SetState(string text, Color color)
    {
        _state.ForeColor = color;
        _state.Text = text;
    }

    /// <summary>这一单有结果了（到账或失败）：码收起来，按钮换成"关闭"。</summary>
    public void Finish(string qrText, string state, Color color, bool allowAgain)
    {
        _qr.Payload = null;
        _qr.EmptyText = qrText;
        SetState(state, color);
        _btnAgain.Enabled = allowAgain;
        _btnClose.Text = "关闭";
        _btnClose.Kind = BtnKind.Primary;
    }

    /// <summary>出图用：摆一张现成的码，不联网。</summary>
    internal void DevPose(int yuan, string payload, string state)
    {
        SetAmount(yuan);
        ShowQr(payload);
        SetState(state, Theme.TextSub);
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
