using System.Text.Json;

namespace DesktopCalendarWidget.Translate;

/// <summary>
/// Google 免费翻译端点（translate.googleapis.com，client=gtx）。无需 API Key，
/// 但在中国大陆网络下经常不可达，所以放在降级链第二位并且超时较短。
/// </summary>
public sealed class GoogleTranslateProvider : ITranslateProvider
{
    public string Name => "Google";

    public TranslateSource Source => TranslateSource.Google;

    /// <summary>纯函数：构造请求 URL，便于不联网的回归测试。</summary>
    public static string BuildUrl(string text, TranslateTarget target)
    {
        var to = target == TranslateTarget.English ? "en" : "zh-CN";
        return $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={to}&dt=t&q={Uri.EscapeDataString(text)}";
    }

    /// <summary>
    /// 纯函数：解析响应。响应是嵌套数组：[[["译文","原文",...],...],null,"zh-CN"]，
    /// 第 0 组的每段第一项拼起来就是完整译文。
    /// </summary>
    public static string? ParseResponse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
            var segments = root[0];
            if (segments.ValueKind != JsonValueKind.Array) return null;
            var parts = new List<string>();
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0) continue;
                var first = segment[0];
                if (first.ValueKind == JsonValueKind.String)
                {
                    var value = first.GetString();
                    if (!string.IsNullOrEmpty(value)) parts.Add(value);
                }
            }
            var text = string.Concat(parts).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public async Task<string?> TranslateAsync(TranslateRequest request, CancellationToken token)
    {
        var json = await TranslateHttp.GetStringAsync(BuildUrl(request.Text, request.Target), token);
        return ParseResponse(json);
    }
}
