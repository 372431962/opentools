using System.IO;
using System.Text.Json;

namespace DesktopCalendarWidget;

/// <summary>
/// 从 1.6 之前的 courses.json / events.json 迁移到统一的 schedules.json。
/// 旧类型只在这里出现，迁移完就随旧文件一起退场，界面上不再需要区分「课程」和「日程」。
/// 旧课程存的是节次，靠 settings.PeriodTimes 反查成时刻；查不到的课不编造时间，
/// 迁成「时间待定」，界面上明说，用户补上时刻表或直接改时刻即可。
/// </summary>
internal static class LegacyMigration
{
    // 属性名必须与旧文件的 JSON 字段逐一对应，改了名字就读不出旧数据了。
    private sealed class LegacyCourse
    {
        public string? Name { get; set; }
        public int DayOfWeek { get; set; }
        public int StartPeriod { get; set; } = 1;
        public int EndPeriod { get; set; } = 1;
        public string? Location { get; set; }
        public string? Teacher { get; set; }
        public string? WeekType { get; set; }
        public int StartWeek { get; set; } = 1;
        public int EndWeek { get; set; }
        public string? Color { get; set; }
    }

    private sealed class LegacyEvent
    {
        public Guid Id { get; set; }
        public string? Title { get; set; }
        public DateTime Date { get; set; }
        public bool AllDay { get; set; }
        public TimeOnly? StartTime { get; set; }
        public TimeOnly? EndTime { get; set; }
        public string? Location { get; set; }
        public string? Notes { get; set; }
    }

    internal sealed record Result(List<ScheduleItem> Items, int DroppedCourses, int TimePendingCourses, bool Migrated);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static Result Run(string folder, IReadOnlyDictionary<int, string> periodTimes)
    {
        var courses = Read<List<LegacyCourse>>(Path.Combine(folder, "courses.json"));
        var events = Read<List<LegacyEvent>>(Path.Combine(folder, "events.json"));
        if (courses is null && events is null)
            return new Result([], 0, 0, false);

        var items = new List<ScheduleItem>();
        var dropped = 0;
        var pending = 0;
        foreach (var course in courses ?? [])
        {
            var item = ConvertCourse(course, periodTimes, ref pending);
            if (item is null) { dropped++; continue; }
            items.Add(item);
        }
        foreach (var entry in events ?? [])
        {
            var item = ConvertEvent(entry);
            if (item is null) { dropped++; continue; }
            items.Add(item);
        }
        return new Result(Agenda.Normalize(items), dropped, pending, true);
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ScheduleItem? ConvertCourse(LegacyCourse course,
        IReadOnlyDictionary<int, string> periodTimes, ref int timePending)
    {
        if (string.IsNullOrWhiteSpace(course.Name) || course.DayOfWeek is < 1 or > 7) return null;
        var start = Agenda.PeriodStart(periodTimes, course.StartPeriod);
        var end = Agenda.PeriodEnd(periodTimes, course.EndPeriod);
        // 只有一个端点查得到也无法拼出完整时段，宁可整条都算「时间待定」，不要给半截时间。
        var hasBoth = start is not null && end is not null && end > start;
        if (!hasBoth) timePending++;
        return new ScheduleItem
        {
            Kind = ScheduleKind.Course,
            Title = course.Name,
            StartTime = hasBoth ? start : null,
            EndTime = hasBoth ? end : null,
            Recurrence = new WeeklyRecurrence
            {
                DayOfWeek = course.DayOfWeek,
                StartWeek = Math.Max(1, course.StartWeek),
                EndWeek = course.EndWeek,
                WeekType = ScheduleWeekType.Normalize(course.WeekType)
            },
            Location = course.Location,
            Notes = course.Teacher,
            Color = course.Color
        };
    }

    private static ScheduleItem? ConvertEvent(LegacyEvent entry)
    {
        if (entry.Id == Guid.Empty || string.IsNullOrWhiteSpace(entry.Title)) return null;
        if (entry.Date.Date == DateTime.MinValue) return null;
        var timed = !entry.AllDay && entry.StartTime is not null && entry.EndTime is not null && entry.EndTime > entry.StartTime;
        return new ScheduleItem
        {
            // 旧 id 已经保证非空（上面筛过），沿用它，日程在升级后仍是同一条。
            Id = entry.Id,
            // 旧编辑器没有类型概念，一律落到「其他」，用户自己改成会议/出行。
            Kind = ScheduleKind.Other,
            Title = entry.Title,
            Date = DateTime.SpecifyKind(entry.Date.Date, DateTimeKind.Unspecified),
            StartTime = timed ? entry.StartTime : null,
            EndTime = timed ? entry.EndTime : null,
            Location = entry.Location,
            Notes = entry.Notes
        };
    }
}
