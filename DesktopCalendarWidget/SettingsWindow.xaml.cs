using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace DesktopCalendarWidget;

public partial class SettingsWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SettingsService settingsService = new();
    private readonly HolidayService holidayService = new();
    private readonly DateTime currentWeekStart = RestSchedule.WeekStart(DateTime.Today);
    private readonly bool anchorWeekIsSingleAtLoad;

    public WidgetSettings Settings { get; private set; }
    public List<HolidayEntry> Holidays { get; private set; }

    public SettingsWindow(WidgetSettings settings, List<HolidayEntry> holidays)
    {
        InitializeComponent();
        Settings = settings;
        Holidays = holidays.ToList();

        ShowLunarCheck.IsChecked = settings.ShowLunar;
        ShowHolidayCheck.IsChecked = settings.ShowHolidays;
        LockCheck.IsChecked = settings.IsLocked;
        ClickThroughCheck.IsChecked = settings.IsClickThrough;
        StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
        UpdateUrlBox.Text = settings.HolidayUpdateUrl;
        OpacitySlider.Value = Math.Clamp(settings.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);

        RestPatternCombo.SelectedIndex = settings.RestPattern switch
        {
            RestPattern.Weekly => 1,
            RestPattern.Alternate => 2,
            _ => 0
        };
        SingleRestCombo.SelectedIndex = settings.SingleRestDay == SingleRestDay.Saturday ? 0 : 1;

        // 勾选初值按现有锚点推算出的“本周实际状态”，这样改其他选项再保存不会打乱大小周节奏。
        anchorWeekIsSingleAtLoad = settings.RestPattern == RestPattern.Alternate
            ? RestSchedule.IsSingleRestWeek(DateTime.Today, settings.AnchorWeekStart, settings.AnchorWeekIsSingleRest)
            : true;
        AnchorSingleCheck.IsChecked = anchorWeekIsSingleAtLoad;
        AnchorWeekText.Text =
            $"本周：{currentWeekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}（周一） ~ " +
            $"{currentWeekStart.AddDays(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}（周日）";

        UpdateAlternatePanel();
        RefreshHolidayEditor();
    }

    private void RestPatternCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAlternatePanel();

    private void UpdateAlternatePanel()
    {
        if (AlternatePanel is null) return;
        AlternatePanel.Visibility = RestPatternCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshHolidayEditor() =>
        HolidayJsonBox.Text = string.Join(Environment.NewLine, Holidays.Select(x => JsonSerializer.Serialize(x, JsonOptions)));

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var urlTemplate = UpdateUrlBox.Text.Trim();
        if (!urlTemplate.Contains("{0}", StringComparison.Ordinal))
        {
            UpdateStatus.Text = "更新地址需要包含 {0} 作为年份占位符。";
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = "正在获取当前年和下一年数据…";
        try
        {
            var downloaded = new List<HolidayEntry>();
            foreach (var year in new[] { DateTime.Today.Year, DateTime.Today.Year + 1 })
                downloaded.AddRange(await holidayService.DownloadYearAsync(urlTemplate, year));
            if (downloaded.Count == 0) throw new InvalidDataException("接口没有返回可识别的日期数据。");

            // 仅更新内存中的地址，最终由“保存”按钮统一落盘，取消时不会改动配置。
            Settings.HolidayUpdateUrl = urlTemplate;
            Holidays = Holidays.Where(x => !downloaded.Any(y => y.Date == x.Date)).Concat(downloaded).ToList();
            settingsService.SaveHolidays(Holidays);
            RefreshHolidayEditor();
            UpdateStatus.Text = $"更新完成：获取 {downloaded.Count} 条，已保存到本地。";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = $"更新失败：{ex.Message}\n本地已有数据仍可继续使用。";
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private bool TryReadHolidayEditor(out List<HolidayEntry> parsed)
    {
        parsed = [];
        try
        {
            foreach (var line in HolidayJsonBox.Text.Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var item = JsonSerializer.Deserialize<HolidayEntry>(line, JsonOptions);
                if (item is not null && item.DateValue != DateTime.MinValue) parsed.Add(item);
            }
            return true;
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = $"本地数据格式错误：{ex.Message}";
            return false;
        }
    }

    private void SaveHolidayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadHolidayEditor(out var parsed)) return;
        Holidays = parsed;
        settingsService.SaveHolidays(Holidays);
        UpdateStatus.Text = $"已保存 {Holidays.Count} 条本地数据。";
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TryReadHolidayEditor(out var parsed))
            {
                Holidays = parsed;
                settingsService.SaveHolidays(Holidays);
            }
            Directory.CreateDirectory(settingsService.DataFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", settingsService.DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = $"无法打开目录：{ex.Message}";
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadHolidayEditor(out var parsed)) return;
        Holidays = parsed;

        Settings.ShowLunar = ShowLunarCheck.IsChecked == true;
        Settings.ShowHolidays = ShowHolidayCheck.IsChecked == true;
        Settings.IsLocked = LockCheck.IsChecked == true;
        Settings.IsClickThrough = ClickThroughCheck.IsChecked == true;
        Settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        Settings.Opacity = OpacitySlider.Value;
        Settings.RestPattern = RestPatternCombo.SelectedIndex switch
        {
            1 => RestPattern.Weekly,
            2 => RestPattern.Alternate,
            _ => RestPattern.None
        };
        Settings.SingleRestDay = SingleRestCombo.SelectedIndex == 0 ? SingleRestDay.Saturday : SingleRestDay.Sunday;

        if (Settings.RestPattern == RestPattern.Alternate)
        {
            var anchorIsSingle = AnchorSingleCheck.IsChecked == true;
            // 只在“锚点缺失”或“用户改了本周状态”时重设锚点，避免每次保存都移动锚点。
            if (Settings.AnchorWeekStart is null || anchorIsSingle != anchorWeekIsSingleAtLoad)
            {
                Settings.AnchorWeekStart = currentWeekStart;
                Settings.AnchorWeekIsSingleRest = anchorIsSingle;
            }
        }

        var urlTemplate = UpdateUrlBox.Text.Trim();
        if (urlTemplate.Contains("{0}", StringComparison.Ordinal)) Settings.HolidayUpdateUrl = urlTemplate;
        settingsService.Save(Settings);
        settingsService.SaveHolidays(Holidays);
        try { StartupService.SetEnabled(Settings.StartWithWindows); } catch { }
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
