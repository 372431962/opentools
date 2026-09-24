using System.Globalization;
using System.Resources;
using System.Threading;

namespace DesktopCalendarWidget;

/// <summary>
/// 界面文案。属性名即资源键名，XAML 的 {x:Static loc:Loc.X} 和 C# 调用共用同一个编译期符号，
/// 键写错会直接编译失败而不是运行时静默丢字。
/// 语言只在启动时确定（App 读配置后调用 Apply），改语言需要重启，因此 XAML 里用一次性的
/// x:Static 就够了，不需要 DynamicResource。
/// </summary>
internal static class Loc
{
    private static readonly ResourceManager Manager = new("DesktopCalendarWidget.Strings", typeof(Loc).Assembly);

    /// <summary>中文界面才显示农历与中国节假日；其余语言按中文界面里“关掉这两项”的方式渲染。</summary>
    public static bool IsChinese => CurrentCulture.TwoLetterISOLanguageName == "zh";

    public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.CurrentCulture;

    public const string ChineseTag = "zh-CN";
    public const string EnglishTag = "en-US";

    /// <summary>把配置里的语言写进 UI 区域性；无法识别的值回退到中文。</summary>
    public static void Apply(string? languageTag)
    {
        var culture = string.Equals(languageTag, EnglishTag, StringComparison.OrdinalIgnoreCase)
            ? new CultureInfo(EnglishTag)
            : new CultureInfo(ChineseTag);
        CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static string Fmt(string pattern, params object?[] args) => string.Format(CurrentCulture, pattern, args);

    /// <summary>
    /// 取文案。资源没随程序集打包时（例如发布漏了卫星程序集）GetString 会抛异常，
    /// 这里退回键名——界面难看但程序还能起来，不该因为一句文案整个崩掉。
    /// </summary>
    private static string Get(string key)
    {
        try
        {
            return Manager.GetString(key, CurrentCulture) ?? key;
        }
        catch (MissingManifestResourceException)
        {
            return key;
        }
    }

    // 主窗口
    public static string AppTitle => Get(nameof(AppTitle));
    public static string TodayButton => Get(nameof(TodayButton));
    public static string MenuButtonTip => Get(nameof(MenuButtonTip));
    public static string PreviousMonthTip => Get(nameof(PreviousMonthTip));
    public static string NextMonthTip => Get(nameof(NextMonthTip));
    public static string LegendNoRest => Get(nameof(LegendNoRest));
    public static string LegendRest => Get(nameof(LegendRest));
    public static string TodayPrefix => Get(nameof(TodayPrefix));
    public static string DateFormatLong => Get(nameof(DateFormatLong));
    public static string MonthTitleFormat => Get(nameof(MonthTitleFormat));
    public static string WeekDayNames => Get(nameof(WeekDayNames));
    public static string MarkerRest => Get(nameof(MarkerRest));
    public static string MarkerWorkday => Get(nameof(MarkerWorkday));
    public static string SuffixWorkdayAdjust => Get(nameof(SuffixWorkdayAdjust));
    public static string SuffixHolidayOff => Get(nameof(SuffixHolidayOff));
    public static string LunarYearLabel => Get(nameof(LunarYearLabel));
    public static string WeekNoRest => Get(nameof(WeekNoRest));
    public static string WeekSingleRest => Get(nameof(WeekSingleRest));
    public static string WeekDoubleRest => Get(nameof(WeekDoubleRest));
    public static string HotkeyConflictText => Get(nameof(HotkeyConflictText));

    // 菜单
    public static string MenuSettings => Get(nameof(MenuSettings));
    public static string MenuShowWidget => Get(nameof(MenuShowWidget));
    public static string MenuHideWidget => Get(nameof(MenuHideWidget));
    public static string MenuExit => Get(nameof(MenuExit));
    public static string MenuLock => Get(nameof(MenuLock));
    public static string MenuUnlock => Get(nameof(MenuUnlock));
    public static string MenuClickThrough => Get(nameof(MenuClickThrough));
    public static string MenuTopmost => Get(nameof(MenuTopmost));
    public static string MenuTranslate => Get(nameof(MenuTranslate));
    public static string MenuCheckUpdate => Get(nameof(MenuCheckUpdate));

    // 设置窗口
    public static string SettingsTitle => Get(nameof(SettingsTitle));
    public static string SectionDisplay => Get(nameof(SectionDisplay));
    public static string ShowLunarCheck => Get(nameof(ShowLunarCheck));
    public static string ShowHolidayCheck => Get(nameof(ShowHolidayCheck));
    public static string DisplayHideNote => Get(nameof(DisplayHideNote));
    public static string ChineseOnlyNote => Get(nameof(ChineseOnlyNote));
    public static string SectionLanguage => Get(nameof(SectionLanguage));
    public static string LanguageNote => Get(nameof(LanguageNote));
    public static string LanguageChinese => Get(nameof(LanguageChinese));
    public static string LanguageEnglish => Get(nameof(LanguageEnglish));
    public static string SectionRestDays => Get(nameof(SectionRestDays));
    public static string RestPatternLabel => Get(nameof(RestPatternLabel));
    public static string PatternNone => Get(nameof(PatternNone));
    public static string PatternWeekly => Get(nameof(PatternWeekly));
    public static string PatternAlternate => Get(nameof(PatternAlternate));
    public static string SingleRestLabel => Get(nameof(SingleRestLabel));
    public static string DaySaturday => Get(nameof(DaySaturday));
    public static string DaySunday => Get(nameof(DaySunday));
    public static string AnchorSingleThisWeek => Get(nameof(AnchorSingleThisWeek));
    public static string CurrentWeekFormat => Get(nameof(CurrentWeekFormat));
    public static string AlternateExplainer => Get(nameof(AlternateExplainer));
    public static string SectionWindow => Get(nameof(SectionWindow));
    public static string LockCheck => Get(nameof(LockCheck));
    public static string ClickThroughCheck => Get(nameof(ClickThroughCheck));
    public static string StartWithWindowsCheck => Get(nameof(StartWithWindowsCheck));
    public static string HotkeyNote => Get(nameof(HotkeyNote));
    public static string SectionOnline => Get(nameof(SectionOnline));
    public static string OnlineExplainer => Get(nameof(OnlineExplainer));
    public static string UpdateUrlLabel => Get(nameof(UpdateUrlLabel));
    public static string UpdateButton => Get(nameof(UpdateButton));
    public static string UrlNeedsPlaceholder => Get(nameof(UrlNeedsPlaceholder));
    public static string Updating => Get(nameof(Updating));
    public static string UpdateDone => Get(nameof(UpdateDone));
    public static string UpdateFailed => Get(nameof(UpdateFailed));
    public static string SectionLocalData => Get(nameof(SectionLocalData));
    public static string LocalDataHint => Get(nameof(LocalDataHint));
    public static string SaveLocalButton => Get(nameof(SaveLocalButton));
    public static string OpenFolderButton => Get(nameof(OpenFolderButton));
    public static string LocalSavedCount => Get(nameof(LocalSavedCount));
    public static string LocalFormatError => Get(nameof(LocalFormatError));
    public static string OpenFolderFailed => Get(nameof(OpenFolderFailed));
    public static string CancelButton => Get(nameof(CancelButton));
    public static string SaveButton => Get(nameof(SaveButton));
    public static string RestartCaption => Get(nameof(RestartCaption));
    public static string RestartQuestion => Get(nameof(RestartQuestion));

    // 节假日下载
    public static string UrlFormatFailed => Get(nameof(UrlFormatFailed));
    public static string NoHolidayData => Get(nameof(NoHolidayData));

    // 翻译窗口
    public static string TranslateTitle => Get(nameof(TranslateTitle));
    public static string TranslateInputHint => Get(nameof(TranslateInputHint));
    public static string TranslateResultHint => Get(nameof(TranslateResultHint));
    public static string TranslateRun => Get(nameof(TranslateRun));
    public static string TranslateCopy => Get(nameof(TranslateCopy));
    public static string TranslateCopied => Get(nameof(TranslateCopied));
    public static string TranslateClear => Get(nameof(TranslateClear));
    public static string TranslateSwap => Get(nameof(TranslateSwap));
    public static string TranslateDirectionFormat => Get(nameof(TranslateDirectionFormat));
    public static string TranslateDetectFormat => Get(nameof(TranslateDetectFormat));
    public static string TranslateLangChinese => Get(nameof(TranslateLangChinese));
    public static string TranslateLangEnglish => Get(nameof(TranslateLangEnglish));
    public static string TranslateBusy => Get(nameof(TranslateBusy));
    public static string TranslateCached => Get(nameof(TranslateCached));
    public static string TranslateOfflineNote => Get(nameof(TranslateOfflineNote));
    public static string TranslateMixedText => Get(nameof(TranslateMixedText));
    public static string TranslateTooLong => Get(nameof(TranslateTooLong));
    public static string TranslateEmpty => Get(nameof(TranslateEmpty));
    public static string TranslateUnsupported => Get(nameof(TranslateUnsupported));
    public static string TranslateFailed => Get(nameof(TranslateFailed));
    public static string TranslateStatusFormat => Get(nameof(TranslateStatusFormat));
    public static string TranslateTopmostCheck => Get(nameof(TranslateTopmostCheck));
    public static string TranslateClose => Get(nameof(TranslateClose));
    public static string TranslatePrivacyNote => Get(nameof(TranslatePrivacyNote));
    public static string TranslateCharCountFormat => Get(nameof(TranslateCharCountFormat));

    // 翻译设置
    public static string SectionTranslate => Get(nameof(SectionTranslate));
    public static string TranslateProviderLabel => Get(nameof(TranslateProviderLabel));
    public static string TranslateProviderAuto => Get(nameof(TranslateProviderAuto));
    public static string TranslateProviderMyMemory => Get(nameof(TranslateProviderMyMemory));
    public static string TranslateProviderGoogle => Get(nameof(TranslateProviderGoogle));
    public static string TranslateProviderOffline => Get(nameof(TranslateProviderOffline));
    public static string TranslateOfflineFallbackCheck => Get(nameof(TranslateOfflineFallbackCheck));
    public static string TranslateExplainer => Get(nameof(TranslateExplainer));

    // 天气
    public static string SectionWeather => Get(nameof(SectionWeather));
    public static string WeatherCheck => Get(nameof(WeatherCheck));
    public static string WeatherCityLabel => Get(nameof(WeatherCityLabel));
    public static string WeatherRefreshLabel => Get(nameof(WeatherRefreshLabel));
    public static string WeatherUpdateButton => Get(nameof(WeatherUpdateButton));
    public static string WeatherUpdating => Get(nameof(WeatherUpdating));
    public static string WeatherUpdatedFormat => Get(nameof(WeatherUpdatedFormat));
    public static string WeatherFailed => Get(nameof(WeatherFailed));
    public static string WeatherRangeNote => Get(nameof(WeatherRangeNote));
    public static string WeatherPrivacyNote => Get(nameof(WeatherPrivacyNote));
    public static string WeatherTooltipFormat => Get(nameof(WeatherTooltipFormat));
    public static string WeatherPrecipFormat => Get(nameof(WeatherPrecipFormat));
    public static string WeatherSourceFormat => Get(nameof(WeatherSourceFormat));
    public static string WeatherClear => Get(nameof(WeatherClear));
    public static string WeatherPartlyCloudy => Get(nameof(WeatherPartlyCloudy));
    public static string WeatherOvercast => Get(nameof(WeatherOvercast));
    public static string WeatherFog => Get(nameof(WeatherFog));
    public static string WeatherDrizzle => Get(nameof(WeatherDrizzle));
    public static string WeatherRain => Get(nameof(WeatherRain));
    public static string WeatherHeavyRain => Get(nameof(WeatherHeavyRain));
    public static string WeatherSnow => Get(nameof(WeatherSnow));
    public static string WeatherThunderstorm => Get(nameof(WeatherThunderstorm));
    public static string WeatherUnknown => Get(nameof(WeatherUnknown));

    // 自动更新
    public static string SectionUpdate => Get(nameof(SectionUpdate));
    public static string UpdateCurrentVersionFormat => Get(nameof(UpdateCurrentVersionFormat));
    public static string AutoCheckUpdatesCheck => Get(nameof(AutoCheckUpdatesCheck));
    public static string CheckUpdateButton => Get(nameof(CheckUpdateButton));
    public static string UpdatePrivacyNote => Get(nameof(UpdatePrivacyNote));
    public static string UpdateCaption => Get(nameof(UpdateCaption));
    public static string UpdateChecking => Get(nameof(UpdateChecking));
    public static string UpdateUpToDateFormat => Get(nameof(UpdateUpToDateFormat));
    public static string UpdateCheckFailedFormat => Get(nameof(UpdateCheckFailedFormat));
    public static string UpdateInvalidRelease => Get(nameof(UpdateInvalidRelease));
    public static string UpdateAvailableFormat => Get(nameof(UpdateAvailableFormat));
    public static string UpdateNoInstallerFormat => Get(nameof(UpdateNoInstallerFormat));
    public static string UpdateSizeFormat => Get(nameof(UpdateSizeFormat));
    public static string UpdateSizeUnknown => Get(nameof(UpdateSizeUnknown));
    public static string UpdateDownloadingCaption => Get(nameof(UpdateDownloadingCaption));
    public static string UpdateDownloadingFormat => Get(nameof(UpdateDownloadingFormat));
    public static string UpdateDownloadProgressFormat => Get(nameof(UpdateDownloadProgressFormat));
    public static string UpdateCancelButton => Get(nameof(UpdateCancelButton));
    public static string UpdateDownloadFailedFormat => Get(nameof(UpdateDownloadFailedFormat));
    public static string UpdateReadyQuestionFormat => Get(nameof(UpdateReadyQuestionFormat));
    public static string UpdatePortableNote => Get(nameof(UpdatePortableNote));
    public static string UpdateInstallFailedFormat => Get(nameof(UpdateInstallFailedFormat));
    public static string UpdateOpenFailedFormat => Get(nameof(UpdateOpenFailedFormat));

    // 提示
    public static string StartupFailedFormat => Get(nameof(StartupFailedFormat));
    public static string WeatherStaleNote => Get(nameof(WeatherStaleNote));
    public static string HotkeysUnavailableFormat => Get(nameof(HotkeysUnavailableFormat));
    public static string UpdateVerifyFailed => Get(nameof(UpdateVerifyFailed));
}
