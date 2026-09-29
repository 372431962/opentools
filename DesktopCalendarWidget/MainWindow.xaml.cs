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
    private List<ScheduleItem> scheduleItems = [];
    private HashSet<string> deliveredCourseReminders = [];
    /// <summary>节次时刻表快照，只在迁移旧课程时用一次。</summary>
    private IReadOnlyDictionary<int, string> periodTimes = new Dictionary<int, string>();
    /// <summary>
    /// 日程分桶。一次性条目按日期分桶，重复条目单独留着扫——课程就几行，
    /// 一次性事件才可能变多，两者不该用同一套索引。
    /// </summary>
    private AgendaIndex agendaIndex = AgendaIndex.Empty;
    /// <summary>抽屉当前展示的日期；null 表示抽屉收起，底部摘要展示 schedulePanelDate。</summary>
    private DateTime? drawerDate;
    /// <summary>底部摘要展示的日期，来自配置；null 表示跟随今天。</summary>
    private DateTime schedulePanelDate = DateTime.Today;
    private Guid? selectedScheduleId;
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
    private DispatcherTimer? courseReminderTimer;
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
        // 迁移旧课程要用节次时刻表换算成时刻，所以必须先交给 SettingsService 再读。
        settingsService.PeriodTimesForMigration = settings.PeriodTimes;
        scheduleItems = settingsService.LoadSchedules(out var droppedItems, out var pendingItems);
        reportScheduleLoad(droppedItems, pendingItems);
        schedulePanelDate = ReadPanelDate();
        WireDayPanels();
        deliveredCourseReminders = settingsService.LoadReminderKeys(DateTime.Today);
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
        weatherService.LoadFromCache(settings.WeatherCity, settings.WeatherLocation);
        RenderCalendar();
        StartWeather();
        ScheduleNextMidnightRefresh();
        StartCourseReminders();
        // 切语言重建窗口时翻译窗会被搬过来，它的配置引用要跟上新读到的配置对象，
        // 否则它关闭时会把旧配置写回去，覆盖掉刚保存的语言等设置。
        translateWindow?.UpdateSettings(settings);
        updateFlow = new UpdateFlow(settingsService, settings, this);
    }

    /// <summary>
    /// 迁移和坏数据都要让用户看见：迁移后的课程如果没配过节次时刻表，就是「时间待定」，
    /// 界面上不会编一个假时间出来，但也不能一声不响。
    /// </summary>
    private void reportScheduleLoad(int dropped, int pending)
    {
        var parts = new List<string>();
        if (pending > 0) parts.Add(Loc.Fmt(Loc.ScheduleTimePendingFormat, pending));
        if (dropped > 0) parts.Add(Loc.Fmt(Loc.SchedulesDroppedFormat, dropped));
        if (parts.Count == 0) return;
        MessageBox.Show(this, string.Join(Environment.NewLine, parts) + Environment.NewLine + Loc.DataFileHint,
            Loc.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private DateTime ReadPanelDate() =>
        DateTime.TryParseExact(settings.SchedulePanelDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date.Date : DateTime.Today;

    /// <summary>两个面板形态共用同一套回调：编辑、新增、关闭都交给主窗口统一落盘。</summary>
    private void WireDayPanels()
    {
        foreach (var panel in new[] { BottomPanel, DrawerPanel })
        {
            panel.EditRequested += EditScheduleItem;
            panel.AddRequested += _ => AddScheduleItem(drawerDate ?? schedulePanelDate);
        }
        BottomPanel.CloseRequested += () => CloseDrawer();
        DrawerPanel.CloseRequested += () => CloseDrawer();
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
        courseReminderTimer?.Stop();
        courseReminderTimer = null;
        weatherScene?.Stop();
        weatherScene = null;
        if (translateWindow is not null)
        {
            translateWindow.Close();
            translateWindow = null;
        }
        settings.Left = Left;
        settings.Top = Top;
        // 抽屉开着时存的是收起的宽度，否则下次启动挂件会凭空宽出一截。
        // 抽屉开着时窗口宽度里含抽屉那一段，配置里要存收起时的宽度。
        // 直接写 collapsedWidth 而不是「当前宽度减抽屉宽」：后者在用户开抽屉后又拖过窗口时
        // 会对不上，日历列钉的也是 collapsedWidth，两边得是同一个数。
        settings.Width = drawerDate is null || closingDrawer ? Width : collapsedWidth;
        settings.Height = Height;
        settings.IsTopmost = Topmost;
        settings.SchedulePanelDate = schedulePanelDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
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
        if (BuildTodaySchedulesText() is { Length: > 0 } coursesToday) parts.Add(coursesToday);
        if (SchedulesForDate(today).Count is var count && count > 0)
            parts.Add(Loc.Fmt(Loc.EventsDayFormat, count));
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
        // 鼠标穿透时单击收不到，菜单是抽屉唯一的入口。
        var dayItem = new MenuItem { Header = Loc.MenuShowDay };
        dayItem.Click += (_, _) => ShowDrawerFor(schedulePanelDate);
        var visibilityItem = new MenuItem { Header = IsVisible ? Loc.MenuHideWidget : Loc.MenuShowWidget };
        visibilityItem.Click += (_, _) => ToggleWidgetVisibility();
        var updateItem = new MenuItem { Header = Loc.MenuCheckUpdate };
        updateItem.Click += (_, _) => CheckForUpdates();
        var exitItem = new MenuItem { Header = Loc.MenuExit };
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(settingsItem);
        menu.Items.Add(translateItem);
        menu.Items.Add(dayItem);
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
        // 分桶先建好，后面 42 个格子就都是查表而不是全表扫描。
        agendaIndex = new AgendaIndex(scheduleItems);
        MonthTitle.Text = displayedMonth.ToString(Loc.MonthTitleFormat, Loc.CurrentCulture);
        MonthCaption.Text = BuildMonthCaption();
        var todayText = DateTime.Today.ToString(Loc.DateFormatLong, Loc.CurrentCulture);
        var todaySummary = settings.ShowLunarEffective
            ? $"{Loc.TodayPrefix} {todayText} · {LunarCalendarConverter.Format(DateTime.Today)}"
            : $"{Loc.TodayPrefix} {todayText}";
        if (BuildTodaySchedulesText() is { Length: > 0 } todayItems) todaySummary = $"{todaySummary} · {todayItems}";
        if (SchedulesForDate(DateTime.Today).Count is var eventCount && eventCount > 0)
            todaySummary = $"{todaySummary} · {Loc.Fmt(Loc.EventsDayFormat, eventCount)}";
        TodaySummary.Text = todaySummary;
        Legend.Text = BuildLegendText();
        BindPanels();
        
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

    private string BuildMonthCaption()
    {
        var parts = new List<string>();
        if (settings.ShowLunarEffective) parts.Add(LunarCalendarConverter.GetYearLabel(displayedMonth));
        // displayedMonth 恒为当月 1 号，直接拿它算周次会显示「第 1 号那周」，
        // 与用户实际所处的周差半个月。当月视图改用今天，翻看其它月份时用 1 号兜底。
        var weekAnchor = displayedMonth.Year == DateTime.Today.Year && displayedMonth.Month == DateTime.Today.Month
            ? DateTime.Today
            : displayedMonth;
        var week = settings.ShowSchedules
            ? Agenda.WeekIndex(weekAnchor, settings.SemesterStart, settings.SemesterWeeks)
            : null;
        if (week is int index) parts.Add(Loc.Fmt(Loc.CourseWeekFormat, index));
        return string.Join(" · ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
    private IReadOnlyList<ScheduleItem> SchedulesForDate(DateTime date) => settings.ShowSchedules
        ? agendaIndex.ItemsForDate(date, settings.SemesterStart, settings.SemesterWeeks)
        : [];

    /// <summary>今日摘要里那句「今天 N 条：xxx」，日程为空时返回 null。</summary>
    private string? BuildTodaySchedulesText()
    {
        var today = SchedulesForDate(DateTime.Today);
        if (today.Count == 0) return null;
        var names = string.Join(Loc.NameSeparator, today.Take(3).Select(x => x.Title));
        if (today.Count > 3) names = $"{names} +{today.Count - 3}";
        return Loc.Fmt(Loc.CourseTodayFormat, today.Count, names);
    }

    /// <summary>日期格里那条两门课 +「+N」的窄摘要。抽屉展开后不再需要它，日期格保持干净。</summary>
    private TextBlock? BuildCellScheduleLine(IReadOnlyList<ScheduleItem> items)
    {
        if (items.Count == 0) return null;
        static string Short(string name) => name.Length <= 5 ? name : name[..5];
        var names = string.Join(Loc.NameSeparatorCompact, items.Take(2).Select(x => Short(x.Title)));
        if (items.Count > 2) names = $"{names}+{items.Count - 2}";
        return new TextBlock
        {
            Text = names,
            FontSize = 10,
            MaxWidth = 90,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = Loc.Fmt(Loc.CourseTodayFormat, items.Count, string.Join(Loc.NameSeparator, items.Select(x => x.Title))),
            Effect = (Effect)FindResource("TextShadow")
        };
    }

    private string BuildSchedulesToolTip(IReadOnlyList<ScheduleItem> items)
    {
        if (items.Count == 0) return "";
        return string.Join(Environment.NewLine, items.Select(item =>
        {
            var time = Agenda.IsAllDay(item) ? Loc.EventAllDay
                : Agenda.IsTimePending(item) ? Loc.ScheduleTimePending
                : Agenda.TimeLabel(item);
            var line = $"{ScheduleKinds.Icon(item.Kind)} {ScheduleKinds.Label(item.Kind)} · {time} {item.Title}";
            if (!string.IsNullOrWhiteSpace(item.Location)) line += $" · {item.Location}";
            if (!string.IsNullOrWhiteSpace(item.Notes)) line += $" · {item.Notes}";
            return line;
        }));
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
            ? day.ConditionFor(DateTime.Today)
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
        var dayItems = SchedulesForDate(date);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var dateHeader = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        dateHeader.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(CultureInfo.InvariantCulture),
            FontSize = 22,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            Effect = (Effect)FindResource("TextShadow")
        });
        if (dayItems.Count > 0)
            dateHeader.Children.Add(new TextBlock { Text = $" ·{dayItems.Count}", FontSize = 11,
                Foreground = FindBrush("WidgetAccent"), VerticalAlignment = VerticalAlignment.Bottom });
        stack.Children.Add(dateHeader);
        if (weather is not null && BuildWeatherLine(weather, date) is { } weatherLine) stack.Children.Add(weatherLine);
        if (BuildCellScheduleLine(dayItems) is { } scheduleLine) stack.Children.Add(scheduleLine);
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
        var scheduleTip = BuildSchedulesToolTip(dayItems);
        if (scheduleTip.Length > 0) toolTipText = $"{toolTipText}{Environment.NewLine}{scheduleTip}";
        // 抽屉展开时要把当前这一天标出来：不然连点几天之后，远处的抽屉和哪一格对不上已经说不清了。
        var isSelected = selectedDate == date.Date;
        var button = new Button
        {
            Content = stack,
            Margin = new Thickness(3),
            Padding = new Thickness(3),
            BorderThickness = new Thickness(isToday || isSelected ? 1.5 : 0.75),
            BorderBrush = isSelected ? FindBrush("WidgetAccent")
                : isToday ? FindBrush("WidgetAccent") : FindBrush("Border"),
            Background = isSelected ? FindBrush("TodayBackground")
                : isToday ? FindBrush("TodayBackground") : FindBrush("CalendarCellBackground"),
            ToolTip = toolTipText
        };

        // 屏幕阅读器读 AutomationProperties.Name，ToolTip 不参与 UI Automation，必须单独给。
        AutomationProperties.SetName(button, toolTipText.Replace(Environment.NewLine, " "));

        if (isWorkdayAdjustment) button.Foreground = FindBrush("WidgetWorkday");
        else if (isRestDay) button.Foreground = FindBrush("WidgetWeekend");

        button.Click += (_, _) => ToggleDrawerFor(date);
        button.MouseDoubleClick += (_, _) => GoToToday();
        var dayMenu = new ContextMenu();
        var showDay = new MenuItem { Header = Loc.MenuShowDay };
        showDay.Click += (_, _) => ShowDrawerFor(date);
        var addItem = new MenuItem { Header = Loc.ScheduleAddButton };
        addItem.Click += (_, _) => AddScheduleItem(date);
        dayMenu.Items.Add(showDay);
        dayMenu.Items.Add(addItem);
        button.ContextMenu = dayMenu;
        return button;
    }

    /// <summary>
    /// 格子上的天气那一行：现象图标 + 最高/最低温度。图标比温度大一号，隔着几米也能认出天气；
    /// 两者都拿不到时返回 null，这一行直接不占高度。完整信息在悬停提示里。
    /// </summary>
    private UIElement? BuildWeatherLine(WeatherDay weather, DateTime date)
    {
        var icon = WeatherCodes.Icon(weather.ConditionFor(date));
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

    private static string BuildWeatherTip(WeatherDay weather, DateTime date)
    {
        var label = WeatherCodes.Label(weather.ConditionFor(date));
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
        return weather is null ? first : $"{first}{Environment.NewLine}{BuildWeatherTip(weather, date)}";
    }

    private Brush FindBrush(string key) => (Brush)FindResource(key);

    private void PreviousButton_Click(object sender, RoutedEventArgs e) { displayedMonth = displayedMonth.AddMonths(-1); RenderCalendar(); }
    private void NextButton_Click(object sender, RoutedEventArgs e) { displayedMonth = displayedMonth.AddMonths(1); RenderCalendar(); }
    private void TodayButton_Click(object sender, RoutedEventArgs e) => GoToToday();

    // ---- 日程抽屉 ----
    //
    // 几何：日历与抽屉并排，日历列在抽屉展开期间被钉在原宽度上，所以日历的位置和尺寸全程不变。
    //
    // 动画：窗口宽度只改一次，不逐帧改。逐帧改 Window.Width 等于每帧一次 HWND resize，
    // 分层窗口（AllowsTransparency=True）要整棵视觉树重排，在这台机器上肉眼可见地掉帧。
    // 真正的动画交给抽屉列宽，那是纯 WPF 布局，合成器能顺畅地推完。
    private const double DrawerAnimationMs = 130;
    private const double DrawerFrameMs = 8;

    /// <summary>抽屉宽度来自配置并夹在能放下内容的区间里；太窄的抽屉不如不展开。</summary>
    private double DrawerWidth =>
        double.IsFinite(settings.SchedulePanelWidth) ? Math.Clamp(settings.SchedulePanelWidth, 200, 420) : 300;

    /// <summary>右侧空间不够时抽屉改到日历左边，并把窗口往左推。</summary>
    private bool drawerOnLeft;

    /// <summary>展开前的窗口宽度和位置，动画结束要用它们把窗口还原。</summary>
    private double collapsedWidth;
    private double collapsedLeft;
    /// <summary>关闭动画进行中。此时窗口还是宽的，再点日期必须从收起宽度算起，否则会再宽一格。</summary>
    private bool closingDrawer;
    private DateTime? selectedDate;
    private DrawerPlacement drawerPlacement;

    private void ToggleDrawerFor(DateTime date)
    {
        if (drawerDate == date.Date) CloseDrawer();
        else ShowDrawerFor(date);
    }

    private void ShowDrawerFor(DateTime date)
    {
        // 抽屉已经开着时只换日期，绝不重跑几何。旧逻辑每次都拿「当前宽度 + 抽屉宽」当目标，
        // 连点几天窗口就会一格一格变宽，日历也被一起撑大。
        if (drawerDate is not null)
        {
            drawerDate = date.Date;
            selectedDate = date.Date;
            selectedScheduleId = null;
            drawerPlacement = DrawerGeometry.SwitchDay(drawerPlacement);
            BindPanels();
            RenderCalendar();
            return;
        }
        drawerDate = date.Date;
        selectedDate = date.Date;
        selectedScheduleId = null;
        // 关闭动画还没跑完就又点日期的话，窗口此刻仍带着抽屉的宽度。
        // 那时必须沿用收起的几何，否则「当前宽度 + 抽屉宽」会把窗口一次次叠宽。
        collapsedWidth = closingDrawer ? collapsedWidth : Width;
        collapsedLeft = closingDrawer ? collapsedLeft : Left;
        closingDrawer = false;
        drawerPlacement = DrawerGeometry.Open(collapsedLeft, collapsedWidth,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth, DrawerWidth);
        drawerOnLeft = drawerPlacement.OnLeft;

        // 窗口一次改到位：日历列被钉住，多出来的宽度暂时空着，接着由列宽动画把抽屉填进去。
        Left = drawerPlacement.Left;
        Width = drawerPlacement.Width;
        SetDrawerSide(DrawerWidth);
        Drawer.Visibility = Visibility.Visible;
        DrawerPanel.SetExpanded(true);
        BindPanels();
        AnimateDrawerColumn(0, DrawerWidth);
    }

    private void CloseDrawer()
    {
        if (drawerDate is null) return;
        drawerDate = null;
        selectedDate = null;
        closingDrawer = true;
        DrawerPanel.SetExpanded(false);
        AnimateDrawerColumn(DrawerWidth, 0, () =>
        {
            Drawer.Visibility = Visibility.Collapsed;
            SetDrawerSide(0);
            // 列已经收干净、日历也没动过，这时把窗口缩回去不会看到中间的裁切。
            var closed = DrawerGeometry.Close(drawerPlacement, collapsedWidth);
            Left = closed.Left;
            Width = collapsedWidth;
            closingDrawer = false;
        });
        BindPanels();
        RenderCalendar();
    }

    /// <summary>日历所在的那一列：抽屉翻边时两者对调。</summary>
    private int CalendarColumnIndex => drawerOnLeft ? 1 : 0;

    /// <summary>抽屉当前占着的那一列，列宽动画动的就是它。</summary>
    private int DrawerColumnIndex => drawerOnLeft ? 0 : 1;

    /// <summary>
    /// 把日历和抽屉摆到对应的列，并按列号分配宽度。
    /// 宽度必须跟着列号走而不是跟着 CalendarColumn 这个名字走：翻边后抽屉落在第 0 列，
    /// 若仍把日历宽度补给第 0 列，日历就会被挤进抽屉那 300px 里。
    /// </summary>
    private void SetDrawerSide(double drawerWidth)
    {
        Grid.SetColumn(Drawer, DrawerColumnIndex);
        Grid.SetColumn(CalendarHost, CalendarColumnIndex);
        Drawer.SetValue(Border.BorderThicknessProperty,
            drawerOnLeft ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0));
        // 日历钉在原宽度上才不会跟着变宽；抽屉收起时日历恢复星号、独占整个窗口。
        SplitGrid.ColumnDefinitions[CalendarColumnIndex].Width = drawerWidth <= 0
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(collapsedWidth);
        SplitGrid.ColumnDefinitions[DrawerColumnIndex].Width = new GridLength(drawerWidth);
    }

    /// <summary>
    /// 列宽动画：8ms 一帧、130ms 收尾，ease-out 起步快收尾慢。
    /// 只动 GridLength，不碰 Window 几何，所以不触发 HWND 调整大小，也没有整窗重新布局的代价。
    /// </summary>
    private void AnimateDrawerColumn(double from, double to, Action? onCompleted = null)
    {
        drawerAnimation?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DrawerFrameMs) };
        var elapsed = 0.0;
        timer.Tick += (_, _) =>
        {
            elapsed += timer.Interval.TotalMilliseconds;
            var t = Math.Clamp(elapsed / DrawerAnimationMs, 0, 1);
            SplitGrid.ColumnDefinitions[DrawerColumnIndex].Width =
                new GridLength(from + (to - from) * (1 - Math.Pow(1 - t, 3)));
            if (t < 1) return;
            timer.Stop();
            drawerAnimation = null;
            SplitGrid.ColumnDefinitions[DrawerColumnIndex].Width = new GridLength(to);
            onCompleted?.Invoke();
        };
        drawerAnimation = timer;
        timer.Start();
    }

    private DispatcherTimer? drawerAnimation;

    private void BindPanels()
    {
        var date = drawerDate ?? schedulePanelDate;
        var items = SchedulesForDate(date);
        // 底部摘要和抽屉展示同一天：抽屉是右侧的完整视图，底部那条是随手一瞥的入口，两者并存。
        BottomPanelHost.Visibility = settings.ShowSchedules ? Visibility.Visible : Visibility.Collapsed;
        BottomPanel.SetExpanded(false);
        BottomPanel.Bind(date, items, selectedScheduleId);
        if (drawerDate is not null) DrawerPanel.Bind(date, items, selectedScheduleId);
    }

    /// <summary>新增或编辑完都要落盘再重绘；写失败要明说，否则界面和数据会对不上。</summary>
    private void ApplyScheduleEdit(List<ScheduleItem> updated, string failureText)
    {
        if (!settingsService.SaveSchedules(updated))
        {
            MessageBox.Show(this, failureText, Loc.AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        scheduleItems = updated;
        agendaIndex = new AgendaIndex(scheduleItems);
        settings.SchedulePanelDate = schedulePanelDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        settingsService.Save(settings);
        BindPanels();
        RenderCalendar();
    }

    private void AddScheduleItem(DateTime date)
    {
        var editor = new ScheduleEditorWindow(scheduleItems, null, date) { Owner = this };
        if (editor.ShowDialog() != true) return;
        ApplyScheduleEdit(editor.Items, Loc.ScheduleSaveFailed);
    }

    private void EditScheduleItem(ScheduleItem item)
    {
        var editor = new ScheduleEditorWindow(scheduleItems, item, item.Date ?? (drawerDate ?? schedulePanelDate)) { Owner = this };
        if (editor.ShowDialog() != true) return;
        selectedScheduleId = item.Id;
        ApplyScheduleEdit(editor.Items, Loc.ScheduleSaveFailed);
    }

    private void GoToToday()
    {
        displayedMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RenderCalendar();
    }

    /// <summary>抽屉开着时 Esc 收起。除了点「✕」和再点同一天之外的第三条退路。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || drawerDate is null) return;
        CloseDrawer();
        e.Handled = true;
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
        // 鼠标穿透时单击收不到，菜单是抽屉唯一的入口。
        var dayItem = new MenuItem { Header = Loc.MenuShowDay };
        dayItem.Click += (_, _) => ShowDrawerFor(schedulePanelDate);
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
        menu.Items.Add(dayItem);
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
        var dialog = new SettingsWindow(settings, holidays, updateFlow) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            // 「保存本地数据」会即时落盘，取消也要让内存跟上磁盘，否则要等重启才对得上。
            if (dialog.HolidaysPersisted)
            {
                holidays = dialog.Holidays;
                RenderCalendar();
            }
            if (dialog.SchedulesPersisted)
            {
                scheduleItems = dialog.Schedules;
                agendaIndex = new AgendaIndex(scheduleItems);
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
        scheduleItems = dialog.Schedules;
        agendaIndex = new AgendaIndex(scheduleItems);
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
        StartCourseReminders();
        // 抽屉开着时宽度跟着新配置走，锁定位也允许自由缩放。
        if (drawerDate is not null) ApplyDrawerWidth();
        RenderCalendar();
    }

    /// <summary>设置里改了抽屉宽度：只重算列宽，不重播开合动画，也不碰窗口几何。</summary>
    private void ApplyDrawerWidth()
    {
        if (drawerDate is not null)
        {
            drawerAnimation?.Stop();
            drawerAnimation = null;
            SetDrawerSide(DrawerWidth);
            Width = collapsedWidth + DrawerWidth;
            drawerPlacement = drawerPlacement with { Width = Width };
        }
        else
        {
            drawerPlacement = DrawerGeometry.Open(Left, Width,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth, DrawerWidth);
            drawerOnLeft = drawerPlacement.OnLeft;
            SetDrawerSide(0);
            Left = drawerPlacement.Left;
            Width = drawerPlacement.Width;
            Drawer.Visibility = Visibility.Collapsed;
        }
        BindPanels();
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

    private void StartCourseReminders()
    {
        courseReminderTimer?.Stop();
        courseReminderTimer = null;
        // 没有课程时不挂 30 秒的空转定时器：省电，也避免每个 tick 都走一遍推算。
        if (!settings.CourseRemindersEnabled || settings.SemesterStart is null ||
            !scheduleItems.Any(x => x.Kind == ScheduleKind.Course)) return;
        courseReminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        courseReminderTimer.Tick += (_, _) => CheckCourseReminders();
        courseReminderTimer.Start();
        CheckCourseReminders();
    }

    private void CheckCourseReminders()
    {
        if (trayIcon is null || !settings.CourseRemindersEnabled) return;
        var now = DateTime.Now;
        var due = ScheduleReminders.Due(now, agendaIndex, settings.SemesterStart, settings.SemesterWeeks,
            settings.CourseReminderMinutes, deliveredCourseReminders);
        foreach (var reminder in due)
        {
            var room = string.IsNullOrWhiteSpace(reminder.Location) ? "" : $" · {reminder.Location}";
            var message = Loc.Fmt(Loc.ReminderBodyFormat, reminder.Title,
                reminder.StartsAt.ToString("HH:mm", CultureInfo.InvariantCulture), room);
            if (!trayIcon.ShowBalloon(Loc.SectionCourses, message)) continue;
            deliveredCourseReminders.Add(reminder.Key);
            settingsService.SaveReminderKeys(deliveredCourseReminders, now.Date);
        }
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
            await weatherService.LoadAsync(settings.WeatherCity, settings.WeatherLocation, maxAge, force);
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
