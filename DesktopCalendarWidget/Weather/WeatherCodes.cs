namespace DesktopCalendarWidget.Weather;

/// <summary>
/// 天气现象与图标、文案的对照。图标用 emoji（Win10/11 自带彩色字形），文案走 Loc 资源，
/// 因此中文界面显示中文现象名、英文界面显示英文。
/// </summary>
public static class WeatherCodes
{
    /// <summary>Open-Meteo 使用的 WMO 4677 天气代码 → 归一化天气现象。</summary>
    public static WeatherCondition FromWmo(int code) => code switch
    {
        0 => WeatherCondition.Clear,
        1 => WeatherCondition.Clear,
        2 => WeatherCondition.PartlyCloudy,
        3 => WeatherCondition.Overcast,
        45 or 48 => WeatherCondition.Fog,
        51 or 53 or 55 or 56 or 57 => WeatherCondition.Drizzle,
        61 => WeatherCondition.Rain,
        63 or 65 or 66 or 67 or 80 or 81 => WeatherCondition.HeavyRain,
        82 => WeatherCondition.HeavyRain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherCondition.Snow,
        95 or 96 or 99 => WeatherCondition.Thunderstorm,
        _ => WeatherCondition.Unknown
    };

    /// <summary>wttr.in 只给文字描述，按关键字归类。</summary>
    public static WeatherCondition FromDescription(string? description)
    {
        var text = (description ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return WeatherCondition.Unknown;
        if (text.Contains("thunder") || text.Contains("hail")) return WeatherCondition.Thunderstorm;
        if (text.Contains("snow") || text.Contains("blizzard") || text.Contains("sleet") || text.Contains("ice"))
            return WeatherCondition.Snow;
        if (text.Contains("drizzle")) return WeatherCondition.Drizzle;
        if (text.Contains("rain") || text.Contains("shower") || text.Contains("torrential"))
            return text.Contains("heavy") || text.Contains("torrential") ? WeatherCondition.HeavyRain : WeatherCondition.Rain;
        if (text.Contains("fog") || text.Contains("mist") || text.Contains("haze") || text.Contains("smoke"))
            return WeatherCondition.Fog;
        if (text.Contains("overcast")) return WeatherCondition.Overcast;
        if (text.Contains("cloud")) return text.Contains("partly") || text.Contains("patchy") || text.Contains("scattered")
            ? WeatherCondition.PartlyCloudy
            : WeatherCondition.Overcast;
        if (text.Contains("sunny") || text.Contains("clear")) return WeatherCondition.Clear;
        return WeatherCondition.Unknown;
    }

    public static string Icon(WeatherCondition condition) => condition switch
    {
        WeatherCondition.Clear => "\u2600\uFE0F",
        WeatherCondition.PartlyCloudy => "\u26C5",
        WeatherCondition.Overcast => "\u2601\uFE0F",
        WeatherCondition.Fog => "\uD83C\uDF2B\uFE0F",
        WeatherCondition.Drizzle => "\uD83C\uDF26\uFE0F",
        WeatherCondition.Rain => "\uD83C\uDF27\uFE0F",
        WeatherCondition.HeavyRain => "\uD83C\uDF27\uFE0F",
        WeatherCondition.Snow => "\uD83C\uDF28\uFE0F",
        WeatherCondition.Thunderstorm => "\u26C8\uFE0F",
        _ => ""
    };

    /// <summary>现象文案。键名即 Loc 资源键，写错直接编译失败。</summary>
    public static string Label(WeatherCondition condition) => condition switch
    {
        WeatherCondition.Clear => Loc.WeatherClear,
        WeatherCondition.PartlyCloudy => Loc.WeatherPartlyCloudy,
        WeatherCondition.Overcast => Loc.WeatherOvercast,
        WeatherCondition.Fog => Loc.WeatherFog,
        WeatherCondition.Drizzle => Loc.WeatherDrizzle,
        WeatherCondition.Rain => Loc.WeatherRain,
        WeatherCondition.HeavyRain => Loc.WeatherHeavyRain,
        WeatherCondition.Snow => Loc.WeatherSnow,
        WeatherCondition.Thunderstorm => Loc.WeatherThunderstorm,
        _ => Loc.WeatherUnknown
    };
}
