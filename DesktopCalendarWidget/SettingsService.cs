using System.IO;
using System.Text.Json;

namespace DesktopCalendarWidget;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DataFolder { get; }

    public SettingsService(string? dataFolder = null) => DataFolder = dataFolder ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCalendarWidget");

    private string SettingsPath => Path.Combine(DataFolder, "settings.json");
    private string HolidaysPath => Path.Combine(DataFolder, "holidays.json");
    private string SchedulesPath => Path.Combine(DataFolder, "schedules.json");
    private string ReminderHistoryPath => Path.Combine(DataFolder, "course-reminders.json");

    public WidgetSettings Load()
    {
        Directory.CreateDirectory(DataFolder);
        if (!File.Exists(SettingsPath)) return new WidgetSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new WidgetSettings();
            Migrate(settings);
            if (string.IsNullOrWhiteSpace(settings.HolidayUpdateUrl) || !settings.HolidayUpdateUrl.Contains("{0}", StringComparison.Ordinal))
                settings.HolidayUpdateUrl = WidgetSettings.DefaultHolidayUpdateUrl;
            return settings;
        }
        catch
        {
            return new WidgetSettings();
        }
    }

    /// <summary>把旧版配置升级到当前版本：旧版只有「周六单休/周日单休/不标记」，等价于每周单休。</summary>
    internal static void Migrate(WidgetSettings settings)
    {
        if (settings.SettingsVersion is not null) return;
        if (settings.SingleRestDay != SingleRestDay.None) settings.RestPattern = RestPattern.Weekly;
        settings.SettingsVersion = WidgetSettings.CurrentSettingsVersion;
    }

    public void Save(WidgetSettings settings)
    {
        try
        {
            settings.SettingsVersion = WidgetSettings.CurrentSettingsVersion;
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public List<HolidayEntry> LoadHolidays()
    {
        Directory.CreateDirectory(DataFolder);
        if (File.Exists(HolidaysPath))
        {
            try
            {
                return JsonSerializer.Deserialize<List<HolidayEntry>>(File.ReadAllText(HolidaysPath), JsonOptions) ?? [];
            }
            catch
            {
            }
        }
        var defaults = DefaultHolidays();
        SaveHolidays(defaults);
        return defaults;
    }

    public void SaveHolidays(IEnumerable<HolidayEntry> holidays)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var normalized = holidays
                .Where(x => x.DateValue != DateTime.MinValue)
                .OrderBy(x => x.DateValue)
                .ThenBy(x => x.Name)
                .ToList();
            File.WriteAllText(HolidaysPath, JsonSerializer.Serialize(normalized, JsonOptions));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 统一日程数据。文件缺失时先看有没有 1.6 之前的 courses.json / events.json，有就迁移一次。
    /// <paramref name="dropped"/> 回传被丢弃的无效条目数：手改坏文件时调用方要能告诉用户，
    /// 否则坏行会无声消失，并在下一次保存时被真正删掉。
    /// </summary>
    public List<ScheduleItem> LoadSchedules(out int dropped, out int timePending)
    {
        dropped = 0;
        timePending = 0;
        try
        {
            Directory.CreateDirectory(DataFolder);
            if (!File.Exists(SchedulesPath))
            {
                var migration = LegacyMigration.Run(DataFolder, Agenda.ParsePeriodTimes(PeriodTimesForMigration));
                if (!migration.Migrated) return [];
                SaveSchedules(migration.Items);
                dropped = migration.DroppedCourses;
                timePending = migration.TimePendingCourses;
                return migration.Items;
            }
            var source = JsonSerializer.Deserialize<List<ScheduleItem?>>(File.ReadAllText(SchedulesPath), JsonOptions);
            var normalized = Agenda.Normalize(source);
            dropped = CountDropped(source, Agenda.IsValid);
            timePending = normalized.Count(Agenda.IsTimePending);
            return normalized;
        }
        catch
        {
            return [];
        }
    }

    public List<ScheduleItem> LoadSchedules() => LoadSchedules(out _, out _);

    /// <summary>
    /// 迁移旧课程要拿节次时刻表换算成时刻。SettingsService 自己不读配置，
    /// 所以调用方必须在 LoadSchedules 之前把 settings.PeriodTimes 放进来；
    /// 没放的话课程会迁成「时间待定」，用户可在设置里补时刻。
    /// </summary>
    public string? PeriodTimesForMigration { get; set; }

    public bool SaveSchedules(IEnumerable<ScheduleItem?> items)
    {
        try
        {
            var source = items.ToList();
            var normalized = Agenda.Normalize(source);
            if (normalized.Count != source.Count) return false;
            Directory.CreateDirectory(DataFolder);
            WriteJsonAtomically(SchedulesPath, normalized);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int CountDropped<T>(IEnumerable<T?>? items, Func<T?, bool> isValid) =>
        items?.Count(item => !isValid(item)) ?? 0;

    public HashSet<string> LoadReminderKeys(DateTime today)
    {
        try
        {
            if (!File.Exists(ReminderHistoryPath)) return [];
            var keys = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(ReminderHistoryPath), JsonOptions);
            return RecentReminderKeys(keys, today);
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public bool SaveReminderKeys(IEnumerable<string> keys, DateTime today)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            WriteJsonAtomically(ReminderHistoryPath, RecentReminderKeys(keys, today));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static HashSet<string> RecentReminderKeys(IEnumerable<string>? keys, DateTime today)
    {
        var earliest = today.Date.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return (keys ?? []).Where(key => key is { Length: >= 10 } &&
            string.CompareOrdinal(key[..10], earliest) >= 0).ToHashSet(StringComparer.Ordinal);
    }
    private static void WriteJsonAtomically<T>(string path, T data)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static List<HolidayEntry> DefaultHolidays() =>
    [
        new() { Date = "2025-01-01", Name = "元旦" },
        new() { Date = "2025-01-28", Name = "春节" }, new() { Date = "2025-01-29", Name = "春节" }, new() { Date = "2025-01-30", Name = "春节" }, new() { Date = "2025-01-31", Name = "春节" }, new() { Date = "2025-02-01", Name = "春节" }, new() { Date = "2025-02-02", Name = "春节" }, new() { Date = "2025-02-03", Name = "春节" },
        new() { Date = "2025-04-04", Name = "清明节" }, new() { Date = "2025-04-05", Name = "清明节" }, new() { Date = "2025-04-06", Name = "清明节" },
        new() { Date = "2025-05-01", Name = "劳动节" }, new() { Date = "2025-05-02", Name = "劳动节" }, new() { Date = "2025-05-03", Name = "劳动节" }, new() { Date = "2025-05-04", Name = "劳动节" }, new() { Date = "2025-05-05", Name = "劳动节" },
        new() { Date = "2025-05-31", Name = "端午节" }, new() { Date = "2025-06-01", Name = "端午节" }, new() { Date = "2025-06-02", Name = "端午节" },
        new() { Date = "2025-10-01", Name = "国庆节" }, new() { Date = "2025-10-02", Name = "国庆节" }, new() { Date = "2025-10-03", Name = "国庆节" }, new() { Date = "2025-10-04", Name = "国庆节" }, new() { Date = "2025-10-05", Name = "国庆节" }, new() { Date = "2025-10-06", Name = "国庆节" }, new() { Date = "2025-10-07", Name = "国庆节" },
        new() { Date = "2026-01-01", Name = "元旦" }, new() { Date = "2026-02-17", Name = "春节" }, new() { Date = "2026-04-05", Name = "清明节" }, new() { Date = "2026-05-01", Name = "劳动节" }, new() { Date = "2026-06-19", Name = "端午节" }, new() { Date = "2026-09-25", Name = "中秋节" }, new() { Date = "2026-10-01", Name = "国庆节" }
    ];
}
