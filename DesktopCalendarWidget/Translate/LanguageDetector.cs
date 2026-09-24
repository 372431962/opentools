namespace DesktopCalendarWidget.Translate;

/// <summary>识别出的文本语种。首版只做中英互译，其余语种一律判定为 Other。</summary>
public enum TextLanguage
{
    Chinese,
    English,
    Mixed,
    Other,
    Empty
}

/// <summary>
/// 中英互译的语种识别。纯函数、零依赖，按字符类别统计比例判断：
/// 统计汉字（CJK 统一表意文字）与拉丁字母的个数，忽略数字、标点与空白。
/// 中文里夹杂英文术语很常见，所以按多数派判断而不是要求纯净文本。
/// </summary>
public static class LanguageDetector
{
    /// <summary>进入“中英混合”区间的汉字占比下界。</summary>
    private const double MixedLowerCjkRatio = 0.30;

    /// <summary>纯中文判断阈值为 1 减去该值，即汉字占比高于 0.90。</summary>
    private const double NonChineseTolerance = 0.10;

    public static TextLanguage Detect(string? text)
    {
        var cjk = 0;
        var latin = 0;
        foreach (var ch in text ?? "")
        {
            if (IsCjk(ch)) cjk++;
            else if (IsLatinLetter(ch)) latin++;
        }
        if (cjk == 0 && latin == 0) return TextLanguage.Empty;
        if (cjk == 0) return TextLanguage.English;
        if (latin == 0) return TextLanguage.Chinese;
        var ratio = cjk / (double)(cjk + latin);
        if (ratio >= MixedLowerCjkRatio && ratio <= 1 - NonChineseTolerance) return TextLanguage.Mixed;
        return ratio > 1 - NonChineseTolerance ? TextLanguage.Chinese : TextLanguage.English;
    }

    /// <summary>
    /// 决定翻译方向的目标语言：中文译英文，英文译中文。
    /// 中英混合时按多数派方向处理；纯中文且未识别出汉字以外内容时也走中译英。
    /// 无法判断（空、纯数字、其他语种）返回 null，由调用方拒绝翻译。
    /// </summary>
    public static TranslateTarget? TargetOf(string? text) => Detect(text) switch
    {
        TextLanguage.Chinese => TranslateTarget.English,
        TextLanguage.English => TranslateTarget.Chinese,
        TextLanguage.Mixed => CountCjk(text) * 2 >= CountLatin(text) ? TranslateTarget.English : TranslateTarget.Chinese,
        _ => null
    };

    private static int CountCjk(string? text) => (text ?? "").Count(IsCjk);

    private static int CountLatin(string? text) => (text ?? "").Count(IsLatinLetter);

    private static bool IsCjk(char ch) => (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF);

    private static bool IsLatinLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
}
