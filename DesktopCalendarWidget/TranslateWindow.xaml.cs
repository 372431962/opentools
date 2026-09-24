using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopCalendarWidget.Translate;

namespace DesktopCalendarWidget;

/// <summary>
/// 中英互译小窗。输入停止 400ms 后自动翻译，Ctrl+Enter 立即翻译；
/// 可置顶到桌面所有窗口之上（WS_EX_TOPMOST），设置会记住窗口位置与置顶状态。
/// </summary>
public partial class TranslateWindow : Window
{
    /// <summary>停止输入多久后自动翻译。</summary>
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>“已复制”按钮文案保持多久。</summary>
    private static readonly TimeSpan CopiedFeedback = TimeSpan.FromSeconds(1.2);

    private readonly SettingsService settingsService;
    private WidgetSettings settings;
    private readonly TranslateService translateService;
    private readonly DispatcherTimer debounceTimer;
    private CancellationTokenSource? pending;
    private DispatcherTimer? copyTimer;
    private bool restoringState;
    private bool skipSettingsSaveOnClose;
    private TranslateOutcome? lastOutcome;

    public TranslateWindow(WidgetSettings settings, SettingsService settingsService)
    {
        InitializeComponent();
        this.settings = settings;
        this.settingsService = settingsService;
        // 翻译逻辑与 UI 解耦：服务只认字符串与取消令牌，可被测试工程直接链接。
        translateService = new TranslateService(
            System.IO.Path.Combine(settingsService.DataFolder, "translate-cache.json"),
            TranslateDictionary.LoadEmbedded());

        Left = settings.TranslateLeft;
        Top = settings.TranslateTop;
        Width = Math.Max(420, settings.TranslateWidth);
        Height = Math.Max(300, settings.TranslateHeight);
        TopmostToggle.IsChecked = settings.TranslateTopmost;
        Topmost = settings.TranslateTopmost;
        UpdateDirection(null);
        UpdateCharCount();
        UpdateHints();

        debounceTimer = new DispatcherTimer { Interval = DebounceInterval };
        debounceTimer.Tick += (_, _) =>
        {
            debounceTimer.Stop();
            _ = RunAsync();
        };
        Closing += TranslateWindow_Closing;
    }

    /// <summary>主窗口换过配置对象后同步引用，避免关闭时把旧配置写回去覆盖新设置。</summary>
    public void UpdateSettings(WidgetSettings settings) => this.settings = settings;

    /// <summary>重建静态 XAML 文案，同时保留用户当前的翻译与窗口状态。</summary>
    public TranslateWindow RecreateForNewLanguage()
    {
        var resumeTranslation = (pending is not null || debounceTimer.IsEnabled) &&
            !string.IsNullOrWhiteSpace(InputBox.Text);
        var replacement = new TranslateWindow(settings, settingsService)
        {
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height,
            Topmost = Topmost
        };
        replacement.TopmostToggle.IsChecked = Topmost;
        replacement.restoringState = true;
        replacement.InputBox.Text = InputBox.Text;
        if (lastOutcome is not null && !resumeTranslation) replacement.ApplyOutcome(lastOutcome);
        else replacement.ResultBox.Text = ResultBox.Text;
        replacement.restoringState = false;
        replacement.UpdateCharCount();
        replacement.UpdateHints();
        replacement.UpdateDetectedLanguage();
        skipSettingsSaveOnClose = true;
        Close();
        replacement.Show();
        if (resumeTranslation) replacement.debounceTimer.Start();
        return replacement;
    }

    /// <summary>没有标题栏，顶栏空白处就是拖动区。</summary>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TranslateWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        debounceTimer.Stop();
        CancelPending();
        if (!skipSettingsSaveOnClose)
        {
            settings.TranslateLeft = Left;
            settings.TranslateTop = Top;
            settings.TranslateWidth = Width;
            settings.TranslateHeight = Height;
            settings.TranslateTopmost = Topmost;
            settingsService.Save(settings);
        }
        translateService.Flush();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (restoringState) return;
        debounceTimer.Stop();
        CancelPending();
        lastOutcome = null;
        ResultBox.Text = "";
        StatusText.Text = "";
        copyTimer?.Stop();
        CopyButton.Content = Loc.TranslateCopy;
        UpdateCharCount();
        UpdateHints();
        UpdateDetectedLanguage();
        // 输入变化即废弃旧译文，请求只在停止输入 400ms 后发送。
        debounceTimer.Start();
    }

    private void UpdateDetectedLanguage()
    {
        var text = InputBox.Text.Trim();
        if (text.Length > TranslateService.MaxLength) text = text[..TranslateService.MaxLength];
        UpdateDirection(LanguageDetector.TargetOf(text));
        if (LanguageDetector.Detect(text) == TextLanguage.Mixed)
            DetectText.Text = Loc.TranslateMixedText;
    }

    /// <summary>译文是代码写进去的，只用来收放占位提示。</summary>
    private void ResultBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateHints();

    private void UpdateHints()
    {
        InputHint.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultHint.Visibility = ResultBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            debounceTimer.Stop();
            _ = RunAsync();
            e.Handled = true;
        }
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        debounceTimer.Stop();
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        CancelPending();
        ResultBox.Text = "";
        lastOutcome = null;
        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ResultBox.Text = "";
            StatusText.Text = Loc.TranslateEmpty;
            UpdateDirection(null);
            return;
        }

        var cancellation = new CancellationTokenSource();
        pending = cancellation;
        StatusText.Text = Loc.TranslateBusy;
        try
        {
            var outcome = await translateService.TranslateAsync(
                text, settings.TranslateProvider, settings.TranslateOfflineFallback, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            ApplyOutcome(outcome);
        }
        catch (OperationCanceledException)
        {
            // 新输入取代了本次请求，静默丢弃。
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            {
                ResultBox.Text = "";
                StatusText.Text = $"{Loc.TranslateFailed}: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(pending, cancellation)) pending = null;
            cancellation.Dispose();
        }
    }

    private void CancelPending()
    {
        pending?.Cancel();
        pending = null;
    }

    private void ApplyOutcome(TranslateOutcome outcome)
    {
        UpdateDirection(outcome.Target);
        lastOutcome = outcome;
        switch (outcome.Status)
        {
            case TranslateStatus.Empty:
                ResultBox.Text = "";
                StatusText.Text = Loc.TranslateEmpty;
                return;
            case TranslateStatus.Unsupported:
                ResultBox.Text = "";
                StatusText.Text = Loc.TranslateUnsupported;
                return;
            case TranslateStatus.Failed:
                ResultBox.Text = "";
                StatusText.Text = Loc.TranslateFailed;
                return;
        }

        ResultBox.Text = outcome.Text ?? "";
        if (outcome.FromCache)
        {
            StatusText.Text = Loc.TranslateCached;
        }
        else if (outcome.IsOffline)
        {
            StatusText.Text = Loc.Fmt(Loc.TranslateStatusFormat, Loc.TranslateOfflineNote, outcome.ElapsedMs);
        }
        else
        {
            StatusText.Text = Loc.Fmt(Loc.TranslateStatusFormat, outcome.Source.ToString(), outcome.ElapsedMs);
        }
        if (outcome.Truncated) StatusText.Text = $"{StatusText.Text} · {Loc.Fmt(Loc.TranslateTooLong, TranslateService.MaxLength)}";
    }

    /// <summary>顶栏方向指示。方向永远是自动检测出来的，交换按钮只是把译文送回输入框再翻一次。</summary>
    private void UpdateDirection(TranslateTarget? target)
    {
        // target 为空表示还没识别出方向，默认按中译英显示。
        var targetIsEnglish = target != TranslateTarget.Chinese;
        var sourceName = targetIsEnglish ? Loc.TranslateLangChinese : Loc.TranslateLangEnglish;
        var targetName = targetIsEnglish ? Loc.TranslateLangEnglish : Loc.TranslateLangChinese;
        DirectionText.Text = Loc.Fmt(Loc.TranslateDirectionFormat, sourceName, targetName);
        DetectText.Text = target is null ? "" : Loc.Fmt(Loc.TranslateDetectFormat, targetName);
    }

    private void UpdateCharCount() =>
        CharCountText.Text = Loc.Fmt(Loc.TranslateCharCountFormat, InputBox.Text.Length, TranslateService.MaxLength);

    private void SwapButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ResultBox.Text)) return;
        InputBox.Text = ResultBox.Text;
        debounceTimer.Stop();
        _ = RunAsync();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultBox.Text)) return;
        try
        {
            Clipboard.SetText(ResultBox.Text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被别的进程占住，直接放弃提示，不打断用户。
            return;
        }
        CopyButton.Content = Loc.TranslateCopied;
        copyTimer?.Stop();
        copyTimer = new DispatcherTimer { Interval = CopiedFeedback };
        copyTimer.Tick += (_, _) =>
        {
            copyTimer.Stop();
            CopyButton.Content = Loc.TranslateCopy;
        };
        copyTimer.Start();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        CancelPending();
        lastOutcome = null;
        InputBox.Text = "";
        debounceTimer.Stop();
        ResultBox.Text = "";
        StatusText.Text = "";
        UpdateDirection(null);
    }

    /// <summary>置顶开关：Topmost 即 WS_EX_TOPMOST，天然显示在普通窗口之上。</summary>
    private void TopmostToggle_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
        settings.TranslateTopmost = Topmost;
        settingsService.Save(settings);
    }
}
