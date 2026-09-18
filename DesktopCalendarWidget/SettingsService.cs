using System.IO;
using System.Text.Json;

namespace DesktopCalendarWidget;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopCalendarWidget");

    private string SettingsPath => Path.Combine(DataFolder, "settings.json");
    private string HolidaysPath => Path.Combine(DataFolder, "holidays.json");

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
    private static void Migrate(WidgetSettings settings)
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
