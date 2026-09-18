using System.Runtime.InteropServices;

namespace DesktopCalendarWidget;

/// <summary>
/// 单实例互斥与“显示挂件”广播消息。
/// 互斥锁按登录会话（Local\）命名，因此同一用户不会启动第二份，其他 Windows 用户仍可各自运行。
/// </summary>
public static class SingleInstance
{
    public const string MutexName = @"Local\DesktopCalendarWidget.SingleInstance";

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>已有实例监听的消息；收到后把桌面挂件显示到最前。</summary>
    public static readonly int ShowWidgetMessage = RegisterWindowMessage("DesktopCalendarWidget.ShowWidget");

    /// <summary>广播给已有实例，请它显示挂件。返回是否成功投递。</summary>
    public static bool NotifyExistingInstance() =>
        ShowWidgetMessage != 0 && PostMessage(HwndBroadcast, ShowWidgetMessage, IntPtr.Zero, IntPtr.Zero);

    /// <summary>该窗口消息是否为本程序的“显示挂件”广播。</summary>
    public static bool IsShowWidgetMessage(int message) =>
        message != 0 && message == ShowWidgetMessage;
}
