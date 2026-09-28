using System.Globalization;
using System.Text.Json.Serialization;

namespace DesktopCalendarWidget.Weather;

/// <summary>归一化后的天气现象。各家接口的原始编码不同，先映射到这组值再进 UI。</summary>
public enum WeatherCondition
{
    Unknown,
    Clear,
    PartlyCloudy,
    Overcast,
    Fog,
    Drizzle,
    Rain,
    HeavyRain,
    Snow,
    Thunderstorm
}

/// <summary>用户明确选择的天气地点。坐标决定预报位置，行政区字段用于显示完整路径。</summary>
public sealed class WeatherLocation
{
    public string Query { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Admin1 { get; set; }
    public string? Admin2 { get; set; }
    public string? Admin3 { get; set; }
    public string? Country { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    [JsonIgnore]
    public string DisplayName => string.Join(" · ", new[] { Name, Admin3, Admin2, Admin1, Country }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase));

    public bool HasCoordinates => double.IsFinite(Latitude) && double.IsFinite(Longitude) &&
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

/// <summary>某一天的天气。日期用 yyyy-MM-dd 文本持久化，与 HolidayEntry 同样的约定。</summary>
public sealed class WeatherDay
{
    public string Date { get; set; } = "";

    public WeatherCondition Condition { get; set; }
    /// <summary>当天实时天气现象。仅当天有值，缺失时回退到日预报现象。</summary>
    public WeatherCondition? CurrentCondition { get; set; }
    /// <summary>按日历日期选择现象：当天优先实时观测，其他日期使用日预报。</summary>
    public WeatherCondition ConditionFor(DateTime date) => date.Date == DateValue && CurrentCondition.HasValue
        ? CurrentCondition.Value
        : Condition;

    /// <summary>当天最高温（摄氏度）。缺数据时为 NaN，UI 不显示温度。</summary>
    public double TempMax { get; set; } = double.NaN;

    public double TempMin { get; set; } = double.NaN;

    /// <summary>降水概率（0-100）。接口没给时为 null。</summary>
    public int? PrecipitationProbability { get; set; }

    [JsonIgnore]
    public DateTime DateValue =>
        DateTime.TryParse(Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.Date
            : DateTime.MinValue;

    [JsonIgnore]
    public bool HasTemperature => double.IsFinite(TempMax) && double.IsFinite(TempMin);
}

/// <summary>一次拉取到的天气快照，同时也是本地缓存文件的格式。</summary>
public sealed class WeatherSnapshot
{
    public string City { get; set; } = "";

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    /// <summary>拉取时刻（UTC）。超过刷新周期后需要重新拉取。</summary>
    public DateTime FetchedAtUtc { get; set; }

    /// <summary>真正提供数据的接口名，状态栏展示用。</summary>
    public string Provider { get; set; } = "";

    public List<WeatherDay> Days { get; set; } = [];

    /// <summary>按日期建索引，供日历取数。损坏缓存里的 null 条目会被忽略。</summary>
    public IReadOnlyDictionary<DateTime, WeatherDay> ToMap() =>
        (Days ?? [])
            .Where(x => x is not null && x.DateValue != DateTime.MinValue)
            .GroupBy(x => x.DateValue)
            .ToDictionary(x => x.Key, x => x.First());
}
