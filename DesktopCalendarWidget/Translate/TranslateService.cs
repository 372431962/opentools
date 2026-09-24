using System.Collections.Concurrent;
using System.Diagnostics;

namespace DesktopCalendarWidget.Translate;

/// <summary>翻译结果的状态，UI 据此选择提示文案。</summary>
public enum TranslateStatus
{
    Ok,
    /// <summary>输入为空或只有空白。</summary>
    Empty,
    /// <summary>不是中英文字，首版只做中英互译。</summary>
    Unsupported,
    /// <summary>所有来源都失败，包括离线词典。</summary>
    Failed
}

/// <summary>
/// 一次翻译的完整结果。<see cref="IsOffline"/> 表示结果来自内置词典的逐词直译，
/// <see cref="FromCache"/> 表示直接命中缓存没有联网。
/// </summary>
public sealed record TranslateOutcome(
    TranslateStatus Status,
    string? Text,
    TranslateTarget? Target,
    TranslateSource Source,
    bool IsOffline,
    bool FromCache,
    double ElapsedMs,
    bool Truncated);

/// <summary>
/// 翻译编排：决定方向、查缓存、按设置挑选 Provider 依次降级、最后走离线词典。
/// 对失败的 Provider 做短暂熔断，避免“Google 不可达”这类情况每次都白等一个超时。
/// </summary>
public sealed class TranslateService
{
    /// <summary>单次可翻译的最大字符数，超出部分直接丢弃并由 UI 提示。</summary>
    public const int MaxLength = 5000;

    /// <summary>单个 Provider 的超时。免费接口普遍不稳，宁可快点换下一个。</summary>
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Provider 连续失败后跳过多久。</summary>
    public static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(60);

    public const string SettingAuto = "auto";
    public const string SettingMyMemory = "mymemory";
    public const string SettingGoogle = "google";
    public const string SettingOffline = "offline";

    private readonly TranslateCache cache;
    private readonly OfflineDictionaryProvider offline;
    private readonly List<ITranslateProvider> online;
    private readonly ConcurrentDictionary<TranslateSource, DateTime> brokenUntil = new();

    public TranslateService(
        string? cachePath = null,
        TranslateDictionary? dictionary = null,
        IEnumerable<ITranslateProvider>? providers = null)
    {
        cache = new TranslateCache(cachePath);
        offline = new OfflineDictionaryProvider(dictionary ?? TranslateDictionary.FromEntries([]));
        // 顺序即降级顺序：MyMemory 在国内可达但有每日额度，额度用尽时会自己失败并让位；
        // Google 质量更好但国内经常不可达，放在后面当第二选择。
        online = providers?.ToList() ?? [new MyMemoryTranslateProvider(), new GoogleTranslateProvider()];
    }

    /// <summary>设置里可选的在线 Provider 列表，供设置界面生成下拉框。</summary>
    public IReadOnlyList<ITranslateProvider> OnlineProviders => online;

    public async Task<TranslateOutcome> TranslateAsync(
        string? text,
        string providerSetting,
        bool offlineFallback,
        CancellationToken token = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return new TranslateOutcome(TranslateStatus.Empty, null, null, TranslateSource.Auto, false, false, 0, false);

        var truncated = trimmed.Length > MaxLength;
        if (truncated) trimmed = trimmed[..MaxLength];
        var target = LanguageDetector.TargetOf(trimmed);
        if (target is null)
            return new TranslateOutcome(TranslateStatus.Unsupported, null, null, TranslateSource.Auto, false, false, 0, truncated);

        var request = new TranslateRequest(trimmed, target.Value);

        if (cache.TryGet(request, out var cached))
            return new TranslateOutcome(TranslateStatus.Ok, cached, target, TranslateSource.Auto, false, true, stopwatch.Elapsed.TotalMilliseconds, truncated);

        foreach (var provider in SelectProviders(providerSetting, offlineFallback))
        {
            if (IsBroken(provider.Source)) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ProviderTimeout);
                // 在线 GET 接口对长文本分块；离线词典直接处理完整文本，保留最长匹配语义。
                var result = provider.Source == TranslateSource.OfflineDictionary
                    ? await provider.TranslateAsync(request, timeout.Token)
                    : await TranslateHttp.ChunkedTranslateAsync(provider, request, timeout.Token);
                // 在线接口“翻不出来”同样算失败：额度用尽这类空结果不该每次都白跑一趟。
                if (string.IsNullOrWhiteSpace(result))
                {
                    if (provider.Source != TranslateSource.OfflineDictionary) MarkBroken(provider.Source);
                    continue;
                }
                MarkHealthy(provider.Source);
                if (provider.Source != TranslateSource.OfflineDictionary) cache.Set(request, result);
                return new TranslateOutcome(TranslateStatus.Ok, result, target, provider.Source,
                    provider.Source == TranslateSource.OfflineDictionary, false, stopwatch.Elapsed.TotalMilliseconds, truncated);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                MarkBroken(provider.Source);
            }
        }
        return new TranslateOutcome(TranslateStatus.Failed, null, target, TranslateSource.Auto, false, false, stopwatch.Elapsed.TotalMilliseconds, truncated);
    }

    /// <summary>把最近的译文写盘，退出前调用一次即可。</summary>
    public void Flush() => cache.FlushToDisk();

    /// <summary>按设置挑选本次要走的降级链。显式指定某个在线接口时只用它，但离线兜底仍接在最后。</summary>
    internal List<ITranslateProvider> SelectProviders(string providerSetting, bool offlineFallback)
    {
        var selected = new List<ITranslateProvider>();
        switch ((providerSetting ?? SettingAuto).Trim().ToLowerInvariant())
        {
            case SettingMyMemory:
                selected.AddRange(online.Where(x => x.Source == TranslateSource.MyMemory));
                break;
            case SettingGoogle:
                selected.AddRange(online.Where(x => x.Source == TranslateSource.Google));
                break;
            case SettingOffline:
                selected.Add(offline);
                return selected;
            default:
                selected.AddRange(online);
                break;
        }
        if (offlineFallback) selected.Add(offline);
        return selected;
    }

    private bool IsBroken(TranslateSource source) =>
        brokenUntil.TryGetValue(source, out var until) && until > DateTime.UtcNow;

    private void MarkBroken(TranslateSource source) => brokenUntil[source] = DateTime.UtcNow + CircuitBreakDuration;

    private void MarkHealthy(TranslateSource source) => brokenUntil.TryRemove(source, out _);
}
