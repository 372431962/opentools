namespace DesktopCalendarWidget.Translate;

/// <summary>
/// 离线词典兜底。所有在线接口都失败时用它做词级直译，结果必须在 UI 上标注为“词典直译”。
/// </summary>
public sealed class OfflineDictionaryProvider : ITranslateProvider
{
    private readonly TranslateDictionary dictionary;

    public OfflineDictionaryProvider(TranslateDictionary dictionary)
    {
        this.dictionary = dictionary;
    }

    public string Name => "Offline";

    public TranslateSource Source => TranslateSource.OfflineDictionary;

    public Task<string?> TranslateAsync(TranslateRequest request, CancellationToken token)
    {
        var result = dictionary.Translate(request.Text, request.Target);
        return Task.FromResult(string.IsNullOrWhiteSpace(result) ? null : result);
    }
}
