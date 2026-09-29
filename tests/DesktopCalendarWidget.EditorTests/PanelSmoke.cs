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
    private static void Realize(FrameworkElement element)
    {
        element.Measure(new Size(1000, 1000));
        element.Arrange(new Rect(0, 0, 1000, 1000));
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
