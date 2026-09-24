using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Effects;
using DesktopCalendarWidget.Update;
using DesktopCalendarWidget.Weather;

namespace DesktopCalendarWidget;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20L;
    private const long WsExLayered = 0x80000L;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModNoRepeat = 0x4000;
    private const int HotkeyToggleClickThrough = 0x0C01;
    private const int HotkeyToggleLock = 0x0C02;
    private const uint VirtualKeyC = 0x43;
    private const uint VirtualKeyL = 0x4C;

    private readonly SettingsService settingsService = new();
    private WidgetSettings settings = new();
    private List<HolidayEntry> holidays = [];
    private IReadOnlyDictionary<DateTime, HolidayEntry> holidayMap = new Dictionary<DateTime, HolidayEntry>();
    private IReadOnlyDictionary<DateTime, WeatherDay> weatherMap = new Dictionary<DateTime, WeatherDay>();
    /// <summary>上次渲染所用的天气快照引用。引用没变就不需要重绘，避免缓存命中时白刷一遍。</summary>
    private IReadOnlyDictionary<DateTime, WeatherDay>? renderedWeatherMap;
    private bool renderedWeatherStale;
    private DateTime displayedMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private HwndSource? windowSource;
    private IntPtr windowHandle;
    private bool clickThroughHotkeyAvailable;
    private bool lockHotkeyAvailable;
    private TrayIcon? trayIcon;
    private WeatherService? weatherService;
    private DispatcherTimer? weatherTimer;
    private DispatcherTimer? midnightTimer;
    private TranslateWindow? translateWindow;
    private UpdateFlow? updateFlow;
    private WeatherScene? weatherScene;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        // 动画层按当前高度算下落距离，尺寸变过就重建；隐藏时停掉，别在后台空转。
        SizeChanged += (_, _) => weatherScene?.RefreshLayout();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ApplyWeatherScene();
            else weatherScene?.Stop();
        };
        weatherScene = new WeatherScene(WeatherSceneLayer, WeatherParticles, WeatherFlash);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = PresentationSource.FromVisual(this) as HwndSource;
        windowSource?.AddHook(WndProc);
        windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero) return;
        clickThroughHotkeyAvailable = RegisterHotKey(windowHandle, HotkeyToggleClickThrough, ModControl | ModAlt | ModNoRepeat, VirtualKeyC);
        lockHotkeyAvailable = RegisterHotKey(windowHandle, HotkeyToggleLock, ModControl | ModAlt | ModNoRepeat, VirtualKeyL);
        trayIcon = new TrayIcon(windowHandle, BuildTrayToolTip(), Environment.ProcessPath);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        settings = settingsService.Load();
        holidays = settingsService.LoadHolidays();
        // 屏幕可能比 XAML 最小尺寸还小；先缩小最小值，再恢复尺寸和完整可见的位置。
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        MinWidth = Math.Min(MinWidth, Math.Max(1, screenWidth));
        MinHeight = Math.Min(MinHeight, Math.Max(1, screenHeight));
        Width = WindowPlacement.ClampSize(settings.Width, MinWidth, screenWidth);
        Height = WindowPlacement.ClampSize(settings.Height, MinHeight, screenHeight);
        Left = WindowPlacement.ClampPosition(settings.Left, SystemParameters.VirtualScreenLeft, screenWidth, Width, 80);
        Top = WindowPlacement.ClampPosition(settings.Top, SystemParameters.VirtualScreenTop, screenHeight, Height, 80);
        Topmost = settings.IsTopmost;
        // 主窗口保持透明，天气粒子直接绘制在桌面之上。

        var clickThroughDisabled = false;
        if (settings.IsClickThrough && !clickThroughHotkeyAvailable)
        {
            settings.IsClickThrough = false;
            settingsService.Save(settings);
            clickThroughDisabled = true;
        }
        ReportUnavailableHotkeys(clickThroughDisabled);

        ApplyLock();
        ApplyClickThrough();
        // 天气服务先建好并读入本地缓存，首屏就能显示上次的天气，不必等第一次联网刷新。
        weatherService ??= new WeatherService(Path.Combine(settingsService.DataFolder, "weather.json"));
        weatherService.LoadFromCache(settings.WeatherCity);
        RenderCalendar();
        StartWeather();
        ScheduleNextMidnightRefresh();
        // 切语言重建窗口时翻译窗会被搬过来，它的配置引用要跟上新读到的配置对象，
        // 否则它关闭时会把旧配置写回去，覆盖掉刚保存的语言等设置。
        translateWindow?.UpdateSettings(settings);
        updateFlow = new UpdateFlow(settingsService, settings, this);
    }

    /// <summary>快捷键被别的程序占用时必须告知，否则「勾了没反应」最难自查。穿透那条尤其危险。</summary>
    private void ReportUnavailableHotkeys(bool clickThroughDisabled)
    {
        var unavailable = new List<string>();
        if (!clickThroughHotkeyAvailable) unavailable.Add("Ctrl+Alt+C");
        if (!lockHotkeyAvailable) unavailable.Add("Ctrl+Alt+L");
        if (unavailable.Count == 0) return;
        var text = Loc.Fmt(Loc.HotkeysUnavailableFormat, string.Join(", ", unavailable));
        if (clickThroughDisabled) text = $"{Loc.HotkeyConflictText}{Environment.NewLine}{text}";
        MessageBox.Show(this, text, Loc.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>锁定位置同时禁止缩放：只挡拖动的话，边缘仍能把挂件拖变形。</summary>
    private void ApplyLock() => ResizeMode = settings.IsLocked ? ResizeMode.NoResize : ResizeMode.CanResize;


    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // 翻译窗不是本窗口的 Owner，挂件关闭时要一并关掉，否则进程会因“还有窗口”而不退出。
        updateFlow?.Dispose();
        updateFlow = null;
        weatherTimer?.Stop();
        weatherTimer = null;
        midnightTimer?.Stop();
        midnightTimer = null;
        weatherScene?.Stop();
        weatherScene = null;
        if (translateWindow is not null)
        {
            translateWindow.Close();
            translateWindow = null;
        }
        settings.Left = Left;
        settings.Top = Top;
        settings.Width = Width;
        settings.Height = Height;
        settings.IsTopmost = Topmost;
        settingsService.Save(settings);
        trayIcon?.Dispose();
        trayIcon = null;
        if (windowHandle == IntPtr.Zero) return;
        UnregisterHotKey(windowHandle, HotkeyToggleClickThrough);
        UnregisterHotKey(windowHandle, HotkeyToggleLock);
    }

    protected override void OnClosed(EventArgs e)
    {
        windowSource?.RemoveHook(WndProc);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TrayIcon.TaskbarCreatedMessage)
        {
            // 资源管理器重启后需要重新注册托盘图标
            trayIcon?.ReRegister(BuildTrayToolTip());
            return IntPtr.Zero;
        }

        if (SingleInstance.IsShowWidgetMessage(msg))
        {
            // 第二个实例启动时广播此消息：把挂件显示到最前
            ShowWidgetToFront();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmHotkey)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyToggleClickThrough:
                    settings.IsClickThrough = !settings.IsClickThrough;
                    settingsService.Save(settings);
                    ApplyClickThrough();
                    handled = true;
                    break;
                case HotkeyToggleLock:
                    settings.IsLocked = !settings.IsLocked;
                    settingsService.Save(settings);
                    ApplyLock();
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        switch (TrayIcon.ParseCallback(msg, lParam))
        {
            case TrayEvent.ContextMenu:
                ShowTrayMenu();
                handled = true;
                break;
            case TrayEvent.DoubleClick:
                ToggleWidgetVisibility();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private string BuildTrayToolTip()
    {
        var today = DateTime.Today;
        var parts = new List<string>
        {
            Loc.AppTitle,
            today.ToString(Loc.DateFormatLong, Loc.CurrentCulture)
        };
        if (settings.ShowLunarEffective)
        {
            var lunar = LunarCalendarConverter.Format(today);
            if (!string.IsNullOrEmpty(lunar)) parts.Add(lunar);
        }
        if (settings.RestPattern != RestPattern.None)
        {
            var week = RestSchedule.GetWeekSchedule(today, settings, holidayMap);
            parts.Add(week.WeekendRestDays switch
            {
                0 => Loc.WeekNoRest,
                1 => Loc.WeekSingleRest,
                _ => Loc.WeekDoubleRest
            });
        }
        return string.Join(" · ", parts);
    }

    private void ShowTrayMenu()
    {
        // 先激活窗口，托盘菜单在点击别处时才会正常关闭
        if (windowHandle != IntPtr.Zero) SetForegroundWindow(windowHandle);
        var menu = new ContextMenu();
        var settingsItem = new MenuItem { Header = Loc.MenuSettings };
        settingsItem.Click += (_, _) => OpenSettings();
        var translateItem = new MenuItem { Header = Loc.MenuTranslate };
        translateItem.Click += (_, _) => OpenTranslate();
        var visibilityItem = new MenuItem { Header = IsVisible ? Loc.MenuHideWidget : Loc.MenuShowWidget };
        visibilityItem.Click += (_, _) => ToggleWidgetVisibility();
        var updateItem = new MenuItem { Header = Loc.MenuCheckUpdate };
        updateItem.Click += (_, _) => CheckForUpdates();
        var exitItem = new MenuItem { Header = Loc.MenuExit };
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(settingsItem);
        menu.Items.Add(translateItem);
        menu.Items.Add(visibilityItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(updateItem);
        menu.Items.Add(exitItem);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>手动检查更新：结果用弹窗反馈，自动检查失败才是静默的。</summary>
    private async void CheckForUpdates()
    {
        if (updateFlow is null) return;
        var status = await updateFlow.CheckNowAsync();
        if (status.Length > 0)
            MessageBox.Show(this, status, Loc.UpdateCaption, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ToggleWidgetVisibility()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            Activate();
        }
    }

    private void ShowWidgetToFront()
    {
        if (!IsVisible) Show();
        var wasTopmost = Topmost;
        Topmost = true; // 临时置顶，确保能从其他窗口之下提到最前
        Activate();
        Topmost = wasTopmost;
        if (windowHandle != IntPtr.Zero) SetForegroundWindow(windowHandle);
    }

    private void RenderCalendar()
    {
        holidayMap = RestSchedule.CreateHolidayMap(holidays);
        weatherMap = weatherService?.Map ?? new Dictionary<DateTime, WeatherDay>();
        renderedWeatherMap = weatherMap;
        renderedWeatherStale = weatherService?.IsStale == true;
        MonthTitle.Text = displayedMonth.ToString(Loc.MonthTitleFormat, Loc.CurrentCulture);
        MonthCaption.Text = settings.ShowLunarEffective ? LunarCalendarConverter.GetYearLabel(displayedMonth) : "";
        var todayText = DateTime.Today.ToString(Loc.DateFormatLong, Loc.CurrentCulture);
        TodaySummary.Text = settings.ShowLunarEffective
            ? $"{Loc.TodayPrefix} {todayText} · {LunarCalendarConverter.Format(DateTime.Today)}"
            : $"{Loc.TodayPrefix} {todayText}";
        Legend.Text = BuildLegendText();
        ApplyWeatherScene();
        trayIcon?.SetToolTip(BuildTrayToolTip());
        CalendarGrid.Children.Clear();
        CalendarGrid.RowDefinitions.Clear();
        CalendarGrid.ColumnDefinitions.Clear();
        for (var column = 0; column < 7; column++) CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition());
        CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        for (var row = 0; row < 6; row++) CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 表格固定周日开头（列配色按 0/6 判断周末），只换文案不换顺序。
        string[] weekNames = Loc.WeekDayNames.Split(',');
        // 资源缺失时 Loc 回退键名（无逗号），Split 只有 1 项，按下标取 [1] 会越界；退回中文表头兜底。
        if (weekNames.Length < 7) weekNames = ["日", "一", "二", "三", "四", "五", "六"];
        for (var column = 0; column < 7; column++)
        {
            var header = new TextBlock
            {
                Text = weekNames[column],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = column is 0 or 6 ? FindBrush("WidgetWeekend") : FindBrush("WidgetMutedInk"),
                Effect = (Effect)FindResource("TextShadow")
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, column);
            CalendarGrid.Children.Add(header);
        }

        var startOffset = (int)displayedMonth.DayOfWeek;
        var daysInMonth = DateTime.DaysInMonth(displayedMonth.Year, displayedMonth.Month);

        for (var cell = 0; cell < 42; cell++)
        {
            var dayNumber = cell - startOffset + 1;
            if (dayNumber < 1 || dayNumber > daysInMonth) continue;
            var date = displayedMonth.AddDays(dayNumber - 1);
            var dayButton = BuildDayButton(date, RestSchedule.GetDaySchedule(date, settings, holidayMap));
            Grid.SetRow(dayButton, cell / 7 + 1);
            Grid.SetColumn(dayButton, cell % 7);
            CalendarGrid.Children.Add(dayButton);
        }
    }

    /// <summary>背景动画只跟当天天气走：开关关闭、没有当天数据或现象无法识别时退回静态底色。</summary>
    private void ApplyWeatherScene()
    {
        if (weatherScene is null) return;
        if (!IsVisible)
        {
            weatherScene.Stop();
            return;
        }
        var today = settings.ShowWeather && weatherMap.TryGetValue(DateTime.Today, out var day)
            ? day.Condition
            : (WeatherCondition?)null;
        weatherScene.Apply(today);
    }

    private string BuildLegendText()
    {
        var legend = settings.RestPattern == RestPattern.None ? Loc.LegendNoRest : Loc.LegendRest;
        if (settings.ShowWeather && weatherService?.Current is { Provider.Length: > 0 } snapshot)
        {
            legend = $"{legend}   {Loc.Fmt(Loc.WeatherSourceFormat, snapshot.Provider)}";
            // 拉取失败或缓存属于别的城市时继续显示旧数据，但必须说明，不能让人以为是实时的。
            if (weatherService.IsStale) legend = $"{legend}   {Loc.WeatherStaleNote}";
        }
        return legend;
    }

    private Button BuildDayButton(DateTime date, DaySchedule schedule)
    {
        var isToday = date.Date == DateTime.Today;
        var usesRestSchedule = settings.RestPattern != RestPattern.None;
        var holiday = schedule.Holiday;
        var isRestDay = schedule.IsRestDay;
        var isWorkdayAdjustment = schedule.IsWorkdayAdjustment;
        var isHoliday = schedule.IsHoliday;
        var weather = settings.ShowWeather && weatherMap.TryGetValue(date, out var found) ? found : null;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(CultureInfo.InvariantCulture),
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            Effect = (Effect)FindResource("TextShadow")
        });
        if (weather is not null && BuildWeatherLine(weather) is { } weatherLine) stack.Children.Add(weatherLine);
        if (settings.ShowLunarEffective)
        {
            stack.Children.Add(new TextBlock
            {
                Text = LunarCalendarConverter.Format(date),
                FontSize = 14,
                Foreground = FindBrush("WidgetMutedInk"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = (Effect)FindResource("TextShadow")
            });
        }

        var marker = "";
        var markerBrush = "WidgetAccent";
        if (settings.ShowHolidaysEffective && isWorkdayAdjustment)
        {
            marker = Loc.MarkerWorkday;
            markerBrush = "WidgetWorkday";
        }
        else if (settings.ShowHolidaysEffective && isHoliday)
        {
            marker = ShortHolidayName(holiday?.Name);
        }
        else if (usesRestSchedule && isRestDay)
        {
            marker = Loc.MarkerRest;
        }

        if (marker.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = marker,
                FontSize = 13,
                Foreground = FindBrush(markerBrush),
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = (Effect)FindResource("TextShadow")
            });
        }

        var toolTipText = BuildToolTip(date, settings.ShowHolidaysEffective ? holiday : null, weather);
        var button = new Button
        {
            Content = stack,
            Margin = new Thickness(3),
            Padding = new Thickness(3),
            BorderThickness = new Thickness(isToday ? 1.5 : 0.75),
            BorderBrush = isToday ? FindBrush("WidgetAccent") : FindBrush("Border"),
            Background = isToday ? FindBrush("TodayBackground") : FindBrush("CalendarCellBackground"),
            ToolTip = toolTipText
        };

        // 屏幕阅读器读 AutomationProperties.Name，ToolTip 不参与 UI Automation，必须单独给。
        AutomationProperties.SetName(button, toolTipText.Replace(Environment.NewLine, " "));

        if (isWorkdayAdjustment) button.Foreground = FindBrush("WidgetWorkday");
        else if (isRestDay) button.Foreground = FindBrush("WidgetWeekend");

        button.MouseDoubleClick += (_, _) => GoToToday();
        return button;
    }

    /// <summary>
    /// 格子上的天气那一行：现象图标 + 最高/最低温度。图标比温度大一号，隔着几米也能认出天气；
    /// 两者都拿不到时返回 null，这一行直接不占高度。完整信息在悬停提示里。
    /// </summary>
    private UIElement? BuildWeatherLine(WeatherDay weather)
    {
        var icon = WeatherCodes.Icon(weather.Condition);
        var range = weather.HasTemperature ? $"{(int)Math.Round(weather.TempMax)}/{(int)Math.Round(weather.TempMin)}°" : null;
        if (icon.Length == 0 && range is null) return null;
        if (range is null) return WeatherText(icon, 16);
        if (icon.Length == 0) return WeatherText(range, 13);
        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        line.Children.Add(WeatherText(icon, 16, new Thickness(0, 0, 4, 0)));
        line.Children.Add(WeatherText(range, 13));
        return line;
    }

    private TextBlock WeatherText(string text, double fontSize, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = fontSize,
        Margin = margin ?? new Thickness(),
        VerticalAlignment = VerticalAlignment.Center,
        Effect = (Effect)FindResource("TextShadow")
    };

    private static string BuildWeatherTip(WeatherDay weather)
    {
        var label = WeatherCodes.Label(weather.Condition);
        var text = weather.HasTemperature
            ? Loc.Fmt(Loc.WeatherTooltipFormat, label, (int)Math.Round(weather.TempMin), (int)Math.Round(weather.TempMax))
            : label;
        if (weather.PrecipitationProbability is int probability)
            text = $"{text} · {Loc.Fmt(Loc.WeatherPrecipFormat, probability)}";
        return text;
    }

    /// <summary>节假日名只有中文界面会显示，所以按中文习惯截断。</summary>
    private static string ShortHolidayName(string? name)
    {
        var value = name ?? "";
        return value.Length <= 3 ? value : value[..3];
    }

    private static string BuildToolTip(DateTime date, HolidayEntry? holiday, WeatherDay? weather)
    {
        var dateText = date.ToString(Loc.DateFormatLong, Loc.CurrentCulture);
        var first = holiday is null || (!holiday.IsWorkday && !holiday.IsHoliday)
            ? dateText
            : $"{dateText} · {holiday.Name}{(holiday.IsWorkday ? Loc.SuffixWorkdayAdjust : Loc.SuffixHolidayOff)}";
        return weather is null ? first : $"{first}{Environment.NewLine}{BuildWeatherTip(weather)}";
    }

    private Brush FindBrush(string key) => (Brush)FindResource(key);

    private void PreviousButton_Click(object sender, RoutedEventArgs e) { displayedMonth = displayedMonth.AddMonths(-1); RenderCalendar(); }
    private void NextButton_Click(object sender, RoutedEventArgs e) { displayedMonth = displayedMonth.AddMonths(1); RenderCalendar(); }
    private void TodayButton_Click(object sender, RoutedEventArgs e) => GoToToday();

    private void GoToToday()
    {
        displayedMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RenderCalendar();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!settings.IsLocked && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var settingsItem = new MenuItem { Header = Loc.MenuSettings };
        settingsItem.Click += (_, _) => OpenSettings();
        var translateItem = new MenuItem { Header = Loc.MenuTranslate };
        translateItem.Click += (_, _) => OpenTranslate();
        var lockItem = new MenuItem { Header = settings.IsLocked ? Loc.MenuUnlock : Loc.MenuLock, IsCheckable = true, IsChecked = settings.IsLocked };
        lockItem.Click += (_, _) => { settings.IsLocked = lockItem.IsChecked; settingsService.Save(settings); ApplyLock(); };
        var clickItem = new MenuItem { Header = Loc.MenuClickThrough, IsCheckable = true, IsChecked = settings.IsClickThrough };
        clickItem.Click += (_, _) => { settings.IsClickThrough = clickItem.IsChecked; settingsService.Save(settings); ApplyClickThrough(); };
        var topItem = new MenuItem { Header = Loc.MenuTopmost, IsCheckable = true, IsChecked = Topmost };
        topItem.Click += (_, _) => { Topmost = topItem.IsChecked; settings.IsTopmost = Topmost; settingsService.Save(settings); };
        var visibilityItem = new MenuItem { Header = Loc.MenuHideWidget };
        visibilityItem.Click += (_, _) => ToggleWidgetVisibility();
        var updateItem = new MenuItem { Header = Loc.MenuCheckUpdate };
        updateItem.Click += (_, _) => CheckForUpdates();
        var exitItem = new MenuItem { Header = Loc.MenuExit };
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(settingsItem);
        menu.Items.Add(translateItem);
        menu.Items.Add(lockItem);
        menu.Items.Add(clickItem);
        menu.Items.Add(topItem);
        menu.Items.Add(visibilityItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(updateItem);
        menu.Items.Add(exitItem);
        menu.IsOpen = true;
    }

    /// <summary>翻译窗单例：已开着就提到前面，没有才新建。</summary>
    private void OpenTranslate()
    {
        if (translateWindow is null)
        {
            translateWindow = new TranslateWindow(settings, settingsService);
            translateWindow.Closed += (_, _) => translateWindow = null;
            translateWindow.Show();
            return;
        }
        translateWindow.Show();
        translateWindow.Activate();
    }

    /// <summary>交出翻译窗的所有权，让它不随本窗口关闭：切换语言重建主窗口时用。</summary>
    public TranslateWindow? DetachTranslateWindow()
    {
        var window = translateWindow;
        translateWindow = null;
        return window;
    }

    /// <summary>接管已有的翻译窗。配置引用由 Loaded 在读到配置后统一同步。</summary>
    public void AdoptTranslateWindow(TranslateWindow? window)
    {
        if (window is null) return;
        translateWindow = window;
        window.Closed += (_, _) => translateWindow = null;
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(settings, holidays, updateFlow, weatherService) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            // 下载与「保存本地数据」都会即时落盘，取消也要让内存跟上磁盘，否则要等重启才对得上。
            if (dialog.HolidaysPersisted)
            {
                holidays = dialog.Holidays;
                RenderCalendar();
            }
            // 「立即更新天气」用的是编辑中未保存的城市，共享的 WeatherService 已经切到那份数据。
            // 取消表示用户不认这次改动：按当前配置城市立刻重拉一次，别让界面停在没保存的城市上。
            if (dialog.WeatherChanged) StartWeather(forceInitial: true);
            return;
        }
        // 不换引用：UpdateFlow 与翻译窗都持有这个实例，整字段拷贝才能让它们读到新值，
        // 换成新对象的话，它们之后 Save 的就是旧对象，会把刚保存的设置覆盖回去。
        settings.ApplyEditedSettings(dialog.Settings);
        holidays = dialog.Holidays;
        settingsService.Save(settings);
        settingsService.SaveHolidays(holidays);
        if (dialog.RestartRequested)
        {
            ((App)Application.Current).RestartForNewLanguage(settings.Language);
            return;
        }
        ApplyLock();
        ApplyClickThrough();
        updateFlow?.Reschedule();
        // 天气配置可能变了，重挂定时器；有改动就立刻拉一次，避免等下一个周期。
        StartWeather(forceInitial: dialog.WeatherChanged);
        RenderCalendar();
    }

    /// <summary>刷新间隔归一：与设置窗口 NormalizeRefresh 同一口径，0 或负值按默认 60 分钟，不是 5 分钟。</summary>
    private static int NormalizedRefreshMinutes(int minutes) => minutes > 0 ? Math.Clamp(minutes, 5, 1440) : 60;

    /// <summary>跨午夜自动重绘：今日加粗、今日摘要与天气场景都按“当天”计算，挂件长开不能停在昨天。</summary>
    private void ScheduleNextMidnightRefresh()
    {
        midnightTimer?.Stop();
        var delay = DateTime.Today.AddDays(1).AddSeconds(2) - DateTime.Now;
        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
        midnightTimer = new DispatcherTimer { Interval = delay };
        midnightTimer.Tick += (_, _) =>
        {
            RenderCalendar();
            ScheduleNextMidnightRefresh();
        };
        midnightTimer.Start();
    }

    private void StartWeather(bool forceInitial = false)
    {
        weatherTimer?.Stop();
        weatherTimer = null;
        weatherService ??= new WeatherService(Path.Combine(settingsService.DataFolder, "weather.json"));
        if (!settings.ShowWeather)
        {
            weatherMap = new Dictionary<DateTime, WeatherDay>();
            return;
        }
        var interval = TimeSpan.FromMinutes(NormalizedRefreshMinutes(settings.WeatherRefreshMinutes));
        weatherTimer = new DispatcherTimer { Interval = interval };
        weatherTimer.Tick += (_, _) => _ = RefreshWeatherAsync(false);
        weatherTimer.Start();
        _ = RefreshWeatherAsync(forceInitial);
    }

    /// <summary>拉天气并重绘。<paramref name="force"/> 用于设置里的“立即更新”。</summary>
    private async Task RefreshWeatherAsync(bool force)
    {
        if (weatherService is null || !settings.ShowWeather) return;
        try
        {
            var maxAge = TimeSpan.FromMinutes(NormalizedRefreshMinutes(settings.WeatherRefreshMinutes));
            await weatherService.LoadAsync(settings.WeatherCity, maxAge, force);
            // Map 没变但标记为过期时，也要更新图例，避免把旧城市数据当成实时天气。
            if (IsLoaded && (!ReferenceEquals(weatherService.Map, renderedWeatherMap) ||
                weatherService.IsStale != renderedWeatherStale)) RenderCalendar();
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭导致的取消，忽略。
        }
        catch
        {
            // 免费接口或损坏缓存不应形成未观察任务异常；已有旧快照时仍然重绘。
            if (IsLoaded && weatherService.Map.Count > 0) RenderCalendar();
        }
    }

    private void ApplyClickThrough()
    {
        if (windowHandle == IntPtr.Zero) windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero) return;
        var style = ReadExtendedStyle(windowHandle);
        style |= WsExLayered;
        if (settings.IsClickThrough) style |= WsExTransparent;
        else style &= ~WsExTransparent;
        WriteExtendedStyle(windowHandle, style);
    }

    private static long ReadExtendedStyle(IntPtr handle) =>
        IntPtr.Size == 8 ? GetWindowLongPtr(handle, GwlExStyle).ToInt64() : GetWindowLong32(handle, GwlExStyle);

    private static void WriteExtendedStyle(IntPtr handle, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        else SetWindowLong32(handle, GwlExStyle, (int)style);
    }
}
