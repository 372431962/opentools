using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;

/// <summary>
/// 日期格的容量测试。
///
/// 格子 640 宽、6 行高，里面要塞日期号、天气、课程摘要、农历、节日名五层内容。
/// 五层全开时这些内容加起来比格子本身还高，节日名会被挤出格子、和下一行叠在一起，
/// 表现为「节日展示时单格显示不全」。这类溢出在截图上很容易和别的问题混淆，
/// 所以这里直接按 MainWindow.xaml.cs 里的字号复刻一份内容栈，量它装不装得下。
/// 改了字号或底部区域高度，数字会跟着变，测试会如实报告。
/// </summary>
internal static class CalendarCellFit
{
    // 与 MainWindow.xaml 保持一致：820 高的窗口减去外框、页头、翻月行、
    // 底部日程条和图例，剩下的才是日历网格。
    private const double WidgetHeight = 820;
    private const double Chrome = 52;
    private const double Header = 78;
    private const double NavRow = 60;
    private const double BottomStrip = 112;
    private const double Legend = 34;
    private const double WeekdayHeaderRow = 32;
    private const double CellChrome = 8;      // 格子自带的 Margin 3 + Padding 3 上下
    private const double CellContentWidth = 76;

    // 与 MainWindow.xaml.cs 里的 BuildDayButton 一致。
    private const double DayNumberSize = 17;
    private const double CountBadgeSize = 10;
    private const double WeatherIconSize = 11;
    private const double WeatherRangeSize = 10;
    private const double CourseSummarySize = 9;
    private const double SubLineSize = 12;
    private const double RowGap = 1;      // BuildDayButton 给除日期号外每层的上边距

    internal static void Run(Action<string, Action> run)
    {
        var gridHeight = WidgetHeight - Chrome - Header - NavRow - BottomStrip - Legend;
        var cellHeight = (gridHeight - WeekdayHeaderRow) / 6.0;
        var inner = cellHeight - CellChrome;

        run("a day cell fits its worst case without overflowing", () =>
        {
            // 最坏的一格：有课程、天气、节日名，三样全开。
            var height = Measure(hasWeather: true, hasCourses: true, hasSubLine: true);
            Program.Require(height <= inner,
                $"the tallest cell needs {height:F1}px but only {inner:F1}px is available; " +
                "the holiday name will spill into the row below");
        });

        run("every combination of cell content fits", () =>
        {
            // 内容是按优先级逐层加上去的，但开关组合由用户决定，任意两层都可能同时出现。
            foreach (var weather in new[] { false, true })
            foreach (var courses in new[] { false, true })
            foreach (var subLine in new[] { false, true })
            {
                var height = Measure(weather, courses, subLine);
                var what = $"weather={weather} courses={courses} subline={subLine}";
                Program.Require(height <= inner,
                    $"{what} needs {height:F1}px but only {inner:F1}px is available");
            }
        });

        run("the holiday name and the lunar text share one line", () =>
        {
            // 两者都是补充信息，各占一行的话最坏情况要多出 12px，格子装不下。
            // 节日优先：用户是特意打开节日开关的，占了位置就不再看农历。
            var withHoliday = Measure(hasWeather: true, hasCourses: true, hasSubLine: true);
            var bothSeparately = MeasureWithSeparateLunarAndHoliday();
            Program.Require(withHoliday < bothSeparately,
                "the holiday and lunar lines appear to be on separate rows again");
        });

        run("an empty cell still centres its day number", () =>
        {
            // 最空的一格也要看着是居中的，不能因为内容少就贴顶或贴底。
            var height = Measure(hasWeather: false, hasCourses: false, hasSubLine: false);
            Program.Require(height > 10 && height < inner,
                $"a plain cell should be comfortably shorter than the row, got {height:F1}px against {inner:F1}px");
        });
    }

    private static double Measure(bool hasWeather, bool hasCourses, bool hasSubLine) =>
        Build(hasWeather, hasCourses, hasSubLine, separateLunarAndHoliday: false);

    private static double MeasureWithSeparateLunarAndHoliday() =>
        Build(true, true, true, separateLunarAndHoliday: true);

    private static double Build(bool hasWeather, bool hasCourses, bool hasSubLine, bool separateLunarAndHoliday)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

        var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        head.Children.Add(new TextBlock { Text = "28", FontSize = DayNumberSize, FontWeight = FontWeights.Bold });
        if (hasCourses)
        {
            head.Children.Add(new TextBlock
            {
                Text = " \u00b73",
                FontSize = CountBadgeSize,
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }
        stack.Children.Add(head);

        if (hasWeather)
        {
            var weather = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, RowGap, 0, 0) };
            weather.Children.Add(new TextBlock { Text = "\u2602", FontSize = WeatherIconSize, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
            weather.Children.Add(new TextBlock { Text = "25\u00b0", FontSize = WeatherRangeSize, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(weather);
        }

        if (hasCourses)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "\u8bed\u6587\u8bfe\u00b7\u6570\u5b66\u8bfe+1",
                FontSize = CourseSummarySize,
                MaxWidth = 72,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        if (separateLunarAndHoliday)
        {
            // 只用来量「两行各占一行」会有多高，不出现在真实布局里。
            stack.Children.Add(new TextBlock { Text = "\u56fd\u5e86\u8282", FontSize = SubLineSize, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = "\u5341\u4e5d", FontSize = SubLineSize, HorizontalAlignment = HorizontalAlignment.Center });
        }
        else if (hasSubLine)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "\u56fd\u5e86\u8282",
                FontSize = SubLineSize,
                MaxWidth = 72,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        var host = new Border { Child = stack, Width = CellContentWidth, Padding = new Thickness(3) };
        var window = new Window
        {
            Width = 400,
            Height = 400,
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Content = host
        };
        window.Show();
        // 只 new 不布局的话 DesiredSize 是 0，量出来的一切都毫无意义。
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        host.UpdateLayout();
        var height = stack.DesiredSize.Height;
        window.Close();
        return height;
    }
}
