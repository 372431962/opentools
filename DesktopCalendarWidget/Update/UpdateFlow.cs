using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopCalendarWidget.Update;

/// <summary>
/// 更新流程的界面侧：启动后延迟首查、之后每 12 小时复查，发现新版弹提示，
/// 用户同意后带进度下载安装包，再退出本程序并启动安装向导。
/// 检查与下载都在 UI 线程的异步等待里完成，不阻塞日历绘制。
/// </summary>
public sealed class UpdateFlow : IDisposable
{
    /// <summary>启动后延迟首查，避开拉天气与节假日的那段时间。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);

    private readonly SettingsService settingsService;
    private readonly WidgetSettings settings;
    private readonly Window owner;
    private readonly UpdateService service = new();
    private readonly string updatesFolder;

    private DispatcherTimer? startupTimer;
    private DispatcherTimer? intervalTimer;
    private bool checking;

    public UpdateFlow(SettingsService settingsService, WidgetSettings settings, Window owner)
    {
        this.settingsService = settingsService;
        this.settings = settings;
        this.owner = owner;
        updatesFolder = Path.Combine(settingsService.DataFolder, "updates");
        Reschedule();
    }

    /// <summary>按当前配置重建定时器；设置里改过自动检查开关后调用。</summary>
    public void Reschedule()
    {
        StopTimers();
        if (!settings.AutoCheckUpdates) return;
        startupTimer = new DispatcherTimer { Interval = StartupDelay };
        startupTimer.Tick += (_, _) =>
        {
            startupTimer?.Stop();
            _ = CheckInBackgroundAsync();
            intervalTimer = new DispatcherTimer { Interval = UpdateService.CheckInterval };
            intervalTimer.Tick += (_, _) => _ = CheckInBackgroundAsync();
            intervalTimer.Start();
        };
        startupTimer.Start();
    }

    public void Dispose() => StopTimers();

    private void StopTimers()
    {
        startupTimer?.Stop();
        startupTimer = null;
        intervalTimer?.Stop();
        intervalTimer = null;
    }

    /// <summary>后台自动检查：断网、接口变动、用户已选择不再提示都不出声。</summary>
    private async Task CheckInBackgroundAsync()
    {
        if (checking || !settings.AutoCheckUpdates) return;
        if (settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < UpdateService.CheckInterval) return;
        checking = true;
        try
        {
            var info = await service.CheckAsync();
            // 成功拿到结果（包括「已是最新」）才记录检查时间：开机首查常赶上网卡还没连上，
            // 失败也落时间的话，等于把当天剩下的自动检查全部推掉。
            MarkChecked();
            if (info is null || IsSkipped(info)) return;
            await PromptAsync(info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 静默：自动检查失败不该打断桌面。
        }
        finally
        {
            checking = false;
        }
    }

    /// <summary>
    /// 手动检查（菜单「检查更新」与设置里的按钮）：忽略「不再提示」，失败也要给文字反馈。
    /// 返回值是可直接显示的提示文字，弹窗已经说明了情况时返回空串。
    /// </summary>
    public async Task<string> CheckNowAsync()
    {
        if (checking) return Loc.UpdateChecking;
        checking = true;
        try
        {
            var info = await service.CheckAsync();
            MarkChecked();
            if (info is null) return Loc.Fmt(Loc.UpdateUpToDateFormat, UpdateService.CurrentVersionText);
            await PromptAsync(info);
            return "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Loc.Fmt(Loc.UpdateCheckFailedFormat, ex.Message);
        }
        finally
        {
            checking = false;
        }
    }

    private void MarkChecked()
    {
        settings.LastUpdateCheckUtc = DateTime.UtcNow;
        settingsService.Save(settings);
    }

    private bool IsSkipped(UpdateInfo info) =>
        UpdateService.ParseVersion(settings.SkippedUpdateVersion) is { } skipped && skipped.CompareTo(info.Version) >= 0;

    private async Task PromptAsync(UpdateInfo info)
    {
        if (info.InstallerUrl is null)
        {
            // Release 没带安装包时只能去发布页手动下载。
            if (Ask(Loc.Fmt(Loc.UpdateNoInstallerFormat, info.VersionText), MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                OpenUrl(info.ReleaseUrl);
            return;
        }

        var answer = Ask(
            Loc.Fmt(Loc.UpdateAvailableFormat, info.VersionText, UpdateService.CurrentVersionText, SizeText(info)),
            MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Cancel)
        {
            settings.SkippedUpdateVersion = info.VersionText;
            settingsService.Save(settings);
            return;
        }
        if (answer != MessageBoxResult.Yes) return;

        await DownloadAndInstallAsync(info);
    }

    private async Task DownloadAndInstallAsync(UpdateInfo info)
    {
        var dialog = new DownloadDialog(info, owner);
        string installer;
        try
        {
            var progress = new Progress<DownloadProgress>(dialog.ShowProgress);
            var task = UpdateService.DownloadAsync(info, updatesFolder, progress, dialog.Cancellation.Token);
            // 下载一结束（成功或抛错）就收掉进度窗；结果在 ShowDialog 返回后 await 同一个 task 取。
            _ = task.ContinueWith(_ => dialog.Dispatcher.BeginInvoke(dialog.Close), TaskScheduler.Default);
            dialog.ShowDialog();
            installer = await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ShowError(Loc.Fmt(Loc.UpdateDownloadFailedFormat, ex.Message));
            return;
        }

        var note = RunningFromInstallFolder() ? "" : Environment.NewLine + Environment.NewLine + Loc.UpdatePortableNote;
        if (Ask(Loc.Fmt(Loc.UpdateReadyQuestionFormat, installer) + note, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        LaunchInstaller(installer);
    }

    /// <summary>
    /// msiexec 必须等本进程退出、文件占用释放之后再跑，所以交给一个 cmd 先延时约 3 秒。
    /// 这里用 ping 而不是 timeout：本程序没有控制台，timeout 在没有 stdin 时会直接失败返回，
    /// 反而起不到等待作用。安装包按用户安装（perUser），不会弹管理员授权。
    /// </summary>
    private static void LaunchInstaller(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & msiexec /i \"{installerPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            ShowError(Loc.Fmt(Loc.UpdateInstallFailedFormat, ex.Message));
            return;
        }
        Application.Current.Shutdown();
    }

    /// <summary>是否跑在安装包写入的那个按用户安装目录里。</summary>
    private static bool RunningFromInstallFolder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DesktopCalendarWidget");
        var path = Environment.ProcessPath ?? "";
        return path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string SizeText(UpdateInfo info) => info.InstallerBytes > 0
        ? Loc.Fmt(Loc.UpdateSizeFormat, (long)Math.Ceiling(info.InstallerBytes / 1024d / 1024d))
        : Loc.UpdateSizeUnknown;

    private MessageBoxResult Ask(string text, MessageBoxButton buttons) =>
        MessageBox.Show(owner, text, Loc.UpdateCaption, buttons, MessageBoxImage.Question);

    private static void ShowError(string text) =>
        MessageBox.Show(text, Loc.UpdateCaption, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError(Loc.Fmt(Loc.UpdateOpenFailedFormat, ex.Message));
        }
    }

    /// <summary>下载进度小窗，代码构建即可，不值得单独一份 XAML。</summary>
    private sealed class DownloadDialog : Window
    {
        private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1, Height = 10, Margin = new Thickness(0, 14, 0, 0) };
        private readonly TextBlock detail = new()
        {
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0x7B, 0x70))
        };

        public CancellationTokenSource Cancellation { get; } = new();

        public DownloadDialog(UpdateInfo info, Window owner)
        {
            Title = Loc.UpdateDownloadingCaption;
            Owner = owner;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFC, 0xF8));

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Fmt(Loc.UpdateDownloadingFormat, info.VersionText, SizeText(info)),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(bar);
            panel.Children.Add(detail);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            // 点右上角 ✕ 同样算取消：否则 ShowDialog 直接返回，外层会一直等到下载结束。
            Closing += (_, _) => Cancellation.Cancel();
            var cancel = new Button { Content = Loc.UpdateCancelButton, Padding = new Thickness(14, 7, 14, 7) };
            // 取消即关闭：外层 await 任务时会捕获取消异常。
            cancel.Click += (_, _) =>
            {
                Cancellation.Cancel();
                Close();
            };
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;
        }

        public void ShowProgress(DownloadProgress progress)
        {
            if (progress.Ratio is not { } ratio) return;
            bar.Value = ratio;
            detail.Text = Loc.Fmt(
                Loc.UpdateDownloadProgressFormat,
                progress.DownloadedBytes / 1024d / 1024d,
                progress.TotalBytes / 1024d / 1024d);
        }
    }
}
