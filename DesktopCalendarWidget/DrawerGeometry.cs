namespace DesktopCalendarWidget;

/// <summary>
/// 抽屉展开后的窗口落位。纯计算，不碰 WPF：往哪边长、宽度变成多少、收起后回哪，
/// 都在这里算完，MainWindow 只负责把结果插值过去。
/// </summary>
internal readonly record struct DrawerPlacement(double Left, double Width, bool OnLeft);

internal static class DrawerGeometry
{
    /// <summary>右侧空间够就往右长，窗口位置一点不动；不够才翻到左边并把窗口左移。</summary>
    public static DrawerPlacement Open(double left, double width, double screenLeft, double screenWidth, double drawerWidth)
    {
        var rightRoom = screenLeft + screenWidth - (left + width);
        // 翻边的前提是左边还放得下，并且挪完之后屏幕上至少还留得下一半日历。
        var onLeft = rightRoom < drawerWidth && left - drawerWidth >= screenLeft + width / 2;
        return new DrawerPlacement(onLeft ? left - drawerWidth : left, width + drawerWidth, onLeft);
    }

    /// <summary>收起：宽度退回原值，翻过边的话位置也退回去。</summary>
    public static DrawerPlacement Close(DrawerPlacement opened, double collapsedWidth) =>
        new(opened.OnLeft ? opened.Left + (opened.Width - collapsedWidth) : opened.Left, collapsedWidth, false);
}
