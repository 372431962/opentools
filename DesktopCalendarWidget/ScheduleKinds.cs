namespace DesktopCalendarWidget;

/// <summary>
/// 类型元数据：配色与图标。色板刻意选了明度接近的七种颜色，任何一种都不会在挂件上「喊」出来，
/// 但两两之间能分辨。课程沿用挂件主色的深红系，其余依次错开冷暖。
/// </summary>
public static class ScheduleKinds
{
    public const string CourseColor = "#8C4A2F";
    public const string MeetingColor = "#2F6B8C";
    public const string TravelColor = "#3F7A5A";
    public const string ReminderColor = "#8A6A2F";
    public const string BirthdayColor = "#A34A78";
    public const string ExamColor = "#6B4A8C";
    public const string OtherColor = "#6E6A63";

    public static string Color(ScheduleKind kind) => kind switch
    {
        ScheduleKind.Course => CourseColor,
        ScheduleKind.Meeting => MeetingColor,
        ScheduleKind.Travel => TravelColor,
        ScheduleKind.Reminder => ReminderColor,
        ScheduleKind.Birthday => BirthdayColor,
        ScheduleKind.Exam => ExamColor,
        _ => OtherColor
    };

    public static string Icon(ScheduleKind kind) => kind switch
    {
        ScheduleKind.Course => "📖",
        ScheduleKind.Meeting => "💬",
        ScheduleKind.Travel => "✈️",
        ScheduleKind.Reminder => "⏰",
        ScheduleKind.Birthday => "🎂",
        ScheduleKind.Exam => "📝",
        _ => "📌"
    };

    public static string Label(ScheduleKind kind) => kind switch
    {
        ScheduleKind.Course => Loc.KindCourse,
        ScheduleKind.Meeting => Loc.KindMeeting,
        ScheduleKind.Travel => Loc.KindTravel,
        ScheduleKind.Reminder => Loc.KindReminder,
        ScheduleKind.Birthday => Loc.KindBirthday,
        ScheduleKind.Exam => Loc.KindExam,
        _ => Loc.KindOther
    };

    /// <summary>下拉框与列表共用的固定顺序。</summary>
    public static IReadOnlyList<ScheduleKind> All { get; } =
    [
        ScheduleKind.Course, ScheduleKind.Meeting, ScheduleKind.Travel,
        ScheduleKind.Reminder, ScheduleKind.Birthday, ScheduleKind.Exam, ScheduleKind.Other
    ];

    /// <summary>只有课程是按学期周次重复的；其余类型都是一次性事件。</summary>
    public static bool SupportsRecurrence(ScheduleKind kind) => kind == ScheduleKind.Course;

    /// <summary>按名称解析类型，认不出就落到「其他」，避免手改 JSON 写错就整条记录作废。</summary>
    public static ScheduleKind Parse(string? value)
    {
        if (Enum.TryParse<ScheduleKind>(value, ignoreCase: true, out var kind) && Enum.IsDefined(kind)) return kind;
        return ScheduleKind.Other;
    }
}
