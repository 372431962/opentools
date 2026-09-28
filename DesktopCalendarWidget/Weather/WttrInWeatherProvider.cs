using System.Globalization;
using System.Text.Json;

namespace DesktopCalendarWidget.Weather;

/// <summary>
/// wttr.in 免费天气接口（无需 API Key）。直接支持城市名，但只给 3 天，
/// 因此仅作为 Open-Meteo 不可用时的兜底。
/// </summary>
public sealed class WttrInWeatherProvider : IWeatherProvider
{
    public string Name => "wttr.in";

    /// <summary>纯函数：构造请求 URL，城市名按路径段转义。</summary>
    public static string BuildUrl(string city) =>
        $"https://wttr.in/{Uri.EscapeDataString(city.Trim())}?format=j1&lang=en";

    /// <summary>
    /// 纯函数：按查询构造请求 URL。有坐标时优先用坐标（~lat,lon），
    /// 裸地名会被 wttr.in 解析到全球任意同名地，用户明确选过地点时不能丢坐标。
    /// </summary>
    public static string BuildUrl(WeatherQuery query) =>
        query is { Latitude: not null, Longitude: not null }
            ? string.Format(CultureInfo.InvariantCulture,
                "https://wttr.in/~{0},{1}?format=j1&lang=en", query.Latitude.Value, query.Longitude.Value)
            : BuildUrl(query.City);

    /// <summary>纯函数：解析响应。字段类型不符时跳过该条，整体不可识别时返回 null。</summary>
    public static List<WeatherDay>? ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("weather", out var weather) ||
                weather.ValueKind != JsonValueKind.Array) return null;

            var currentCondition = WeatherCodes.FromDescription(DescribeCurrent(document.RootElement));
            var days = new List<WeatherDay>();
            foreach (var item in weather.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("date", out var dateValue) ||
                    dateValue.ValueKind != JsonValueKind.String) continue;
                var date = dateValue.GetString();
                if (string.IsNullOrEmpty(date)) continue;
                days.Add(new WeatherDay
                {
                    Date = DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                        ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : date,
                    Condition = WeatherCodes.FromDescription(Describe(item)),
                    CurrentCondition = days.Count == 0 && currentCondition != WeatherCondition.Unknown ? currentCondition : null,
                    TempMax = DoubleValue(item, "maxtempC"),
                    TempMin = DoubleValue(item, "mintempC")
                });
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
    }

    public async Task<List<WeatherDay>?> GetDaysAsync(WeatherQuery query, CancellationToken token)
    {
        if (query.Latitude is null && string.IsNullOrWhiteSpace(query.City)) return null;
        var json = await WeatherHttp.GetStringAsync(BuildUrl(query), token);
        return ParseResponse(json);
    }

    /// <summary>优先取正午时段作为整日预报，避免用午夜的状况代表全天。</summary>
    private static string? Describe(JsonElement day)
    {
        if (!day.TryGetProperty("hourly", out var hourly) || hourly.ValueKind != JsonValueKind.Array ||
            hourly.GetArrayLength() == 0) return null;
        var selected = hourly[0];
        foreach (var item in hourly.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("time", out var time) &&
                time.ValueKind == JsonValueKind.String && time.GetString() == "1200")
            {
                selected = item;
                break;
            }
        }
        return DescriptionValue(selected);
    }

    private static string? DescribeCurrent(JsonElement root)
    {
        if (!root.TryGetProperty("current_condition", out var current) ||
            current.ValueKind != JsonValueKind.Array || current.GetArrayLength() == 0) return null;
        return current[0].ValueKind == JsonValueKind.Object ? DescriptionValue(current[0]) : null;
    }

    private static string? DescriptionValue(JsonElement item)
    {
        if (!item.TryGetProperty("weatherDesc", out var descs) || descs.ValueKind != JsonValueKind.Array ||
            descs.GetArrayLength() == 0) return null;
        var text = descs[0];
        return text.ValueKind == JsonValueKind.Object && text.TryGetProperty("value", out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double DoubleValue(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return double.NaN;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.NaN;
    }
}
