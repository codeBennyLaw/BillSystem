using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BillSystem.Models;

namespace BillSystem.Services;

/// <summary>一次微信扫码下单的结果。</summary>
public sealed class RechargeOrder
{
    public string OrderCode { get; init; } = "";

    /// <summary>要编成二维码的那串文本（网页也是拿它自己画的，服务器不给图片）。</summary>
    public string QrPayload { get; init; } = "";

    /// <summary>二维码有效期，单位秒。</summary>
    public int ExpireSeconds { get; init; }

    /// <summary>下单的本机时间，用来算还剩多久。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public DateTime ExpiresAt => CreatedAt.AddSeconds(ExpireSeconds);
}

/// <summary>订单支付状态。接口给的是数字：0 待支付，1 成功，2 失败。</summary>
public enum PayResult
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
}

/// <summary>
/// 电费充值接口封装（只做微信扫码那条路）。
///
/// 下单  POST /scp-api/electricity-recharge/create-order-cb   (application/json)
///       {"mobile":"","building":43,"room":422,"payCent":2000,"userTypeId":1,"codeType":"weixin"}
///       → data.{orderCode, qrCode, qrCodeExpireTime}
///
/// 查单  POST /scp-api/electricity-recharge/check-order-cb     (x-www-form-urlencoded)
///       orderCode=...
///       → data.record.payResult (0/1/2)
///
/// 历史  POST /scp-api/electricity-recharge/orderQueryWithPage (x-www-form-urlencoded)
///       building/room/userTypeID/payResult/period/page/pageSize
///       → data.{total, page, pageSize, data:[...]}
///       period 取 3M / 6M / 1Y / 1Y+，其中 1Y+ 是"全部"，不是"一年以上"。
///
/// 三个接口都不要登录态，只认 Referer。
/// </summary>
public sealed class RechargeApi : IDisposable
{
    private const string Root = "http://202.192.240.231/scp-api/electricity-recharge/";
    public const string CreateEndpoint = Root + "create-order-cb";
    public const string CheckEndpoint = Root + "check-order-cb";
    public const string HistoryEndpoint = Root + "orderQueryWithPage";

    private const string Referer = "http://202.192.240.231/recharge.html";

    /// <summary>网页里写死的上下限，这边照抄，免得白跑一趟。</summary>
    public const int MinYuan = 1;
    public const int MaxYuan = 1000;

    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public RechargeApi()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = false,
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri(Referer);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ---------- 下单 ----------

    /// <summary>
    /// 下一笔微信充值单，拿回二维码文本。<paramref name="yuan"/> 是元，接口要的是分。
    /// <c>mobile</c> 留在 body 里但给空串——学校那边只拿它发短信通知，整个键去掉反而有可能
    /// 碰上服务端的非空校验。
    /// </summary>
    public async Task<RechargeOrder> CreateWeixinOrderAsync(
        int building, int room, int yuan, CancellationToken ct = default)
    {
        if (yuan is < MinYuan or > MaxYuan)
            throw new ElectricityApiException($"充值金额要在 {MinYuan}~{MaxYuan} 元之间");

        string body = JsonSerializer.Serialize(new CreateReq
        {
            Mobile = "",
            Building = building,
            Room = room,
            PayCent = yuan * 100,
            UserTypeId = 1,
            CodeType = "weixin",
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        string json = await SendAsync(CreateEndpoint, content, ct).ConfigureAwait(false);

        Envelope<CreateData>? env = Parse<CreateData>(json);
        if (env?.Data is null || !env.Success)
            throw new ElectricityApiException(Fail(env?.Message, "下单失败"));

        if (string.IsNullOrWhiteSpace(env.Data.QrCode))
            throw new ElectricityApiException("服务器没有返回二维码内容");

        return new RechargeOrder
        {
            OrderCode = env.Data.OrderCode ?? "",
            QrPayload = env.Data.QrCode!,
            // 偶尔会回 0，那就按网页的默认 300 秒算
            ExpireSeconds = env.Data.QrCodeExpireTime > 0 ? env.Data.QrCodeExpireTime : 300,
        };
    }

    // ---------- 查单 ----------

    public async Task<PayResult> CheckOrderAsync(string orderCode, CancellationToken ct = default)
    {
        using var content = new StringContent(
            $"orderCode={Uri.EscapeDataString(orderCode)}", Encoding.UTF8, "application/x-www-form-urlencoded");
        string json = await SendAsync(CheckEndpoint, content, ct).ConfigureAwait(false);

        Envelope<CheckData>? env = Parse<CheckData>(json);
        if (env?.Data?.Record is null || !env.Success)
            throw new ElectricityApiException(Fail(env?.Message, "查询订单失败"));

        return env.Data.Record.PayResult switch
        {
            1 => PayResult.Paid,
            2 => PayResult.Failed,
            _ => PayResult.Pending,
        };
    }

    // ---------- 历史 ----------

    /// <summary>
    /// 拉充值记录。<paramref name="period"/> 用 <c>1Y+</c> 就是全部；服务端按时间倒序给。
    /// 一次拉一页，翻到 <paramref name="maxRecords"/> 条或者拉完为止。
    /// </summary>
    public async Task<List<RechargeRecord>> QueryHistoryAsync(
        int building, int room, int maxRecords = 500, string period = "1Y+", CancellationToken ct = default)
    {
        var result = new List<RechargeRecord>();
        const int pageSize = 50;

        for (int page = 1; result.Count < maxRecords; page++)
        {
            string body = $"building={building}&room={room}&userTypeID=1&payResult=1" +
                          $"&period={Uri.EscapeDataString(period)}&page={page}&pageSize={pageSize}";
            using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
            string json = await SendAsync(HistoryEndpoint, content, ct).ConfigureAwait(false);

            Envelope<HistoryData>? env = Parse<HistoryData>(json);
            if (env?.Data is null || !env.Success)
                throw new ElectricityApiException(Fail(env?.Message, "查询充值记录失败"));

            List<HistoryItem> items = env.Data.Data ?? new List<HistoryItem>();
            foreach (HistoryItem it in items)
            {
                if (string.IsNullOrWhiteSpace(it.OrderCode)) continue;
                result.Add(new RechargeRecord
                {
                    OrderCode = it.OrderCode!,
                    PayTime = ParseTime(it.PayTime),
                    PayCent = it.PayCent,
                    PayMethod = it.PayMethod ?? "",
                    PayResult = it.PayResult ?? "",
                    Building = ParseInt(it.Building, building),
                    Room = ParseInt(it.Room, room),
                });
            }

            // 这一页没满，说明到底了
            if (items.Count < pageSize || result.Count >= env.Data.Total) break;
        }

        return result;
    }

    // ---------- 公共部分 ----------

    private async Task<string> SendAsync(string url, HttpContent content, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ElectricityApiException("请求超时，检查是否连着校园网");
        }
        catch (HttpRequestException ex)
        {
            throw new ElectricityApiException("连不上服务器，检查是否连着校园网", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new ElectricityApiException($"服务器返回 HTTP {(int)resp.StatusCode}");
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
    }

    private static Envelope<T>? Parse<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<Envelope<T>>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new ElectricityApiException("返回内容不是合法 JSON", ex);
        }
    }

    private static string Fail(string? message, string fallback)
        => string.IsNullOrWhiteSpace(message) ? fallback : message!;

    internal static DateTime ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return default;
        string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss" };
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out DateTime v)
               || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out v)
            ? v
            : default;
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    public void Dispose() => _http.Dispose();

    // ---------- 接口 DTO ----------
    // 只声明程序真的读的字段，接口多给的那些 JSON 反序列化会自己忽略掉。

    private sealed class Envelope<T>
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public T? Data { get; set; }
    }

    private sealed class CreateReq
    {
        [JsonPropertyName("mobile")] public string Mobile { get; set; } = "";
        [JsonPropertyName("building")] public int Building { get; set; }
        [JsonPropertyName("room")] public int Room { get; set; }
        [JsonPropertyName("payCent")] public int PayCent { get; set; }
        // 注意这个是小写 d：只有下单接口这么拼，查询那两个是 userTypeID
        [JsonPropertyName("userTypeId")] public int UserTypeId { get; set; }
        [JsonPropertyName("codeType")] public string CodeType { get; set; } = "";
    }

    private sealed class CreateData
    {
        public string? OrderCode { get; set; }
        public string? QrCode { get; set; }
        public int QrCodeExpireTime { get; set; }
    }

    private sealed class CheckData
    {
        public CheckRecord? Record { get; set; }
    }

    private sealed class CheckRecord
    {
        public int PayResult { get; set; }
    }

    private sealed class HistoryData
    {
        public int Total { get; set; }
        public List<HistoryItem>? Data { get; set; }
    }

    private sealed class HistoryItem
    {
        public string? OrderCode { get; set; }
        public string? PayTime { get; set; }
        public string? PayMethod { get; set; }
        public string? PayResult { get; set; }
        public int PayCent { get; set; }
        public string? Building { get; set; }
        public string? Room { get; set; }
    }
}
