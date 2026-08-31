using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BillSystem.Models;

namespace BillSystem.Services;

public sealed class ElectricityApiException : Exception
{
    public ElectricityApiException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 五邑大学宿舍电费查询接口封装。
///
/// POST http://202.192.240.231/scp-api/electricity-recharge/getCurrentRemaining_v2
///   Content-Type: application/x-www-form-urlencoded
///   userTypeID=1&amp;building=43&amp;room=422
///
/// 返回 data.resamp = 剩余电量(度)、data.usedamp = 累计用电量(度)、data.updatedt = 抄表时间。
/// 接口只有"当前值"，没有历史查询，所以年/月/日/小时曲线靠本程序自己攒。
/// </summary>
public sealed class ElectricityApi : IDisposable
{
    public const string Endpoint =
        "http://202.192.240.231/scp-api/electricity-recharge/getCurrentRemaining_v2";

    private const string Referer = "http://202.192.240.231/recharge.html";

    private readonly HttpClient _http;

    public ElectricityApi()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = false, // 校园网直连，别被系统代理绕进去
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri(Referer);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<Reading> QueryAsync(int building, int room, CancellationToken ct = default)
    {
        string body = $"userTypeID=1&building={building}&room={room}";
        using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
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

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json, building, room);
        }
    }

    internal static Reading Parse(string json, int building, int room)
    {
        Envelope? env;
        try
        {
            env = JsonSerializer.Deserialize<Envelope>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
            });
        }
        catch (JsonException ex)
        {
            throw new ElectricityApiException("返回内容不是合法 JSON", ex);
        }

        if (env is null)
            throw new ElectricityApiException("返回内容为空");

        if (env.Data is null || !env.Success)
        {
            string msg = string.IsNullOrWhiteSpace(env.Message) ? "查询失败" : env.Message!;
            throw new ElectricityApiException(msg);
        }

        var d = env.Data;
        if (!TryParseMeterTime(d.Updatedt, out var meterTime))
            meterTime = DateTime.Now;

        var now = DateTime.Now;
        return new Reading
        {
            SlotTime = Reading.SlotOf(now),
            MeterTime = meterTime,
            FetchedAt = now,
            Used = d.Usedamp,
            Remaining = d.Resamp,
            Building = building,
            Room = room,
        };
    }

    private static bool TryParseMeterTime(string? s, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(s)) return false;

        string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss" };
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out value)
               || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    public void Dispose() => _http.Dispose();

    // ---------- 接口 DTO ----------

    private sealed class Envelope
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public Data? Data { get; set; }
    }

    /// <summary>只声明用得上的三个字段，其余的 JSON 反序列化会自己忽略掉。</summary>
    private sealed class Data
    {
        public double Usedamp { get; set; }
        public double Resamp { get; set; }
        public string? Updatedt { get; set; }
    }
}
