using DesktopCalendarWidget;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Linq;

/// <summary>
/// 主题样式的回归测试。
/// 这些样式很容易在改 XAML 时被悄悄换回 WPF 默认外观：隐式样式一旦写错或漏挂，
/// 控件不报错，只是变回系统蓝方块和直角边框，靠肉眼在截图里辨认成本很高。
/// 这里把控件真正实例化并测出视觉树上的结果，断言主题确实生效。
/// </summary>
internal static class ThemeSmoke
{
    internal static void Run(Action<string, Action> run, Application app)
    {
        run("buttons carry a focus state and a disabled state", () =>
        {
            var button = new Button { Style = (Style)Application.Current.Resources["ButtonBase"] };
            Realize(button);
            var bd = Find<Border>(button, "Bd");
            Program.Require(bd is not null, "the button template has no named border, so the states cannot be drawn");

            // 焦点走真实焦点系统，悬停/按下无法在测试里伪造（属性只读），那两项由下面
            // 「template declares hover and press triggers」单独断言。
            Program.Require(Settles(() => button.Focus(), bd!), "a focused button shows no focus ring");
            Program.Require(button.IsEnabled, "the button should start enabled");
            Program.Require(Settles(() => button.IsEnabled = false, button),
                "a disabled button stays looking enabled");
        });

        run("every button template declares hover and press triggers", () =>
        {
            // 隐式样式一旦被改写或漏掉，这两个触发器很容易连同模板一起丢掉，
            // 控件不会报错，只是变成一块没有反馈的死区。
            foreach (var key in new[] { "ButtonBase", "WidgetButton", "PrimaryButton", "DangerButton" })
            {
                var style = (Style)Application.Current.Resources[key];
                var text = System.Windows.Markup.XamlWriter.Save(style);
                Program.Require(text.Contains("IsMouseOver"), $"{key} declares no hover trigger");
                Program.Require(text.Contains("IsPressed"), $"{key} declares no press trigger");
            }
        });

        run("the widget button lightens on hover instead of darkening", () =>
        {
            // 挂件浮在壁纸之上，悬停时压一层半透明白等于「浮起来」；压黑会显脏。
            var button = new Button { Style = (Style)app.Resources["WidgetButton"] };
            Realize(button);
            var bd = Find<Border>(button, "Bd");
            var resting = ((SolidColorBrush)bd!.Background).Color;
            Program.Require(resting.A == 0, $"the widget button should start transparent, got alpha {resting.A}");
            // 悬停态没法在测试里真的把鼠标移上去（IsMouseOver 只读且带授权密钥），
            // 改为检查悬停触发器实际绑定的底色，确认它是半透明白而不是一层黑。
            var hoverSurface = (SolidColorBrush)app.Resources["WidgetHoverSurface"];
            Program.Require(hoverSurface.Color.A > 10 && hoverSurface.Color.R > 250,
                $"WidgetHoverSurface must be a translucent white, got #{hoverSurface.Color}");

            // 悬停时 WidgetButton 用的就是这支画刷：把触发器集合取出来比对。
            // Style 不是 DependencyObject，取不到它的 Template；从一个真正套用了该样式的
            // 按钮上把模板借出来，触发器集合才是同一个对象。
            var probe = new Button { Style = (Style)app.Resources["WidgetButton"] };
            Realize(probe);
            var hover = probe.Template.Triggers.OfType<Trigger>()
                .FirstOrDefault(t => t.Property == UIElement.IsMouseOverProperty);
            Program.Require(hover is not null, "the widget button declares no hover trigger");
            var applied = hover!.Setters.OfType<Setter>()
                .Where(s => (s.Property as DependencyProperty)?.Name == "Background")
                .Select(s => s.Value).OfType<SolidColorBrush>().FirstOrDefault();
            Program.Require(applied is not null, "hovering the widget button changes no background");
            Program.Require(applied!.Color == hoverSurface.Color,
                $"the widget button hover surface #{applied.Color} is not WidgetHoverSurface #{hoverSurface.Color}");
        });

        run("the primary button uses the accent brush rather than a hardcoded colour", () =>
        {
            // 保存按钮原先把强调色硬编码成 #B44837，跟 Accent 画刷脱钩，改主题时它不跟着变。
            var accent = (SolidColorBrush)app.Resources["Accent"];
            var button = new Button { Style = (Style)app.Resources["PrimaryButton"] };
            Realize(button);
            var bd = Find<Border>(button, "Bd");
            var actual = ((SolidColorBrush)bd!.Background).Color;
            Program.Require(actual == accent.Color,
                $"the primary button background #{actual} does not track Accent #{accent.Color}");
        });

        run("a checked check box draws a readable tick, not a dot", () =>
        {
            // 曾经把 9x7.5 的对勾塞进 17px 的方框，圆头笔画叠上去之后渲染成一个圆点。
            var check = new CheckBox();
            Realize(check);
            var tick = Descendants<System.Windows.Shapes.Path>(check).FirstOrDefault();
            Program.Require(tick is not null, "the check box template draws no tick path");
            Program.Require(tick!.Data.Bounds.Width >= 9,
                $"the tick is only {tick.Data.Bounds.Width}px wide, too thin to read as a tick");

            var box = Find<Border>(check, "Box");
            Program.Require(box is not null, "the check box template has no named box");
            Program.Require(((SolidColorBrush)box!.Background).Color.A == 0,
                "an unchecked box must stay transparent, not show a filled square");
            check.IsChecked = true;
            Program.Require(((SolidColorBrush)box.Background).Color == ((SolidColorBrush)app.Resources["Accent"]).Color,
                "a checked box must fill with the accent colour");
            Program.Require(tick.Visibility == Visibility.Visible, "a checked box must reveal its tick");
        });

        run("a checkable menu item draws a readable tick in its own column", () =>
        {
            // 菜单是代码 new 出来的，样式只能靠隐式查找命中，所以这一条也顺带验证了
            // 隐式样式对 new MenuItem() 有效。
            var item = new MenuItem { Header = "test", IsCheckable = true, IsChecked = true };
            Realize(item);
            var tick = Descendants<System.Windows.Shapes.Path>(item).FirstOrDefault();
            Program.Require(tick is not null, "the menu item fell back to the default template");
            Program.Require(tick!.Data.Bounds.Width >= 9,
                $"the menu tick is only {tick.Data.Bounds.Width}px wide, too thin to read");
            Program.Require(tick.Visibility == Visibility.Visible, "a checked menu item must reveal its tick");
            Program.Require(Descendants<Grid>(item).Any(g => g.ColumnDefinitions.Count >= 2),
                "the menu item has no fixed check column, so the headers will not line up");
        });

        run("the menu, its items and the separator are themed rather than default", () =>
        {
            // ContextMenu 是弹层根，不能直接塞进窗口内容树，宿主按钮一挂上就会弹出。
            var host = new Button { Content = "host", ContextMenu = new ContextMenu() };
            host.ContextMenu.Items.Add(new MenuItem { Header = "one" });
            host.ContextMenu.Items.Add(new Separator());
            Realize(host);
            host.ContextMenu.PlacementTarget = host;
            host.ContextMenu.IsOpen = true;
            host.UpdateLayout();
            Program.Require(host.ContextMenu.Template is not null, "the menu has no themed template");
            // 弹出后菜单的可视树才建好，圆角和投影都在那儿。
            Program.Require(Descendants<Border>(host.ContextMenu).Any(b => b.CornerRadius.TopLeft > 0),
                "the menu border is square, so the default styling is still in effect");
            Program.Require(Descendants<Border>(host.ContextMenu).Any(b => b.Effect is DropShadowEffect),
                "the menu has no shadow, so it will not separate from the desktop");
            host.ContextMenu.IsOpen = false;
        });

        run("the editor's save and delete buttons use the themed styles", () =>
        {
            var editor = new ScheduleEditorWindow([], null, new DateTime(2026, 9, 28));
            try
            {
                var save = (Button)editor.FindName("SaveButton")!;
                var delete = (Button)editor.FindName("DeleteButton")!;
                Program.Require(Equals(save.Style, editor.TryFindResource("PrimaryButton")),
                    "the save button is not using PrimaryButton");
                Program.Require(Equals(delete.Style, editor.TryFindResource("DangerButton")),
                    "the delete button is not using DangerButton");
            }
            finally
            {
                editor.Close();
            }
        });
    }

    /// <summary>
    /// 挂进一个可见窗口并跑一轮布局，控件的模板才会被套用、可视树才会建立。
    /// 只 new 一个 Button 出来，它的 VisualTree 是空的，上面那些断言全都无从谈起。
    /// </summary>
    private static void Realize(FrameworkElement element)
    {
        var host = new Window
        {
            Width = 400,
            Height = 300,
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Content = element
        };
        host.Show();
        host.UpdateLayout();
        element.UpdateLayout();
    }

    private static bool Settles(Action change, FrameworkElement element)
    {
        var bd = Find<Border>(element, "Bd") ?? element;
        var before = Snapshot(bd);
        change();
        element.UpdateLayout();
        return Snapshot(bd) != before;
    }

    private static string Snapshot(DependencyObject o) =>
        o is Border b
            ? $"{b.Background}|{b.BorderBrush}|{b.BorderThickness}|{(b.Effect?.ToString() ?? "-")}"
            : $"{((FrameworkElement)o).Opacity}|{((FrameworkElement)o).IsEnabled}";

    private static T? Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        Descendants<T>(root).FirstOrDefault(e => e.Name == name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is T hit) yield return hit;
            foreach (var sub in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return sub;
        }
    }
}
