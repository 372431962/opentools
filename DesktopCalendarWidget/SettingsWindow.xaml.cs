using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DesktopCalendarWidget.Update;
using DesktopCalendarWidget.Weather;

namespace DesktopCalendarWidget;

public partial class SettingsWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>天气刷新间隔的可选值（分钟）；旧配置的自定义值会动态加入，避免保存时被改写。</summary>
    private readonly List<int> refreshChoices = [30, 60, 180, 360];

    /// <summary>刷新间隔的合法区间，与主窗口 StartWeather 的 clamp 保持一致；无效旧值回落到默认 60。</summary>
    private static int NormalizeRefresh(int minutes) => minutes > 0 ? Math.Clamp(minutes, 5, 1440) : 60;

    private readonly SettingsService settingsService = new();
    private readonly HolidayService holidayService = new();
    private readonly UpdateFlow? updateFlow;
    private readonly CancellationTokenSource closeCancellation = new();
    private readonly DateTime currentWeekStart = RestSchedule.WeekStart(DateTime.Today);
    private readonly RestPattern originalRestPattern;
    private readonly string originalLanguage;
    private readonly bool originalShowWeather;
    private readonly string originalWeatherCity;
    private readonly WeatherLocation? originalWeatherLocation;
    private List<WeatherLocation> weatherLocations = [];
    private bool updatingWeatherLocationUi;
    private readonly int originalWeatherRefresh;
    private bool anchorWeekIsSingleAtLoad;
    private bool scheduleReady;

    public WidgetSettings Settings { get; private set; }
    public List<HolidayEntry> Holidays { get; private set; }

    /// <summary>保存时用户同意立刻重启，由主窗口在对话框关闭后执行。</summary>
    public bool RestartRequested { get; private set; }

    /// <summary>天气相关配置被改动过，主窗口据此立刻重拉一次天气。</summary>
    public bool WeatherChanged { get; private set; }

    /// <summary>
    /// 节假日已被写入磁盘。下载与「保存本地数据」都会立即落盘，因此即便用户随后选择取消，
    /// 主窗口也必须同步内存里的节假日，否则界面与磁盘不一致，要等下次启动才对得上。
    /// </summary>
    public bool HolidaysPersisted { get; private set; }

    public SettingsWindow(WidgetSettings settings, List<HolidayEntry> holidays, UpdateFlow? updateFlow)
    {
        InitializeComponent();
        Closing += (_, _) => closeCancellation.Cancel();
        this.updateFlow = updateFlow;
        // 编辑副本：点「取消」时整份丢弃，不会把改动留在主窗口正在用的配置对象上。
        Settings = settings.Clone();
        originalRestPattern = settings.RestPattern;
        originalLanguage = string.Equals(settings.Language, Loc.EnglishTag, StringComparison.OrdinalIgnoreCase)
            ? Loc.EnglishTag
            : Loc.ChineseTag;
        originalShowWeather = settings.ShowWeather;
        originalWeatherCity = settings.WeatherCity ?? WidgetSettings.DefaultWeatherCity;
        // 旧配置里可能是 0 或超出区间的值，先归一再回填，否则下拉会静默选中第一项。
        originalWeatherRefresh = NormalizeRefresh(settings.WeatherRefreshMinutes);
        Holidays = holidays.ToList();

        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.LanguageChinese });
        LanguageCombo.Items.Add(new ComboBoxItem { Content = Loc.LanguageEnglish });
        LanguageCombo.SelectedIndex = originalLanguage == Loc.EnglishTag ? 1 : 0;
        ShowLunarCheck.IsChecked = settings.ShowLunar;
        ShowHolidayCheck.IsChecked = settings.ShowHolidays;
        UpdateLanguagePanel();
        LockCheck.IsChecked = settings.IsLocked;
        ClickThroughCheck.IsChecked = settings.IsClickThrough;
        StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
        UpdateUrlBox.Text = settings.HolidayUpdateUrl;

        ShowWeatherCheck.IsChecked = settings.ShowWeather;
        originalWeatherLocation = settings.WeatherLocation;
        updatingWeatherLocationUi = true;
        WeatherCityBox.Text = originalWeatherLocation?.DisplayName ?? originalWeatherCity;
        weatherLocations = originalWeatherLocation is null ? [] : [originalWeatherLocation];
        WeatherLocationCombo.ItemsSource = weatherLocations;
        WeatherLocationCombo.SelectedItem = originalWeatherLocation;
        updatingWeatherLocationUi = false;
        WeatherLocationNote.Text = originalWeatherLocation is { HasCoordinates: true } selected
            ? Loc.Fmt(Loc.WeatherSelectedLocation, selected.DisplayName, selected.Latitude, selected.Longitude)
            : Loc.WeatherChooseLocation;
        if (!refreshChoices.Contains(originalWeatherRefresh)) refreshChoices.Add(originalWeatherRefresh);
        refreshChoices.Sort();
        foreach (var minutes in refreshChoices) WeatherRefreshCombo.Items.Add(new ComboBoxItem { Content = minutes.ToString(CultureInfo.InvariantCulture) });
        // 归一化后的值必定已在列表里，不需要再用 Math.Max 兜底。
        WeatherRefreshCombo.SelectedIndex = refreshChoices.IndexOf(originalWeatherRefresh);

        TranslateProviderCombo.SelectedIndex = settings.TranslateProvider switch
        {
            Translate.TranslateService.SettingMyMemory => 1,
            Translate.TranslateService.SettingGoogle => 2,
            Translate.TranslateService.SettingOffline => 3,
            _ => 0
        };
        TranslateOfflineFallbackCheck.IsChecked = settings.TranslateOfflineFallback;

        UpdateVersionText.Text = Loc.Fmt(Loc.UpdateCurrentVersionFormat, UpdateService.CurrentVersionText);
        AutoCheckUpdatesCheck.IsChecked = settings.AutoCheckUpdates;
        CheckUpdateButton.IsEnabled = updateFlow is not null;

        RestPatternCombo.SelectedIndex = settings.RestPattern switch
        {
            RestPattern.Weekly => 1,
            RestPattern.Alternate => 2,
            _ => 0
        };
        SingleRestCombo.SelectedIndex = settings.SingleRestDay == SingleRestDay.Saturday ? 0 : 1;
        anchorWeekIsSingleAtLoad = originalRestPattern != RestPattern.Alternate ||
            RestSchedule.GetWeekSchedule(currentWeekStart, settings, RestSchedule.CreateHolidayMap(Holidays)).IsSingleRestWeek;
        AnchorSingleCheck.IsChecked = anchorWeekIsSingleAtLoad;
        scheduleReady = true;
        RefreshCurrentWeek();

        UpdateAlternatePanel();
        RefreshHolidayEditor();
    }

    /// <summary>非中文界面强制隐藏农历与中国节假日，连带把无效的勾选框禁用。</summary>
    private void UpdateLanguagePanel()
    {
        ShowLunarCheck.IsEnabled = Loc.IsChinese;
        ShowHolidayCheck.IsEnabled = Loc.IsChinese;
        ChineseOnlyNote.Visibility = Loc.IsChinese ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RestPatternCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAlternatePanel();
        RefreshCurrentWeek();
    }

    private void SingleRestCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCurrentWeek();

    private void AnchorSingleCheck_Click(object sender, RoutedEventArgs e) => RefreshCurrentWeek();

    private void HolidayJsonBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (scheduleReady && TryReadHolidayEditor(out var parsed))
        {
            Holidays = parsed;
            RefreshCurrentWeek();
        }
    }

    private void RefreshCurrentWeek()
    {
        if (!scheduleReady) return;
        var manuallyChanged = (AnchorSingleCheck.IsChecked == true) != anchorWeekIsSingleAtLoad;
        var preview = new WidgetSettings
        {
            RestPattern = RestPattern.Alternate,
            SingleRestDay = SingleRestCombo.SelectedIndex == 0 ? SingleRestDay.Saturday : SingleRestDay.Sunday,
            AnchorWeekStart = Settings.AnchorWeekStart,
            AnchorWeekIsSingleRest = Settings.AnchorWeekIsSingleRest
        };
        RestSchedule.ApplyAnchorSelection(preview, originalRestPattern, currentWeekStart,
            AnchorSingleCheck.IsChecked == true, manuallyChanged);
        var week = RestSchedule.GetWeekSchedule(currentWeekStart, preview, RestSchedule.CreateHolidayMap(Holidays));
        // 数据更新可以刷新自动初值，但保留用户尚未保存的手动选择。
        if (!manuallyChanged && originalRestPattern == RestPattern.Alternate)
        {
            anchorWeekIsSingleAtLoad = week.IsSingleRestWeek;
            AnchorSingleCheck.IsChecked = anchorWeekIsSingleAtLoad;
        }
        AnchorWeekText.Text = Loc.Fmt(Loc.CurrentWeekFormat,
            currentWeekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            currentWeekStart.AddDays(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            week.WeekendRestDays);
    }

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
            UpdateStatus.Text = Loc.UrlNeedsPlaceholder;
            return;
        }
        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = Loc.Updating;
        try
        {
            var downloaded = new List<HolidayEntry>();
            foreach (var year in new[] { DateTime.Today.Year, DateTime.Today.Year + 1 })
                downloaded.AddRange(await holidayService.DownloadYearAsync(urlTemplate, year, closeCancellation.Token));
            closeCancellation.Token.ThrowIfCancellationRequested();
            if (downloaded.Count == 0) throw new InvalidDataException(Loc.NoHolidayData);

            // 地址只改编辑副本，由「保存」落盘；节假日按说明即时落盘，
            // 并置位让主窗口同步内存，避免取消后界面与磁盘不一致。
            Settings.HolidayUpdateUrl = urlTemplate;
            Holidays = Holidays.Where(x => !downloaded.Any(y => y.Date == x.Date)).Concat(downloaded).ToList();
            settingsService.SaveHolidays(Holidays);
            HolidaysPersisted = true;
            RefreshHolidayEditor();
            RefreshCurrentWeek();
            UpdateStatus.Text = Loc.Fmt(Loc.UpdateDone, downloaded.Count);
        }
        catch (OperationCanceledException) when (closeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = Loc.Fmt(Loc.UpdateFailed, ex.Message);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private async void WeatherSearchButton_Click(object sender, RoutedEventArgs e)
    {
        var query = WeatherCityBox.Text.Trim();
        if (query.Length == 0)
        {
            WeatherStatus.Text = Loc.WeatherSearchPrompt;
            return;
        }
        WeatherSearchButton.IsEnabled = false;
        WeatherStatus.Text = Loc.WeatherSearching;
        try
        {
            weatherLocations = await WeatherService.SearchLocationsAsync(query, closeCancellation.Token);
            WeatherLocationCombo.ItemsSource = weatherLocations;
            WeatherLocationCombo.SelectedIndex = -1;
            WeatherStatus.Text = weatherLocations.Count == 0 ? Loc.WeatherNoLocations : "";
        }
        catch (OperationCanceledException) when (closeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            WeatherStatus.Text = Loc.Fmt(Loc.WeatherFailed, ex.Message);
        }
        finally
        {
            WeatherSearchButton.IsEnabled = true;
        }
    }

    private void WeatherCityBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingWeatherLocationUi || WeatherLocationCombo is null) return;
        if (WeatherLocationCombo.SelectedItem is WeatherLocation selected &&
            !string.Equals(WeatherCityBox.Text, selected.DisplayName, StringComparison.Ordinal))
        {
            WeatherLocationCombo.SelectedItem = null;
            Settings.WeatherLocation = null;
            WeatherLocationNote.Text = Loc.WeatherChooseLocation;
        }
    }

    private void WeatherLocationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingWeatherLocationUi || WeatherLocationCombo.SelectedItem is not WeatherLocation selected) return;
        updatingWeatherLocationUi = true;
        WeatherCityBox.Text = selected.DisplayName;
        updatingWeatherLocationUi = false;
        Settings.WeatherLocation = selected;
        // 极端情况下（如手工编辑过配置）DisplayName 可能为空，不能把城市名覆盖成空串。
        if (selected.DisplayName.Length > 0) Settings.WeatherCity = selected.DisplayName;
        WeatherChanged = true;
        WeatherLocationNote.Text = Loc.Fmt(Loc.WeatherSelectedLocation, selected.DisplayName, selected.Latitude, selected.Longitude);
    }
    private async void WeatherUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        WeatherUpdateButton.IsEnabled = false;
        WeatherStatus.Text = Loc.WeatherUpdating;
        WeatherChanged = true;
        try
        {
            var city = WeatherCityBox.Text.Trim();
            // 预览用独立实例与临时缓存：用户可能点「取消」，
            // 不能把尚未保存的城市写进共享服务和主窗口正在使用的缓存文件。
            var previewPath = Path.Combine(Path.GetTempPath(), "DesktopCalendarWidget-weather-preview.json");
            var preview = new WeatherService(previewPath);
            var updated = await preview.LoadAsync(city, Settings.WeatherLocation, TimeSpan.Zero, force: true, closeCancellation.Token);
            var current = preview.Current;
            if (!updated || current is null)
            {
                WeatherStatus.Text = Loc.Fmt(Loc.WeatherFailed, Loc.WeatherUnknown);
                return;
            }
            WeatherChanged = true;
            WeatherStatus.Text = Loc.Fmt(Loc.WeatherUpdatedFormat, current.Provider, current.Days.Count);
        }
        catch (OperationCanceledException) when (closeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            WeatherStatus.Text = Loc.Fmt(Loc.WeatherFailed, ex.Message);
        }
        finally
        {
            WeatherUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>手动检查更新：复用主窗口的更新流程，弹窗与下载都在那里完成。</summary>
    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (updateFlow is null) return;
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateStatus.Text = Loc.UpdateChecking;
        try
        {
            var status = await updateFlow.CheckNowAsync();
            CheckUpdateStatus.Text = status;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
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
            UpdateStatus.Text = Loc.Fmt(Loc.LocalFormatError, ex.Message);
            return false;
        }
    }

    private void SaveHolidayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadHolidayEditor(out var parsed)) return;
        Holidays = parsed;
        settingsService.SaveHolidays(Holidays);
        HolidaysPersisted = true;
        RefreshCurrentWeek();
        UpdateStatus.Text = Loc.Fmt(Loc.LocalSavedCount, Holidays.Count);
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TryReadHolidayEditor(out var parsed))
            {
                Holidays = parsed;
                settingsService.SaveHolidays(Holidays);
                HolidaysPersisted = true;
                RefreshCurrentWeek();
            }
            Directory.CreateDirectory(settingsService.DataFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", settingsService.DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = Loc.Fmt(Loc.OpenFolderFailed, ex.Message);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadHolidayEditor(out var parsed)) return;
        Holidays = parsed;
        RefreshCurrentWeek();

        Settings.ShowLunar = ShowLunarCheck.IsChecked == true;
        Settings.ShowHolidays = ShowHolidayCheck.IsChecked == true;
        Settings.IsLocked = LockCheck.IsChecked == true;
        Settings.IsClickThrough = ClickThroughCheck.IsChecked == true;
        Settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        Settings.RestPattern = RestPatternCombo.SelectedIndex switch
        {
            1 => RestPattern.Weekly,
            2 => RestPattern.Alternate,
            _ => RestPattern.None
        };
        Settings.SingleRestDay = SingleRestCombo.SelectedIndex == 0 ? SingleRestDay.Saturday : SingleRestDay.Sunday;

        Settings.ShowWeather = ShowWeatherCheck.IsChecked == true;
        var city = WeatherCityBox.Text.Trim();
        Settings.WeatherCity = city.Length == 0 ? WidgetSettings.DefaultWeatherCity : city;
        if (WeatherLocationCombo.SelectedItem is WeatherLocation selectedLocation)
        {
            Settings.WeatherLocation = selectedLocation;
            if (selectedLocation.DisplayName.Length > 0) Settings.WeatherCity = selectedLocation.DisplayName;
        }
        else if (Settings.WeatherLocation is not null &&
            !string.Equals(Settings.WeatherLocation.DisplayName, Settings.WeatherCity, StringComparison.Ordinal))
        {
            Settings.WeatherLocation = null;
        }
        Settings.WeatherRefreshMinutes = WeatherRefreshCombo.SelectedIndex >= 0
            ? refreshChoices[WeatherRefreshCombo.SelectedIndex]
            : originalWeatherRefresh;
        WeatherChanged = WeatherChanged ||
            Settings.ShowWeather != originalShowWeather ||
            !string.Equals(Settings.WeatherCity, originalWeatherCity, StringComparison.OrdinalIgnoreCase) ||
            !SameLocation(Settings.WeatherLocation, originalWeatherLocation) ||
            Settings.WeatherRefreshMinutes != originalWeatherRefresh;

        Settings.TranslateProvider = TranslateProviderCombo.SelectedIndex switch
        {
            1 => Translate.TranslateService.SettingMyMemory,
            2 => Translate.TranslateService.SettingGoogle,
            3 => Translate.TranslateService.SettingOffline,
            _ => Translate.TranslateService.SettingAuto
        };
        Settings.TranslateOfflineFallback = TranslateOfflineFallbackCheck.IsChecked == true;
        Settings.AutoCheckUpdates = AutoCheckUpdatesCheck.IsChecked == true;

        RestSchedule.ApplyAnchorSelection(Settings, originalRestPattern, currentWeekStart,
            AnchorSingleCheck.IsChecked == true, (AnchorSingleCheck.IsChecked == true) != anchorWeekIsSingleAtLoad);

        var urlTemplate = UpdateUrlBox.Text.Trim();
        if (urlTemplate.Contains("{0}", StringComparison.Ordinal)) Settings.HolidayUpdateUrl = urlTemplate;
        Settings.Language = LanguageCombo.SelectedIndex == 1 ? Loc.EnglishTag : Loc.ChineseTag;
        try
        {
            StartupService.SetEnabled(Settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            // 写注册表失败不能静默：用户以为勾上了开机启动，实际没生效。
            MessageBox.Show(this, Loc.Fmt(Loc.StartupFailedFormat, ex.Message), Loc.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // 主窗口接收对话框结果后先保存再重建语言界面；选“稍后”也会在下次启动生效。
        if (Settings.Language != originalLanguage)
            RestartRequested = MessageBox.Show(this, Loc.RestartQuestion, Loc.RestartCaption,
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        DialogResult = true;
        Close();
    }
    private static bool SameLocation(WeatherLocation? left, WeatherLocation? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.HasCoordinates && right.HasCoordinates &&
            Math.Abs(left.Latitude - right.Latitude) < 0.00001 &&
            Math.Abs(left.Longitude - right.Longitude) < 0.00001 &&
            string.Equals(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
