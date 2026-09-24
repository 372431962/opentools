using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace DesktopCalendarWidget.Weather;

/// <summary>天气数据源。返回 null 或空列表表示该源不可用，由 WeatherService 切下一个。</summary>
public interface IWeatherProvider
{
    string Name { get; }

    Task<List<WeatherDay>?> GetDaysAsync(WeatherQuery query, CancellationToken token);
}

/// <summary>一次天气拉取的输入。坐标为 null 时由源自己按城市名解析（或直接拒绝）。</summary>
public readonly record struct WeatherQuery(string City, double? Latitude, double? Longitude);

/// <summary>共用的天气 HTTP 设施。</summary>
internal static class WeatherHttp
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<string> GetStringAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpSupport.ApplyUserAgent(request);
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }
}

/// <summary>
/// Open-Meteo 免费预报接口（无需 API Key）。一天一次请求可拿到多天数据，
/// 是首选数据源；需要先把城市名解析成经纬度。
/// </summary>
public sealed class OpenMeteoWeatherProvider : IWeatherProvider
{
    /// <summary>回看天数。覆盖“查看上个月”时月初那些格子。</summary>
    public const int PastDays = 28;

    /// <summary>预报天数。免费版上限就是 16 天，再远的日期只能空着。</summary>
    public const int ForecastDays = 16;

    public string Name => "Open-Meteo";

    /// <summary>纯函数：构造请求 URL，便于不联网的回归测试。</summary>
    public static string BuildUrl(double latitude, double longitude) =>
        "https://api.open-meteo.com/v1/forecast" +
        $"?latitude={latitude.ToString("0.####", CultureInfo.InvariantCulture)}" +
        $"&longitude={longitude.ToString("0.####", CultureInfo.InvariantCulture)}" +
        "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
        $"&timezone=auto&past_days={PastDays}&forecast_days={ForecastDays}";

    /// <summary>纯函数：解析响应。结构不对时返回 null。</summary>
    public static List<WeatherDay>? ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Object)
                return null;
            if (!daily.TryGetProperty("time", out var times) || times.ValueKind != JsonValueKind.Array) return null;

            var codes = ArrayProperty(daily, "weather_code");
            var maxes = ArrayProperty(daily, "temperature_2m_max");
            var mins = ArrayProperty(daily, "temperature_2m_min");
            var precip = ArrayProperty(daily, "precipitation_probability_max");

            var days = new List<WeatherDay>();
            for (var i = 0; i < times.GetArrayLength(); i++)
            {
                var time = times[i];
                if (time.ValueKind != JsonValueKind.String) continue;
                var date = time.GetString();
                if (string.IsNullOrEmpty(date)) continue;
                var day = new WeatherDay
                {
                    Date = DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : date,
                    TempMax = DoubleAt(maxes, i),
                    TempMin = DoubleAt(mins, i),
                    PrecipitationProbability = IntAt(precip, i)
                };
                var code = IntAt(codes, i);
                day.Condition = code is null ? WeatherCondition.Unknown : WeatherCodes.FromWmo(code.Value);
                if (day.DateValue != DateTime.MinValue) days.Add(day);
            }
            return days.Count == 0 ? null : days;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task<List<WeatherDay>?> GetDaysAsync(WeatherQuery query, CancellationToken token)
    {
        if (query.Latitude is not double latitude || query.Longitude is not double longitude) return null;
        var json = await WeatherHttp.GetStringAsync(BuildUrl(latitude, longitude), token);
        return ParseResponse(json);
    }

    private static JsonElement? ArrayProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value : null;

    private static double DoubleAt(JsonElement? array, int index)
    {
        if (array is not JsonElement value || index >= value.GetArrayLength()) return double.NaN;
        var item = value[index];
        return item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var parsed) ? parsed : double.NaN;
    }

    private static int? IntAt(JsonElement? array, int index)
    {
        if (array is not JsonElement value || index >= value.GetArrayLength()) return null;
        var item = value[index];
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var parsed) ? parsed : null;
    }
}
