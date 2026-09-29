namespace DesktopCalendarWidget;

/// <summary>
/// 窗口尺寸和位置的纯计算。抽出来是为了能脱离 WPF 窗口直接测：
/// 恢复多屏、矮屏、损坏配置这些分支，构造真实窗口验证起来既慢又不可靠。
/// </summary>
public static class WindowPlacement
{
    internal static double ClampSize(double saved, double minimum, double screenExtent)
    {
        var available = Math.Max(1, screenExtent);
        var floor = Math.Min(minimum, available);
        return Math.Clamp(double.IsFinite(saved) ? saved : floor, floor, available);
    }

    /// <summary>
    /// 窗口高度。存的值偏矮就往上补，补不到 desired 就保持原样。
    ///
    /// 日历格子和底部日程条抢的是同一段垂直空间：820px 时格子只剩 61px，节日名会被
    /// 挤出格子，摘要区也只能显示两条。940px 两边都宽裕。
    ///
    /// 只往上补、绝不缩矮：用户特意把小窗口拖小到桌面某个角落摆着，是他的选择，
    /// 不能因为默认值变大就把它撑回去。屏幕不够高时（老笔记本、小分辨率）
    /// 也补不动，此时保持原样，交给 ClampSize 夹住就行。
    /// </summary>
    internal static double PreferredHeight(double saved, double minimum, double screenExtent, double desired)
    {
        var current = ClampSize(saved, minimum, screenExtent);
        // 屏幕撑不到目标高度就别动，ClampSize 已经保证不会超出屏幕。
        if (desired > screenExtent) return current;
        return Math.Clamp(Math.Max(current, desired), Math.Min(minimum, Math.Max(1, screenExtent)), screenExtent);
    }

    internal static double ClampPosition(double saved, double screenOrigin, double screenExtent, double size, double fallbackOffset)
    {
        var value = double.IsFinite(saved) ? saved : screenOrigin + fallbackOffset;
        return Math.Clamp(value, screenOrigin, Math.Max(screenOrigin, screenOrigin + screenExtent - size));
    }
}

/// <summary>一块屏幕的工作区，字段与 System.Windows.Rect 一一对应。</summary>
internal readonly record struct ScreenRect(double Left, double Top, double Width, double Height);
