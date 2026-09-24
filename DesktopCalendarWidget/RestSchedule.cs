namespace DesktopCalendarWidget;

/// <summary>
/// 休息日排班推算。一周以周一为起点；从手动锚点向后按调休后的周末重排。
/// 无假日参数的旧签名保留自然周行为；界面统一使用带假日映射的决策。
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

    /// <summary>同日工作记录优先，其次放假；无有效标记的记录只保留名称，不覆盖排班。</summary>
    public static IReadOnlyDictionary<DateTime, HolidayEntry> CreateHolidayMap(IEnumerable<HolidayEntry> holidays)
    {
        var result = new Dictionary<DateTime, HolidayEntry>();
        foreach (var entry in holidays)
        {
            var date = entry.DateValue;
            if (date == DateTime.MinValue) continue;
            if (!result.TryGetValue(date, out var previous) || Priority(entry) > Priority(previous))
                result[date] = entry;
        }
        return result;
    }

    private static int Priority(HolidayEntry entry) => entry.SchedulePriority;

    /// <summary>实际日期状态。ShowHolidays 只影响界面文字，不参与排班。</summary>
    public static DaySchedule GetDaySchedule(DateTime date, WidgetSettings settings, IReadOnlyDictionary<DateTime, HolidayEntry> holidays)
    {
        holidays.TryGetValue(date.Date, out var holiday);
        if (holiday?.IsWorkday == true) return new(false, true, false, holiday);
        if (holiday?.IsHoliday == true) return new(true, false, true, holiday);
        var plannedSingle = PlannedSingleRestWeek(date, settings, holidays);
        return new(PlannedRestDay(date, settings, plannedSingle), false, false, holiday);
    }

    /// <summary>计划周类型与调休后周末实际状态分开，避免“只剩周六休”被重新解释成周日休。</summary>
    public static WeekSchedule GetWeekSchedule(DateTime date, WidgetSettings settings, IReadOnlyDictionary<DateTime, HolidayEntry> holidays)
    {
        var week = WeekStart(date);
        var plannedSingle = PlannedSingleRestWeek(week, settings, holidays);
        return BuildWeek(week, settings, holidays, plannedSingle);
    }

    private static bool PlannedSingleRestWeek(DateTime date, WidgetSettings settings, IReadOnlyDictionary<DateTime, HolidayEntry> holidays)
    {
        if (settings.RestPattern != RestPattern.Alternate) return settings.RestPattern == RestPattern.Weekly;
        var target = WeekStart(date);
        var anchor = ResolveAnchor(settings.AnchorWeekStart);
        // 手动基准之前只按旧的自然周奇偶回推，历史假期不得改变手动基准。
        if (target < anchor) return IsSingleRestWeek(target, anchor, settings.AnchorWeekIsSingleRest);
        var plannedSingle = settings.AnchorWeekIsSingleRest;
        // 每周只查询两次字典；100 年约 5200 周，无需逐日扫描假日列表。
        for (var week = anchor; week < target; week = week.AddDays(7))
        {
            var actual = BuildWeek(week, settings, holidays, plannedSingle);
            plannedSingle = actual.WeekendRestDays switch
            {
                1 => false,
                2 => true,
                _ => !plannedSingle // 零休周沿用原计划翻转，不把零休当双休。
            };
        }
        return plannedSingle;
    }

    private static WeekSchedule BuildWeek(DateTime week, WidgetSettings settings, IReadOnlyDictionary<DateTime, HolidayEntry> holidays, bool plannedSingle)
    {
        bool IsRest(DateTime day)
        {
            if (holidays.TryGetValue(day, out var holiday))
            {
                if (holiday.IsWorkday) return false;
                if (holiday.IsHoliday) return true;
            }
            return PlannedRestDay(day, settings, plannedSingle);
        }
        return new(plannedSingle, IsRest(week.AddDays(5)), IsRest(week.AddDays(6)));
    }

    private static bool PlannedRestDay(DateTime date, WidgetSettings settings, bool plannedSingle)
    {
        if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) return false;
        return settings.RestPattern == RestPattern.None || !plannedSingle || IsConfiguredRestWeekday(date.DayOfWeek, settings.SingleRestDay);
    }

    /// <summary>仅首次设置、切入大小周或手动改变本周选择时移动锚点。</summary>
    public static void ApplyAnchorSelection(WidgetSettings settings, RestPattern previousPattern, DateTime displayedWeekStart,
        bool selectedSingleRest, bool selectionChanged)
    {
        if (settings.RestPattern == RestPattern.Alternate &&
            (previousPattern != RestPattern.Alternate || settings.AnchorWeekStart is null || selectionChanged))
        {
            // 缺少日期的旧大小周配置仍有计划周类型。普通保存只补齐日期，
            // 不能把调休后的实际单休重新解释为配置单休日的计划单休。
            var preserveOriginalPlan = previousPattern == RestPattern.Alternate &&
                settings.AnchorWeekStart is null && !selectionChanged;
            settings.AnchorWeekStart = WeekStart(displayedWeekStart);
            if (!preserveOriginalPlan) settings.AnchorWeekIsSingleRest = selectedSingleRest;
        }
    }

    private static bool IsConfiguredRestWeekday(DayOfWeek weekday, SingleRestDay restDay) => restDay switch
    {
        SingleRestDay.Saturday => weekday == DayOfWeek.Saturday,
        SingleRestDay.Sunday => weekday == DayOfWeek.Sunday,
        _ => weekday is DayOfWeek.Saturday or DayOfWeek.Sunday
    };
}

public readonly record struct DaySchedule(bool IsRestDay, bool IsWorkdayAdjustment, bool IsHoliday, HolidayEntry? Holiday);

public readonly record struct WeekSchedule(bool PlannedSingleRest, bool SaturdayIsRest, bool SundayIsRest)
{
    public int WeekendRestDays => (SaturdayIsRest ? 1 : 0) + (SundayIsRest ? 1 : 0);
    // 零休时勾选保留原计划；提示使用 WeekendRestDays 明确显示零休。
    public bool IsSingleRestWeek => WeekendRestDays == 1 || (WeekendRestDays == 0 && PlannedSingleRest);
}
