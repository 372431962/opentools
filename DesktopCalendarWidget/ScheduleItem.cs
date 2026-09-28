namespace DesktopCalendarWidget;

/// <summary>日程类型。课程只是其中一种，其余是一次性事件；类型只影响配色、图标和重复规则的可选范围。</summary>
public enum ScheduleKind
{
    /// <summary>课程：按学期周次每周重复。</summary>
    Course,
    Meeting,
    Travel,
    Reminder,
    Birthday,
    Exam,
    Other
}

/// <summary>每周重复规则。填了它就表示这条日程按周重复，此时 <see cref="ScheduleItem.Date"/> 必须为空。</summary>
public sealed class WeeklyRecurrence
{
    /// <summary>星期几：1=周一 … 7=周日。</summary>
    public int DayOfWeek { get; set; } = 1;

    /// <summary>起止学期周（1 起，含端点）。</summary>
    public int StartWeek { get; set; } = 1;

    /// <summary>结束学期周；小于等于 0 表示不限。</summary>
    public int EndWeek { get; set; }

    /// <summary>all 每周 / odd 单周 / even 双周，取值见 <see cref="ScheduleWeekType"/>。</summary>
    public string WeekType { get; set; } = ScheduleWeekType.All;
}

/// <summary>
/// 一条日程。定位方式互斥且唯一：填 <see cref="Date"/> 是一次性，填 <see cref="Recurrence"/> 是每周重复，
/// 两者恰好填一套，由 <see cref="Agenda.IsValid"/> 强制。
/// </summary>
public sealed class ScheduleItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ScheduleKind Kind { get; set; }
    public string Title { get; set; } = "";

    /// <summary>一次性日程的日期；重复日程为 null。</summary>
    public DateTime? Date { get; set; }

    /// <summary>开始时刻；与 <see cref="EndTime"/> 同时为空表示没有时刻。</summary>
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    /// <summary>每周重复规则；一次性日程为 null。</summary>
    public WeeklyRecurrence? Recurrence { get; set; }

    public string? Location { get; set; }
    public string? Notes { get; set; }

    /// <summary>可选色标（如 "#8C4A2F"），留空则用类型默认色。目前只在高级 JSON 里可填。</summary>
    public string? Color { get; set; }

    public ScheduleItem Copy() => new()
    {
        Id = Id,
        Kind = Kind,
        Title = Title,
        Date = Date,
        StartTime = StartTime,
        EndTime = EndTime,
        Recurrence = Recurrence is null ? null : new WeeklyRecurrence
        {
            DayOfWeek = Recurrence.DayOfWeek,
            StartWeek = Recurrence.StartWeek,
            EndWeek = Recurrence.EndWeek,
            WeekType = Recurrence.WeekType
        },
        Location = Location,
        Notes = Notes,
        Color = Color
    };
}

public static class ScheduleWeekType
{
    public const string All = "all";
    public const string Odd = "odd";
    public const string Even = "even";

    public static bool IsValid(string? value) =>
        string.Equals(value, All, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, Odd, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, Even, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value) => IsValid(value) ? value!.ToLowerInvariant() : All;
}
