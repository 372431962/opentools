using System.Net.Http;
using System.Text.Json;

namespace DesktopCalendarWidget.Translate;

/// <summary>免费在线翻译接口（无需 API Key）的公共 HTTP 设施。</summary>
internal static class TranslateHttp
{
    /// <summary>HttpClient 长期复用，避免频繁创建导致套接字耗尽（与 HolidayService 同样的约定）。</summary>
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>请求到一半被取消不算失败，直接向上抛由 TranslateService 吞掉。</summary>
    public static async Task<string> GetStringAsync(string url, CancellationToken token, Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpSupport.ApplyUserAgent(request);
        if (headers is not null)
            foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }

    public static async Task<string> PostJsonAsync(string url, string json, CancellationToken token, Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        HttpSupport.ApplyUserAgent(request);
        if (headers is not null)
            foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }

    public static JsonDocument Parse(string json) => JsonDocument.Parse(json);
    /// <summary>单块最大字符数。中文经 URL 转义后每字约 9 字节，块太大会触发服务端 414。</summary>
    public const int ChunkLength = 800;

    private static readonly char[] ChunkBreaks = ['.', '!', '?', '。', '！', '？', '；', ';', '\n'];

    /// <summary>
    /// 按句子边界把长文本切块后逐块翻译。切点落在句末标点之后，拼接时不需要补任何分隔符；
    /// 只有一块时直接走原请求，短文本不受影响。任何一块失败就整体判失败，由调用方降级。
    /// </summary>
    public static async Task<string?> ChunkedTranslateAsync(ITranslateProvider provider, TranslateRequest request, CancellationToken token)
    {
        var chunks = SplitForOnline(request.Text);
        if (chunks.Count == 1) return await provider.TranslateAsync(request, token);
        var parts = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var part = await provider.TranslateAsync(new TranslateRequest(chunk, request.Target), token);
            if (string.IsNullOrWhiteSpace(part)) return null;
            parts.Add(part.Trim());
        }
        // 中文译文之间不加空格，英文之间加一个空格。
        return string.Join(request.Target == TranslateTarget.English ? " " : "", parts).Trim();
    }

    internal static List<string> SplitForOnline(string text)
    {
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(ChunkLength, text.Length - start);
            if (start + length < text.Length)
            {
                // 尽量在句末标点处断开，找不到就硬切，保证块长有界。
                var cut = text.LastIndexOfAny(ChunkBreaks, start + length - 1, length);
                if (cut > start) length = cut - start + 1;
            }
            chunks.Add(text.Substring(start, length));
            start += length;
        }
        return chunks;
    }
}
