using DesktopCalendarWidget;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

/// <summary>
/// 抽屉面板的行为测试。关闭按钮曾经只是个裸的「✕」字符，在半透明挂件上几乎看不见，
/// 等于没有关闭入口；这里断言它在展开态确实可见、也确实会发出关闭请求。
/// </summary>
internal static class PanelSmoke
{
    internal static void Run(Action<string, Action> run)
    {
        run("day panel shows a close button only when expanded", () =>
        {
            var panel = new DayAgendaPanel();
            var close = (Button)panel.FindName("CloseButton")!;
            var add = (Button)panel.FindName("AddButton")!;

            Program.Require(close.Visibility != Visibility.Visible, "close button must start hidden");
            panel.SetExpanded(true);
            Program.Require(close.Visibility == Visibility.Visible, "close button must appear when expanded");
            // 收起态没有关闭入口：它不是抽屉，按钮没有意义。
            panel.SetExpanded(false);
            Program.Require(close.Visibility != Visibility.Visible, "close button must hide again when collapsed");
            // 新增按钮两种形态都在。
            Program.Require(add.Visibility == Visibility.Visible, "add button is always available");
        });

        run("the close button stays inside the header at every drawer width", () =>
        {
            // 回归：标题和按钮曾是同一个 Grid 里叠放的两个子元素，日期一长就把右边的按钮
            // 挤出内容区被裁掉，关闭按钮直接消失。改成两列之后，按钮列必须始终在可视范围内。
            foreach (var width in new[] { 200.0, 300.0, 420.0 })
            {
                var panel = new DayAgendaPanel();
                panel.SetExpanded(true);
                // 塞一条很长的日期标题，模拟英文界面或长年份。
                panel.Bind(new DateTime(2026, 9, 9), [], null);
                Realize(panel, width);
                panel.UpdateLayout();

                var header = (FrameworkElement)panel.FindName("Header")!;
                var close = (Button)panel.FindName("CloseButton")!;
                var add = (Button)panel.FindName("AddButton")!;
                var host = (FrameworkElement)panel.FindName("Scroller")!;
                var right = host.ActualWidth > 0 ? host.ActualWidth : width;
                Program.Require(close.ActualWidth > 4,
                    $"close button collapsed to {close.ActualWidth} at width {width}");
                Program.Require(add.ActualWidth > 4,
                    $"add button collapsed to {add.ActualWidth} at width {width}");
                Program.Require(close.ActualWidth + add.ActualWidth + 6 <= right + 0.5,
                    $"buttons ({add.ActualWidth}+{close.ActualWidth}) overflow {right} at width {width}");
                Program.Require(header.ActualWidth <= right + 0.5,
                    $"header {header.ActualWidth} is wider than {right} at width {width}");
            }
        });

        run("clicking close raises the close request", () =>
        {
            var panel = new DayAgendaPanel();
            var closed = 0;
            panel.CloseRequested += () => closed++;
            panel.SetExpanded(true);
            ((Button)panel.FindName("CloseButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Require(closed == 1, $"close requested {closed} times");
        });

        run("clicking add raises an add request for the bound date", () =>
        {
            var panel = new DayAgendaPanel();
            var day = new DateTime(2026, 9, 28);
            DateTime? asked = null;
            panel.AddRequested += d => asked = d;
            panel.Bind(day, [], null);
            ((Button)panel.FindName("AddButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Require(asked == day, $"add asked for {asked}");
        });

        run("clicking a row raises an edit request for that schedule", () =>
        {
            var panel = new DayAgendaPanel();
            var item = new ScheduleItem
            {
                Kind = ScheduleKind.Meeting,
                Title = "Standup",
                Date = new DateTime(2026, 9, 28),
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(10, 0)
            };
            var day = item.Date!.Value;
            ScheduleItem? edited = null;
            panel.EditRequested += i => edited = i;
            panel.Bind(day, [item], null);

            var row = panel.FindName("ItemList") as ItemsControl;
            Program.Require(row?.Items.Count == 1, "row was not bound");
            Realize(panel);
            var container = row!.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            Program.Require(container is not null, "row container missing");
            // MouseLeftButtonUp 挂在 DataTemplate 的 Border 上，事件要从它自己身上发出。
            var card = FindDescendants<Border>(container!).FirstOrDefault();
            Program.Require(card is not null, "card border missing");
            // MouseLeftButtonUp 要 MouseButtonEventArgs，纯 RoutedEventArgs 会被拒。
            card!.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
            Program.Require(edited is not null && edited.Id == item.Id, "edit request did not carry the schedule");
        });

        run("rows render the localized kind label and its colour", () =>
        {
            var panel = new DayAgendaPanel();
            var day = new DateTime(2026, 9, 28);
            foreach (var kind in ScheduleKinds.All)
            {
                var item = new ScheduleItem
                {
                    Kind = kind,
                    Title = kind.ToString(),
                    Date = day,
                    StartTime = new TimeOnly(9, 0),
                    EndTime = new TimeOnly(10, 0)
                };
                panel.Bind(day, [item], null);
                Realize(panel);
                var row = (panel.FindName("ItemList") as ItemsControl)!.ItemContainerGenerator
                    .ContainerFromIndex(0) as FrameworkElement;
                Program.Require(row is not null, $"row missing for {kind}");
                var labels = FindTextBlocks(row!).ToList();
                Program.Require(labels.Any(t => t.Text == ScheduleKinds.Label(kind)),
                    $"kind label {ScheduleKinds.Label(kind)} missing for {kind} (saw {string.Join('|', labels.Select(t => t.Text))})");
                // 类型名要用该类型自己的颜色，不能是默认墨色。
                var brush = labels.First(t => t.Text == ScheduleKinds.Label(kind)).Foreground as System.Windows.Media.SolidColorBrush;
                Program.Require(brush is not null, $"kind label has no solid colour for {kind}");
                Program.Require(brush!.Color.ToString().ToUpperInvariant().Contains(ScheduleKinds.Color(kind).TrimStart('#')),
                    $"label colour {brush.Color} does not match {ScheduleKinds.Color(kind)}");
            }
        });
    }

    /// <summary>
    /// ItemsControl 不经过一次布局就不会生成容器，ContainerFromIndex 只会返回 null。
    /// 面板不在可视树里，所以这里手动走一遍 Measure/Arrange。
    /// </summary>
    private static void Realize(FrameworkElement element, double width = 1000)
    {
        element.Measure(new Size(width, 1000));
        element.Arrange(new Rect(0, 0, width, 1000));
        element.UpdateLayout();
    }

    /// <summary>
    /// 走可视树而不是逻辑树：ItemsControl 生成的容器里，DataTemplate 的内容挂在可视树上，
    /// LogicalTreeHelper 看不到它们。
    /// </summary>
    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var found in FindDescendants<T>(child)) yield return found;
        }
    }

    private static IEnumerable<TextBlock> FindTextBlocks(DependencyObject root)
    {
        if (root is TextBlock block) yield return block;
        foreach (var found in FindDescendants<TextBlock>(root)) yield return found;
    }
}
