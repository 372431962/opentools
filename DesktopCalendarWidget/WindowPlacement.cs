namespace DesktopCalendarWidget;

internal static class WindowPlacement
{
    internal static double ClampSize(double saved, double minimum, double screenExtent)
    {
        var available = Math.Max(1, screenExtent);
        var floor = Math.Min(minimum, available);
        return Math.Clamp(double.IsFinite(saved) ? saved : floor, floor, available);
    }

    internal static double ClampPosition(double saved, double screenOrigin, double screenExtent, double size, double fallbackOffset)
    {
        var value = double.IsFinite(saved) ? saved : screenOrigin + fallbackOffset;
        return Math.Clamp(value, screenOrigin, Math.Max(screenOrigin, screenOrigin + screenExtent - size));
    }
}
