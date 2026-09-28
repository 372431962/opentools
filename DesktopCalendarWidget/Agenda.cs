using System.Globalization;

namespace DesktopCalendarWidget;

/// <summary>
/// 日程推算。一次性条目按日期命中，每周重复条目按「学期周次 + 星期 + 起止周 + 单双周」命中。
/// 两种定位方式对调用方是同一条路径：<see cref="AgendaIndex.ItemsForDate"/> 收口，界面不需要判断类型。
/// </summary>
public static class Agenda
{
    /// <summary>星期数字：周一 = 1 … 周日 = 7，与 WeeklyRecurrence.DayOfWeek 同口径。</summary>
    public static int WeekdayNumber(DateTime date) => ((int)date.DayOfWeek + 6) % 7 + 1;

    /// <summary>该日期是学期第几周（1 起）。学期未配置、未开始或已结束都返回 null。</summary>
    public static int? WeekIndex(DateTime date, DateTime? semesterStart, int semesterWeeks)
    {
        if (semesterStart is null || semesterWeeks <= 0) return null;
        var start = RestSchedule.WeekStart(semesterStart.Value);
        var week = RestSchedule.WeekStart(date.Date);
        var index = (int)((week - start).TotalDays / 7) + 1;
        return index >= 1 && index <= semesterWeeks ? index : null;
    }

    /// <summary>
    /// 校验一条日程。定位方式必须恰好一套，时刻要么都给且结束晚于开始，要么都不给。
    /// 重复条目的「没有时刻」是合法状态：迁移过来的课若查不到节次时刻就是它，界面上显示「时间待定」。
    /// </summary>
    public static bool IsValid(ScheduleItem? item) => item is not null &&
        item.Id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(item.Title) &&
        Enum.IsDefined(item.Kind) &&
        HasValidTime(item) &&
        item.Date.HasValue ^ item.Recurrence is not null &&
        (!item.Date.HasValue || item.Date.Value.Date != DateTime.MinValue) &&
        IsValidRecurrence(item.Recurrence);

    private static bool HasValidTime(ScheduleItem item) => item.StartTime is null && item.EndTime is null ||
        (item.StartTime is not null && item.EndTime is not null && item.EndTime > item.StartTime);

    private static bool IsValidRecurrence(WeeklyRecurrence? recurrence) => recurrence is null ||
        (recurrence.DayOfWeek is >= 1 and <= 7 &&
         recurrence.StartWeek >= 1 &&
         (recurrence.EndWeek <= 0 || recurrence.EndWeek >= recurrence.StartWeek) &&
         ScheduleWeekType.IsValid(recurrence.WeekType));

    /// <summary>一次性且没有时刻 = 全天。重复且没有时刻不是全天，是「时间待定」，两者不能混为一谈。</summary>
    public static bool IsAllDay(ScheduleItem item) =>
        item.StartTime is null && item.EndTime is null && item.Recurrence is null;

    /// <summary>重复条目没配时刻：课不该显示成「全天」，但也拿不出真实时间。</summary>
    public static bool IsTimePending(ScheduleItem item) =>
        item.StartTime is null && item.EndTime is null && item.Recurrence is not null;

    /// <summary>"09:00-10:00"；全天与时间待定都返回空串。</summary>
    public static string TimeLabel(ScheduleItem item) =>
        item.StartTime is null || item.EndTime is null ? ""
            : $"{item.StartTime.Value:HH\\:mm}-{item.EndTime.Value:HH\\:mm}";

    /// <summary>按 Id 去重、裁剪空白、规范周类型，再按「日期 → 全天在前 → 时刻 → 标题」排好。</summary>
    public static List<ScheduleItem> Normalize(IEnumerable<ScheduleItem?>? items)
    {
        var seen = new HashSet<Guid>();
        var result = new List<ScheduleItem>();
        foreach (var item in items ?? [])
        {
            if (!IsValid(item) || !seen.Add(item!.Id)) continue;
            var clean = item.Copy();
            clean.Title = item.Title.Trim();
            clean.Location = Clean(item.Location);
            clean.Notes = Clean(item.Notes);
            clean.Color = Clean(item.Color);
            if (clean.Recurrence is not null) clean.Recurrence.WeekType = ScheduleWeekType.Normalize(clean.Recurrence.WeekType);
            // 日历日期没有时区概念，剥掉 Kind 再落盘，否则 JSON 里会冒出 "+08:00" 偏移。
            if (clean.Date is DateTime date) clean.Date = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
            result.Add(clean);
        }
        result.Sort(CompareForDisplay);
        return result;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 展示顺序：全天在前，其次有确定时刻的，再次是时间待定，最后按开始时刻和标题。
    /// 课程因此会出现在一节普通会议之后，但「时间待定」永远排最后，不会挤在时间轴中间。
    /// </summary>
    public static int CompareForDisplay(ScheduleItem a, ScheduleItem b)
    {
        var byBucket = TimeBucket(a).CompareTo(TimeBucket(b));
        if (byBucket != 0) return byBucket;
        var byStart = Nullable.Compare(a.StartTime, b.StartTime);
        if (byStart != 0) return byStart;
        var byTitle = string.CompareOrdinal(a.Title, b.Title);
        return byTitle != 0 ? byTitle : a.Id.CompareTo(b.Id);
    }

    private static int TimeBucket(ScheduleItem item) => IsAllDay(item) ? 0 : IsTimePending(item) ? 2 : 1;

    /// <summary>该条是否在某学期周上课：起止周（含端点）+ 单双周。EndWeek 不大于 0 表示不限。</summary>
    public static bool AppliesInWeek(WeeklyRecurrence recurrence, int weekIndex)
    {
        if (weekIndex < Math.Max(1, recurrence.StartWeek)) return false;
        if (recurrence.EndWeek > 0 && weekIndex > recurrence.EndWeek) return false;
        return recurrence.WeekType switch
        {
            ScheduleWeekType.Odd => weekIndex % 2 == 1,
            ScheduleWeekType.Even => weekIndex % 2 == 0,
            _ => true
        };
    }

    /// <summary>解析 "1=08:00-08:45;2=08:55-09:40"（分号或换行分隔）。无效片段跳过，不抛异常。</summary>
    public static IReadOnlyDictionary<int, string> ParsePeriodTimes(string? text)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var part in text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var period) ||
                period is < 1 or > 30) continue;
            var range = pair[1].Split('-', 2, StringSplitOptions.TrimEntries);
            if (range.Length != 2 ||
                !TimeOnly.TryParseExact(range[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !TimeOnly.TryParseExact(range[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ||
                end <= start) continue;
            result[period] = $"{start:HH\\:mm}-{end:HH\\:mm}";
        }
        return result;
    }

    /// <summary>从 "08:00-08:45" 里取开始时刻；解析不了返回 null。</summary>
    public static TimeOnly? PeriodStart(IReadOnlyDictionary<int, string> periodTimes, int period) =>
        periodTimes.TryGetValue(period, out var text) && TimeOnly.TryParseExact(
            text.Split('-', 2)[0].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? start
            : null;

    /// <summary>从 "08:00-08:45" 里取结束时刻；解析不了返回 null。</summary>
    public static TimeOnly? PeriodEnd(IReadOnlyDictionary<int, string> periodTimes, int period) =>
        periodTimes.TryGetValue(period, out var text) && TimeOnly.TryParseExact(
            text.Split('-', 2)[1].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            ? end
            : null;
}

/// <summary>
/// 按日期分桶的一次性条目 + 一小撮重复条目。重复条目依赖学期周次，无法预先分桶，
/// 但一门课也就几行，42 个日期格各扫一遍不构成瓶颈；一次性条目才是可能变多的那部分。
/// </summary>
public sealed class AgendaIndex
{
    private readonly IReadOnlyDictionary<DateTime, IReadOnlyList<ScheduleItem>> oneOffByDate;
    private readonly IReadOnlyList<ScheduleItem> recurring;

    public AgendaIndex(IEnumerable<ScheduleItem> items)
    {
        var oneOff = new Dictionary<DateTime, List<ScheduleItem>>();
        var repeat = new List<ScheduleItem>();
        foreach (var item in items ?? [])
        {
            if (!Agenda.IsValid(item)) continue;
            if (item.Date is DateTime date)
            {
                if (!oneOff.TryGetValue(date.Date, out var bucket)) oneOff[date.Date] = bucket = [];
                bucket.Add(item);
            }
            else repeat.Add(item);
        }
        foreach (var bucket in oneOff.Values) bucket.Sort(Agenda.CompareForDisplay);
        oneOffByDate = oneOff.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ScheduleItem>)pair.Value);
        recurring = repeat;
    }

    public static AgendaIndex Empty { get; } = new([]);

    /// <summary>某一天的日程，已按展示顺序排好。返回的列表只读。</summary>
    public IReadOnlyList<ScheduleItem> ItemsForDate(DateTime date, DateTime? semesterStart, int semesterWeeks)
    {
        var day = date.Date;
        var hasOneOff = oneOffByDate.TryGetValue(day, out var oneOff);
        var week = recurring.Count == 0 ? null : Agenda.WeekIndex(day, semesterStart, semesterWeeks);
        if (oneOff is null && week is null) return [];
        var result = new List<ScheduleItem>();
        if (oneOff is not null) result.AddRange(oneOff);
        if (week is int index)
        {
            var weekday = Agenda.WeekdayNumber(day);
            foreach (var item in recurring)
                if (item.Recurrence!.DayOfWeek == weekday && Agenda.AppliesInWeek(item.Recurrence, index))
                    result.Add(item);
        }
        result.Sort(Agenda.CompareForDisplay);
        return result;
    }
}
