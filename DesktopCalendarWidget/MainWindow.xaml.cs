using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

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
    private DateTime displayedMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private HwndSource? windowSource;
    private IntPtr windowHandle;
    private bool clickThroughHotkeyAvailable;
    private TrayIcon? trayIcon;

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
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = PresentationSource.FromVisual(this) as HwndSource;
        windowSource?.AddHook(WndProc);
        windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero) return;
        clickThroughHotkeyAvailable = RegisterHotKey(windowHandle, HotkeyToggleClickThrough, ModControl | ModAlt | ModNoRepeat, VirtualKeyC);
        RegisterHotKey(windowHandle, HotkeyToggleLock, ModControl | ModAlt | ModNoRepeat, VirtualKeyL);
        trayIcon = new TrayIcon(windowHandle, BuildTrayToolTip(), Environment.ProcessPath);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        settings = settingsService.Load();
        holidays = settingsService.LoadHolidays();
        Left = ClampToVirtualScreen(settings.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenWidth, 80);
        Top = ClampToVirtualScreen(settings.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenHeight, 80);
        Opacity = Math.Clamp(settings.Opacity, 0.45, 1);

        if (settings.IsClickThrough && !clickThroughHotkeyAvailable)
        {
            settings.IsClickThrough = false;
            settingsService.Save(settings);
            MessageBox.Show(this, "快捷键 Ctrl+Alt+C 已被其他程序占用。为避免挂件无法操作，本次未启用鼠标穿透。", "桌面日历", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        ApplyClickThrough();
        RenderCalendar();
    }

    private static double ClampToVirtualScreen(double value, double origin, double extent, double fallback)
    {
        if (!double.IsFinite(value)) return fallback;
        var minimum = origin - 40;
        var maximum = origin + extent - 80;
        return maximum <= minimum ? fallback : Math.Clamp(value, minimum, maximum);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        settings.Left = Left;
        settings.Top = Top;
        settings.Opacity = Opacity;
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
        var text = $"桌面日历 · {today.ToString("yyyy年M月d日", CultureInfo.InvariantCulture)}";
        var lunar = LunarCalendarConverter.Format(today);
        if (!string.IsNullOrEmpty(lunar)) text += " " + lunar;
        if (settings.RestPattern != RestPattern.None)
            text += RestSchedule.IsSingleRestWeek(today, settings.AnchorWeekStart, settings.AnchorWeekIsSingleRest) ? " · 本周单休" : " · 本周双休";
        return text;
    }

    private void ShowTrayMenu()
    {
        // 先激活窗口，托盘菜单在点击别处时才会正常关闭
        if (windowHandle != IntPtr.Zero) SetForegroundWindow(windowHandle);

        var menu = new ContextMenu();
        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => OpenSettings();
        var visibilityItem = new MenuItem { Header = IsVisible ? "隐藏挂件" : "显示挂件" };
        visibilityItem.Click += (_, _) => ToggleWidgetVisibility();
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(settingsItem);
        menu.Items.Add(visibilityItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
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
        MonthTitle.Text = displayedMonth.ToString("yyyy年 M月", CultureInfo.InvariantCulture);
        MonthCaption.Text = LunarCalendarConverter.GetYearLabel(displayedMonth);
        TodaySummary.Text = $"今天 {DateTime.Today.ToString("yyyy年M月d日", CultureInfo.InvariantCulture)} · {LunarCalendarConverter.Format(DateTime.Today)}";
        Legend.Text = settings.RestPattern == RestPattern.None
            ? "红色：周末与节假日   橙色：调休上班   双击日期回到本月"
            : "红色：休息日与节假日   休：休息日   橙色：调休上班   双击日期回到本月";
        trayIcon?.SetToolTip(BuildTrayToolTip());
        CalendarGrid.Children.Clear();
        CalendarGrid.RowDefinitions.Clear();
        CalendarGrid.ColumnDefinitions.Clear();
        for (var column = 0; column < 7; column++) CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition());
        CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        for (var row = 0; row < 6; row++) CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        string[] weekNames = ["日", "一", "二", "三", "四", "五", "六"];
        for (var column = 0; column < 7; column++)
        {
            var header = new TextBlock
            {
                Text = weekNames[column],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = column is 0 or 6 ? FindBrush("Weekend") : FindBrush("MutedInk")
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, column);
            CalendarGrid.Children.Add(header);
        }

        var startOffset = (int)displayedMonth.DayOfWeek;
        var daysInMonth = DateTime.DaysInMonth(displayedMonth.Year, displayedMonth.Month);
        var holidayMap = holidays
            .Where(x => x.DateValue != DateTime.MinValue)
            .GroupBy(x => x.DateValue)
            .ToDictionary(x => x.Key, x => x.First());

        for (var cell = 0; cell < 42; cell++)
        {
            var dayNumber = cell - startOffset + 1;
            if (dayNumber < 1 || dayNumber > daysInMonth) continue;
            var date = displayedMonth.AddDays(dayNumber - 1);
            var dayButton = BuildDayButton(date, holidayMap.TryGetValue(date.Date, out var holiday) ? holiday : null);
            Grid.SetRow(dayButton, cell / 7 + 1);
            Grid.SetColumn(dayButton, cell % 7);
            CalendarGrid.Children.Add(dayButton);
        }
    }

    private Button BuildDayButton(DateTime date, HolidayEntry? holiday)
    {
        var isToday = date.Date == DateTime.Today;
        var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var usesRestSchedule = settings.RestPattern != RestPattern.None;
        var isRestDay = RestSchedule.IsRestDay(date, settings.RestPattern, settings.SingleRestDay, settings.AnchorWeekStart, settings.AnchorWeekIsSingleRest);
        var isWorkdayAdjustment = settings.ShowHolidays && holiday?.IsWorkday == true;
        var isHoliday = settings.ShowHolidays && holiday?.IsHoliday == true;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(CultureInfo.InvariantCulture),
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal
        });

        if (settings.ShowLunar)
        {
            stack.Children.Add(new TextBlock
            {
                Text = LunarCalendarConverter.Format(date),
                FontSize = 10,
                Foreground = FindBrush("MutedInk"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        var marker = "";
        var markerBrush = "Accent";
        if (isWorkdayAdjustment)
        {
            marker = "班";
            markerBrush = "Workday";
        }
        else if (isHoliday)
        {
            marker = ShortHolidayName(holiday?.Name);
        }
        else if (isRestDay)
        {
            marker = "休";
        }

        if (marker.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = marker,
                FontSize = 9,
                Foreground = FindBrush(markerBrush),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        var button = new Button
        {
            Content = stack,
            Margin = new Thickness(2),
            Padding = new Thickness(2),
            BorderThickness = new Thickness(isToday ? 1.5 : 0.5),
            BorderBrush = isToday ? FindBrush("Accent") : FindBrush("Border"),
            Background = isToday ? new SolidColorBrush(Color.FromRgb(241, 232, 221)) : Brushes.Transparent,
            ToolTip = BuildToolTip(date, holiday)
        };

        if (isWorkdayAdjustment) button.Foreground = FindBrush("Workday");
        else if (isHoliday || isRestDay || (!usesRestSchedule && isWeekend)) button.Foreground = FindBrush("Weekend");

        button.MouseDoubleClick += (_, _) => GoToToday();
        return button;
    }

    private static string ShortHolidayName(string? name)
    {
        var value = name ?? "";
        return value.Length <= 3 ? value : value[..3];
    }

    private static string BuildToolTip(DateTime date, HolidayEntry? holiday)
    {
        var dateText = date.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);
        if (holiday is null) return dateText;
        return $"{dateText} · {holiday.Name}{(holiday.IsWorkday ? "（调休上班）" : "（放假）")}";
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
        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => OpenSettings();
        var lockItem = new MenuItem { Header = settings.IsLocked ? "解锁位置" : "锁定位置", IsCheckable = true, IsChecked = settings.IsLocked };
        lockItem.Click += (_, _) => { settings.IsLocked = lockItem.IsChecked; settingsService.Save(settings); };
        var clickItem = new MenuItem { Header = "鼠标穿透（Ctrl+Alt+C）", IsCheckable = true, IsChecked = settings.IsClickThrough };
        clickItem.Click += (_, _) => { settings.IsClickThrough = clickItem.IsChecked; settingsService.Save(settings); ApplyClickThrough(); };
        var topItem = new MenuItem { Header = "窗口置顶", IsCheckable = true, IsChecked = Topmost };
        topItem.Click += (_, _) => { Topmost = topItem.IsChecked; };
        var visibilityItem = new MenuItem { Header = "隐藏挂件" };
        visibilityItem.Click += (_, _) => ToggleWidgetVisibility();
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(settingsItem);
        menu.Items.Add(lockItem);
        menu.Items.Add(clickItem);
        menu.Items.Add(topItem);
        menu.Items.Add(visibilityItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.IsOpen = true;
    }

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(settings, holidays) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        settings = dialog.Settings;
        holidays = dialog.Holidays;
        Opacity = settings.Opacity;
        settingsService.Save(settings);
        settingsService.SaveHolidays(holidays);
        ApplyClickThrough();
        RenderCalendar();
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
