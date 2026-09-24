namespace DesktopCalendarWidget;

/// <summary>
/// 联网模块的公共约定。各家免费接口对来源程序普遍不友好，统一带一个常见浏览器 UA；
/// 以前这个常量放在 Translate 下，天气与更新模块为了拿它不得不反向引用翻译模块。
/// </summary>
internal static class HttpSupport
{
    /// <summary>统一 User-Agent：节假日、天气、翻译、更新检查共用。</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";

    /// <summary>给请求加上统一的 UA。免费接口常据此区分爬虫与浏览器。</summary>
    public static void ApplyUserAgent(System.Net.Http.HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
}
