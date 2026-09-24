using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace DesktopCalendarWidget;

public sealed class HolidayService
{
    // HttpClient 需要长期复用，避免频繁创建导致套接字耗尽。
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<List<HolidayEntry>> DownloadYearAsync(string urlTemplate, int year, CancellationToken token = default)
    {
        var url = BuildUrl(urlTemplate, year) ?? throw new FormatException(Loc.UrlFormatFailed);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // 与其他联网模块保持一致：免费接口常按 UA 区分来源，不带可能被拒。
        HttpSupport.ApplyUserAgent(request);
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return Parse(document.RootElement);
    }

    private static string? BuildUrl(string? template, int year)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, year);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>解析接口响应。internal 以便回归测试直接喂样例数据，不必联网。</summary>
    internal static List<HolidayEntry> Parse(JsonElement root)
    {
        var result = new List<HolidayEntry>();
        JsonElement source = root;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("holiday", out var holiday)) source = holiday;

        if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in source.EnumerateArray()) AddItem(result, item);
        }
        else if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object) AddItem(result, property.Value, property.Name);
            }
        }

        // 同一天多条时按优先级取舍（与 RestSchedule.CreateHolidayMap 一致），
        // 否则"放假"可能压掉"调休上班"，排班与着色就跟着错了。
        return result
            .Where(x => x.DateValue != DateTime.MinValue)
            .GroupBy(x => x.Date)
            .Select(x => x.OrderByDescending(entry => entry.SchedulePriority).First())
            .ToList();
    }

    private static void AddItem(List<HolidayEntry> result, JsonElement item, string? fallbackName = null)
    {
        if (item.ValueKind != JsonValueKind.Object) return;
        var dateText = StringValue(item, "date") ?? StringValue(item, "day");
        if (!TryParseDate(dateText, out var date)) return;

        var workday = BoolValue(item, "isWorkday") || BoolValue(item, "workday") || BoolValue(item, "is_workday");
        if (item.TryGetProperty("holiday", out var holidayFlag) && holidayFlag.ValueKind == JsonValueKind.False) workday = true;

        var name = StringValue(item, "name") ?? StringValue(item, "title") ?? fallbackName;
        var target = StringValue(item, "target") ?? StringValue(item, "targetName");
        if (workday && !string.IsNullOrWhiteSpace(target)) name = $"{target}调休";
        if (string.IsNullOrWhiteSpace(name)) name = workday ? "调休上班" : "节假日";

        result.Add(new HolidayEntry
        {
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Name = name,
            IsWorkday = workday,
            IsHoliday = !workday
        });
    }

    private static bool TryParseDate(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)) return true;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static string? StringValue(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool BoolValue(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
