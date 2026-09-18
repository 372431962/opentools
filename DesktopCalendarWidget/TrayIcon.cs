using System.IO;
using System.Runtime.InteropServices;

namespace DesktopCalendarWidget;

public enum TrayEvent
{
    None,
    ContextMenu,
    DoubleClick
}

/// <summary>
/// 系统托盘图标。直接用 Shell_NotifyIcon，不引入 WinForms 或第三方包。
/// 图标从程序自身的 exe 中提取，因此单文件发布同样有效。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = 0x8001; // WM_APP + 1

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int IDI_APPLICATION = 32512;
    private const int IconId = 1;
    private const int MaxTipLength = 127;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    private readonly IntPtr windowHandle;
    private IntPtr iconHandle;
    private bool iconOwned;
    private bool registered;
    private string toolTip = "";

    /// <summary>资源管理器重启后系统会广播该消息，收到后需要重新注册图标。</summary>
    public static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    public bool IsRegistered => registered;
    public string ToolTip => toolTip;
    /// <summary>true 表示图标是从程序自身 exe 提取的；false 表示回退到系统默认图标。</summary>
    public bool UsingApplicationIcon => iconOwned;


    public TrayIcon(IntPtr ownerHandle, string initialToolTip, string? iconSourcePath)
    {
        windowHandle = ownerHandle;
        (iconHandle, iconOwned) = LoadIconHandle(iconSourcePath);
        registered = Send(initialToolTip, NIM_ADD);
    }

    /// <summary>更新提示文本；必要时自动完成首次注册或重新注册。</summary>
    public bool SetToolTip(string text)
    {
        if (registered && text == toolTip) return true;
        var message = registered ? NIM_MODIFY : NIM_ADD;
        var ok = Send(text, message);
        if (ok) registered = true;
        return ok;
    }

    /// <summary>资源管理器重启后重新注册（先尝试修改，失败再新增）。</summary>
    public bool ReRegister(string text)
    {
        if (registered)
        {
            var data = BuildData(text);
            if (Shell_NotifyIcon(NIM_MODIFY, ref data))
            {
                toolTip = text;
                return true;
            }
            registered = false;
        }
        registered = Send(text, NIM_ADD);
        return registered;
    }

    public void Dispose()
    {
        if (registered)
        {
            var data = new NotifyIconData
            {
                cbSize = Marshal.SizeOf<NotifyIconData>(),
                hWnd = windowHandle,
                uID = IconId,
                szTip = "",
                szInfo = "",
                szInfoTitle = ""
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            registered = false;
        }

        if (iconOwned && iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
        iconHandle = IntPtr.Zero;
        iconOwned = false;
    }

    /// <summary>把托盘回调消息翻译成具体事件。</summary>
    public static TrayEvent ParseCallback(int message, IntPtr lParam)
    {
        if (message != CallbackMessage) return TrayEvent.None;
        return (lParam.ToInt64() & 0xFFFF) switch
        {
            WM_LBUTTONDBLCLK => TrayEvent.DoubleClick,
            WM_RBUTTONUP or WM_CONTEXTMENU => TrayEvent.ContextMenu,
            _ => TrayEvent.None
        };
    }

    private bool Send(string text, int message)
    {
        var data = BuildData(text);
        var ok = Shell_NotifyIcon(message, ref data);
        if (ok) toolTip = text;
        return ok;
    }

    private NotifyIconData BuildData(string text)
    {
        var tip = text ?? "";
        if (tip.Length > MaxTipLength) tip = tip[..MaxTipLength];
        return new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = windowHandle,
            uID = IconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = iconHandle,
            szTip = tip,
            szInfo = "",
            szInfoTitle = "",
            guidItem = Guid.Empty
        };
    }

    private static (IntPtr Handle, bool Owned) LoadIconHandle(string? iconSourcePath)
    {
        if (!string.IsNullOrWhiteSpace(iconSourcePath) && File.Exists(iconSourcePath))
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            var count = ExtractIconEx(iconSourcePath, 0, large, small, 1);
            if (count > 0)
            {
                var chosen = small[0] != IntPtr.Zero ? small[0] : large[0];
                if (small[0] != IntPtr.Zero && large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (chosen != IntPtr.Zero) return (chosen, true);
            }
        }
        return (LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION)), false);
    }
}
