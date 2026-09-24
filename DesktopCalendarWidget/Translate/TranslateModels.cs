namespace DesktopCalendarWidget.Translate;

/// <summary>翻译方向的两个端点：中 ↔ 英。</summary>
public enum TranslateTarget
{
    Chinese,
    English
}

/// <summary>翻译结果的来源，用于状态栏展示与降级链排序。</summary>
public enum TranslateSource
{
    /// <summary>自动：按设置的优先级依次尝试在线接口。</summary>
    Auto,
    Bing,
    Google,
    MyMemory,
    OfflineDictionary
}

/// <summary>一次翻译请求。目标语言已由 LanguageDetector 决定，Provider 只负责转换。</summary>
public readonly record struct TranslateRequest(string Text, TranslateTarget Target)
{
    /// <summary>请求的源语言（由目标语言取反），Provider 映射到各家接口的语种代码。</summary>
    public TranslateTarget Source => Target == TranslateTarget.Chinese ? TranslateTarget.English : TranslateTarget.Chinese;
}

/// <summary>翻译结果。<see cref="IsOffline"/> 为 true 时 UI 必须标注“词典直译、仅应急”。</summary>
public sealed record TranslateResult(string Text, TranslateSource Source, bool IsOffline, double ElapsedMs);

/// <summary>
/// 翻译提供者。实现必须是纯网络或纯本地的无状态类，不得引用任何 WPF 类型，
/// 以便 tests/DesktopCalendarWidget.TranslateTests 直接链接源码做离线回归。
/// </summary>
public interface ITranslateProvider
{
    /// <summary>状态栏展示名，例如 "Bing"。</summary>
    string Name { get; }

    TranslateSource Source { get; }

    /// <summary>翻不出来时返回 null 或空串，由 TranslateService 切下一个 Provider。</summary>
    Task<string?> TranslateAsync(TranslateRequest request, CancellationToken token);
}
