using System.Globalization;
using System.Text.Json.Serialization;

using DesktopCalendarWidget.Weather;
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

    /// <summary>界面语言（zh-CN / en-US）。改动只在重启后生效。</summary>
    public string Language { get; set; } = Loc.ChineseTag;

    public bool IsLocked { get; set; }
    public bool IsClickThrough { get; set; }

    /// <summary>窗口置顶。菜单里切换后立即记住，重启后保持。</summary>
    public bool IsTopmost { get; set; }
    public bool StartWithWindows { get; set; }
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;

    /// <summary>
    /// 窗口宽高。与位置一样记住，重启后不再被拉回默认尺寸。
    /// 高度 940：日历格子 81px，够放下日期号、天气、课程摘要和节日名四层（最坏 61px）；
    /// 同时给底部日程条留 150px，够看四条。之前 820 时两者只能分 61px 和 112px，
    /// 结果是节日名被挤出格子、摘要区只剩两行。加高比让两者抢空间正确。
    /// </summary>
    public double Width { get; set; } = 640;
    public double Height { get; set; } = 940;

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

    // ---- 翻译模块 ----
    /// <summary>翻译接口选择：auto / mymemory / google / offline。字符串取值见 TranslateService.Setting*。</summary>
    public string TranslateProvider { get; set; } = "auto";

    /// <summary>在线接口都失败时是否用内置词典直译兜底。</summary>
    public bool TranslateOfflineFallback { get; set; } = true;

    /// <summary>翻译窗口置顶，显示在桌面所有窗口之上。</summary>
    public bool TranslateTopmost { get; set; }

    public double TranslateLeft { get; set; } = 240;
    public double TranslateTop { get; set; } = 180;
    public double TranslateWidth { get; set; } = 500;
    public double TranslateHeight { get; set; } = 396;

    // ---- 天气 ----
    /// <summary>默认天气城市。</summary>
    public const string DefaultWeatherCity = "北京";

    /// <summary>是否在日期上显示天气和温度。</summary>
    public bool ShowWeather { get; set; } = true;

    /// <summary>天气查询城市。查询会把城市名发送给第三方天气接口。</summary>
    public string WeatherCity { get; set; } = DefaultWeatherCity;

    /// <summary>用户明确选择的精确天气地点；null 表示旧配置/尚未选择，以 WeatherCity 做兼容查询。</summary>
    public WeatherLocation? WeatherLocation { get; set; }

    /// <summary>天气刷新间隔（分钟）。</summary>
    public int WeatherRefreshMinutes { get; set; } = 60;

    // ---- 日程 ----
    /// <summary>是否在日历上显示日程。没有日程时不产生任何渲染。</summary>
    public bool ShowSchedules { get; set; } = true;

    /// <summary>
    /// 日程面板默认展示的日期。null 表示跟随今天；改成固定日期后挂件每次启动都停在那一天。
    /// 抽屉里选中别的日期不会改这里。
    /// </summary>
    public string? SchedulePanelDate { get; set; }

    /// <summary>日程面板宽度（像素），抽屉展开时窗口变宽这么多。</summary>
    public double SchedulePanelWidth { get; set; } = 300;

    /// <summary>学期开始日期（第 1 周所在周）；推算时按所在周周一归一，与大小周周界一致。null 表示未配置。</summary>
    public DateTime? SemesterStart { get; set; }

    /// <summary>学期总周数，超出后不显示课程。</summary>
    public int SemesterWeeks { get; set; } = 20;

    /// <summary>节次时刻表，形如 "1=08:00-08:45;2=08:55-09:40"。仅用于把旧课程迁成带时刻的日程。</summary>
    public string PeriodTimes { get; set; } = "";

    /// <summary>仅在程序运行且课程有确定开始时刻时提醒。</summary>
    public bool CourseRemindersEnabled { get; set; }
    public int CourseReminderMinutes { get; set; } = 10;

    // ---- 自动更新 ----
    /// <summary>是否自动检查新版本：启动后查一次，之后每 12 小时查一次。</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>上次检查更新的时间（UTC），用于跨启动抑制重复请求。</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>用户在更新提示里选了「不再提示」的那个版本号。</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>农历与中国节假日只属于中文界面：非中文语言下按关闭渲染，配置本身不变，切回中文即恢复。</summary>
    [JsonIgnore]
    public bool ShowLunarEffective => Loc.IsChinese && ShowLunar;

    [JsonIgnore]
    public bool ShowHolidaysEffective => Loc.IsChinese && ShowHolidays;

    /// <summary>
    /// 编辑用的副本。设置窗口改的是副本，点「取消」时丢弃即可，不会污染正在运行的配置
    /// （本类型只有值类型与不可变字符串字段，逐字段复制已足够）。
    /// </summary>
    public WidgetSettings Clone() => (WidgetSettings)MemberwiseClone();

    /// <summary>
    /// 把另一份配置的内容拷进本实例，引用保持不变。
    /// UpdateFlow、TranslateWindow 都长期持有配置引用并在其间调用 Save：保存设置时若直接
    /// 换成新对象，它们之后写盘的就是旧对象，会把整份 settings.json 覆盖回旧值。
    /// 新增配置字段时必须同步这里——回归测试会用反射核对没有遗漏。
    /// </summary>
    public void CopyFrom(WidgetSettings source)
    {
        SettingsVersion = source.SettingsVersion;
        Language = source.Language;
        IsLocked = source.IsLocked;
        IsClickThrough = source.IsClickThrough;
        IsTopmost = source.IsTopmost;
        StartWithWindows = source.StartWithWindows;
        Left = source.Left;
        Top = source.Top;
        Width = source.Width;
        Height = source.Height;
        RestPattern = source.RestPattern;
        SingleRestDay = source.SingleRestDay;
        AnchorWeekStart = source.AnchorWeekStart;
        AnchorWeekIsSingleRest = source.AnchorWeekIsSingleRest;
        ShowLunar = source.ShowLunar;
        ShowHolidays = source.ShowHolidays;
        HolidayUpdateUrl = source.HolidayUpdateUrl;
        TranslateProvider = source.TranslateProvider;
        TranslateOfflineFallback = source.TranslateOfflineFallback;
        TranslateTopmost = source.TranslateTopmost;
        TranslateLeft = source.TranslateLeft;
        TranslateTop = source.TranslateTop;
        TranslateWidth = source.TranslateWidth;
        TranslateHeight = source.TranslateHeight;
        ShowWeather = source.ShowWeather;
        WeatherCity = source.WeatherCity;
        WeatherRefreshMinutes = source.WeatherRefreshMinutes;
        WeatherLocation = source.WeatherLocation;
        ShowSchedules = source.ShowSchedules;
        SchedulePanelDate = source.SchedulePanelDate;
        SchedulePanelWidth = source.SchedulePanelWidth;
        SemesterStart = source.SemesterStart;
        SemesterWeeks = source.SemesterWeeks;
        PeriodTimes = source.PeriodTimes;
        CourseRemindersEnabled = source.CourseRemindersEnabled;
        CourseReminderMinutes = source.CourseReminderMinutes;
        AutoCheckUpdates = source.AutoCheckUpdates;
        LastUpdateCheckUtc = source.LastUpdateCheckUtc;
        SkippedUpdateVersion = source.SkippedUpdateVersion;
    }

    /// <summary>应用设置窗口的编辑，同时保留更新流程在窗口打开期间写入的检查状态。</summary>
    public void ApplyEditedSettings(WidgetSettings edited)
    {
        var lastCheck = LastUpdateCheckUtc;
        var skippedVersion = SkippedUpdateVersion;
        CopyFrom(edited);
        LastUpdateCheckUtc = lastCheck;
        SkippedUpdateVersion = skippedVersion;
    }
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

    /// <summary>同一天出现多条记录时的取舍优先级：调休上班 &gt; 放假 &gt; 只有名称。</summary>
    [JsonIgnore]
    public int SchedulePriority => IsWorkday ? 2 : IsHoliday ? 1 : 0;
}
