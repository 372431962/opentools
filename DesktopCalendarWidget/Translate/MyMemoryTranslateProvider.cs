using System.Text.Json;

namespace DesktopCalendarWidget.Translate;

/// <summary>
/// MyMemory 免费接口（无需 API Key）。国内可达，是降级链的首选在线接口；
/// 匿名额度约 5000 字符/天，用尽时接口会把提示语塞进译文，解析层会把这种情况判为失败并让位给下一个。
/// </summary>
public sealed class MyMemoryTranslateProvider : ITranslateProvider
{
    public string Name => "MyMemory";

    public TranslateSource Source => TranslateSource.MyMemory;

    /// <summary>纯函数：构造请求 URL，便于不联网的回归测试。</summary>
    public static string BuildUrl(string text, TranslateTarget target)
    {
        var pair = target == TranslateTarget.English ? "zh-CN|en" : "en|zh-CN";
        return $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(pair)}";
    }

    /// <summary>纯函数：解析响应。解析不出正文时返回 null，由服务层切下一个 Provider。</summary>
    public static string? ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("responseStatus", out var status) && status.ValueKind == JsonValueKind.Number &&
                status.GetInt32() != 200) return null;
            if (root.TryGetProperty("responseData", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("translatedText", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                var value = text.GetString()?.Trim();
                return string.IsNullOrEmpty(value) || IsQuotaMessage(value) ? null : value;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>额度用尽时接口会把提示语塞进 translatedText，这种结果要当失败处理。</summary>
    private static bool IsQuotaMessage(string value) =>
        value.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> TranslateAsync(TranslateRequest request, CancellationToken token)
    {
        var json = await TranslateHttp.GetStringAsync(BuildUrl(request.Text, request.Target), token);
        return ParseResponse(json);
    }
}
