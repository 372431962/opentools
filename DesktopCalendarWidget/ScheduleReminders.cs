using System.Globalization;

namespace DesktopCalendarWidget;

public sealed record ScheduleReminder(string Key, string Title, DateTime StartsAt, string? Location, ScheduleKind Kind);

/// <summary>
/// 上课提醒。只对课程有意义，且必须有确定的开始时刻——时间待定的课无法提醒，
/// 这与「已通知记录里也出现过待定课程」的历史行为一致，不在这里补默认时刻。
/// </summary>
public static class ScheduleReminders
{
    public static IReadOnlyList<ScheduleReminder> Due(DateTime now, AgendaIndex index,
        DateTime? semesterStart, int semesterWeeks, int minutesBefore, IReadOnlySet<string> delivered)
    {
        if (minutesBefore is < 0 or > 120 || semesterStart is null) return [];
        var due = new List<ScheduleReminder>();
        // 第一节课的提醒可能落在前一天，所以今天和明天都要看。
        foreach (var date in new[] { now.Date, now.Date.AddDays(1) })
        {
            foreach (var item in index.ItemsForDate(date, semesterStart, semesterWeeks))
            {
                if (item.Kind != ScheduleKind.Course || item.StartTime is not TimeOnly start) continue;
                var startsAt = date.Add(start.ToTimeSpan());
                var notifyAt = startsAt.AddMinutes(-minutesBefore);
                // 提前 0 分钟时给 1 分钟窗口，否则那一秒过去就永远错过。
                var deadline = minutesBefore == 0 ? startsAt.AddMinutes(1) : startsAt;
                if (now < notifyAt || now >= deadline) continue;
                var key = ReminderKey(item, date);
                if (!delivered.Contains(key)) due.Add(new ScheduleReminder(key, item.Title, startsAt, item.Location, item.Kind));
            }
        }
        return due;
    }

    /// <summary>
    /// 去重键带 Id 而不是课程名和地点的拼接：改个教室名不该让今天的提醒重新弹一遍。
    /// 前面 10 位是日期，SettingsService 按它裁剪历史文件。
    /// </summary>
    public static string ReminderKey(ScheduleItem item, DateTime date) =>
        $"{date:yyyy-MM-dd}|{item.Id:D}";
}
