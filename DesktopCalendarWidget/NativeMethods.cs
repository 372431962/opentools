using System.Runtime.InteropServices;

namespace DesktopCalendarWidget;

/// <summary>
/// 只放窗口摆位要用的那一条 Win32 调用。判断「窗口所在那块屏有多高」需要
/// GetMonitorInfo：SystemParameters.VirtualScreenHeight 给的是几块屏拼起来的总高，
/// 上下拼 1440 和 1080 时是 2520，拿它当上限会让窗口下半截掉到屏幕外面。
/// </summary>
internal static class NativeMethods
{
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>取离给定点最近那块显示器的工作区，也就是去掉任务栏后的可用区域。</summary>
    internal static bool TryGetWorkingArea(double x, double y, out ScreenRect area)
    {
        area = default;
        try
        {
            var point = new POINT { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
            var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            var work = info.rcWork;
            area = new ScreenRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
            return true;
        }
        catch (DllNotFoundException)
        {
            // 非 Windows 或裁剪过的运行时：拿不到就由调用方退回虚拟屏幕总高。
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
