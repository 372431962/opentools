using Microsoft.Win32;

namespace DesktopCalendarWidget;

public static class StartupService
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "DesktopCalendarWidget";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        // 单文件发布时 Assembly.Location 为空，Environment.ProcessPath 才是真实 exe 路径。
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path)) key.SetValue(ValueName, $"\"{path}\"");
    }
}
