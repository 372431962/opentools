namespace DesktopCalendarWidget;

/// <summary>
/// 休息日排班推算。一周以周一为起点；隔周单休从锚点周开始逐周交替。
/// 全部为纯函数，便于独立验证。
/// </summary>
public static class RestSchedule
{
    /// <summary>返回该日期所在周的周一（保留日期部分）。</summary>
    public static DateTime WeekStart(DateTime date)
    {
        var day = date.Date;
        var offset = ((int)day.DayOfWeek + 6) % 7; // 周一 = 0，周日 = 6
        return day.AddDays(-offset);
    }

    /// <summary>锚点周（周一）。锚点为空时以当天所在周为准。</summary>
    public static DateTime ResolveAnchor(DateTime? anchorWeekStart) => WeekStart(anchorWeekStart ?? DateTime.Today);

    /// <summary>判断该日期所在周是否为单休周。</summary>
    public static bool IsSingleRestWeek(DateTime date, DateTime? anchorWeekStart, bool anchorIsSingleRest)
    {
        var weeks = (int)Math.Round((WeekStart(date) - ResolveAnchor(anchorWeekStart)).TotalDays / 7.0);
        var sameParity = ((weeks % 2) + 2) % 2 == 0;
        return sameParity ? anchorIsSingleRest : !anchorIsSingleRest;
    }

    /// <summary>判断该日期是否为休息日。非周末一律返回 false。</summary>
    public static bool IsRestDay(DateTime date, RestPattern pattern, SingleRestDay restDay, DateTime? anchorWeekStart, bool anchorIsSingleRest)
    {
        if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) return false;
        return pattern switch
        {
            RestPattern.Weekly => IsConfiguredRestWeekday(date.DayOfWeek, restDay),
            // 单休周只休配置的那一天；双休周周六周日都休。
            RestPattern.Alternate => IsSingleRestWeek(date, anchorWeekStart, anchorIsSingleRest)
                ? IsConfiguredRestWeekday(date.DayOfWeek, restDay)
                : true,
            _ => false
        };
    }

    private static bool IsConfiguredRestWeekday(DayOfWeek weekday, SingleRestDay restDay) => restDay switch
    {
        SingleRestDay.Saturday => weekday == DayOfWeek.Saturday,
        SingleRestDay.Sunday => weekday == DayOfWeek.Sunday,
        _ => weekday is DayOfWeek.Saturday or DayOfWeek.Sunday
    };
}
