using System.Globalization;
using System.Text.Json.Serialization;

namespace DesktopCalendarWidget;

/// <summary>休息日排班模式。</summary>
public enum RestPattern
{
    /// <summary>不标记，周末按周六/周日常规显示。</summary>
    None,
    /// <summary>每周单休：每周只有配置的那一天休息。</summary>
    Weekly,
    /// <summary>隔周单休（大小周）：单休周与双休周逐周交替。</summary>
    Alternate
}

/// <summary>单休时休息的那一天。None 仅用于兼容旧配置。</summary>
public enum SingleRestDay
{
    None,
    Saturday,
    Sunday
}

public sealed class WidgetSettings
{
    public const string DefaultHolidayUpdateUrl = "https://timor.tech/api/holiday/year/{0}";
    public const int CurrentSettingsVersion = 2;

    /// <summary>配置版本号。旧配置文件缺少该字段（为 null），用于一次性迁移。</summary>
    public int? SettingsVersion { get; set; }

    public double Opacity { get; set; } = 0.96;
    public bool IsLocked { get; set; }
    public bool IsClickThrough { get; set; }
    public bool StartWithWindows { get; set; }
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;

    /// <summary>休息日模式，默认不标记。</summary>
    public RestPattern RestPattern { get; set; } = RestPattern.None;

    /// <summary>单休日（隔周单休时指单休周里休息的那一天）。</summary>
    public SingleRestDay SingleRestDay { get; set; } = SingleRestDay.Sunday;

    /// <summary>隔周单休的锚点：该周周一。为 null 时按当前周计算。</summary>
    public DateTime? AnchorWeekStart { get; set; }

    /// <summary>锚点周是否为单休周。</summary>
    public bool AnchorWeekIsSingleRest { get; set; } = true;

    public bool ShowLunar { get; set; } = true;
    public bool ShowHolidays { get; set; } = true;
    public string HolidayUpdateUrl { get; set; } = DefaultHolidayUpdateUrl;
}

public sealed class HolidayEntry
{
    public string Date { get; set; } = "";
    public string? Name { get; set; }
    public bool IsWorkday { get; set; }
    public bool IsHoliday { get; set; } = true;

    [JsonIgnore]
    public DateTime DateValue =>
        DateTime.TryParse(Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.Date
            : DateTime.MinValue;
}
