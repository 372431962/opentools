using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopCalendarWidget.Translate;

/// <summary>
/// 离线中英词典。数据是随程序打包的 JSON（中文词 → 英文），反向表在内存里反转得到，
/// 两个方向共用同一份数据。断网时用它做词级直译兜底：中文按“最长匹配”切词，
/// 英文按空格/标点切词后先试双词短语再试单词。它是应急手段，不是翻译引擎，
/// UI 必须把结果标注为“离线词典逐词直译”。
/// </summary>
public sealed partial class TranslateDictionary
{
    /// <summary>词条键的最大长度（字符数），决定最长匹配最多向前看多少字。</summary>
    private readonly int maxKeyLength;
    private readonly Dictionary<string, string> zhToEn;
    private readonly Dictionary<string, string> enToZh;

    private TranslateDictionary(Dictionary<string, string> zhToEn, Dictionary<string, string> enToZh, int maxKeyLength)
    {
        this.zhToEn = zhToEn;
        this.enToZh = enToZh;
        this.maxKeyLength = maxKeyLength;
    }

    public int Count => zhToEn.Count;

    public static string ResourceName { get; } = "DesktopCalendarWidget.Translate.Data.zh-en-dict.json";

    /// <summary>读取随程序打包的词典；资源缺失或损坏时返回空词典，不影响联网翻译。</summary>
    public static TranslateDictionary LoadEmbedded()
    {
        try
        {
            using var stream = typeof(TranslateDictionary).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return FromEntries([]);
            using var reader = new StreamReader(stream);
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? [];
            return FromEntries(entries);
        }
        catch (JsonException)
        {
            return FromEntries([]);
        }
        catch (IOException)
        {
            return FromEntries([]);
        }
    }

    /// <summary>由词条构造词典。反向表遇到重复英文时保留第一条，避免同一个词被后写的释义顶掉。</summary>
    public static TranslateDictionary FromEntries(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var zhToEn = new Dictionary<string, string>(StringComparer.Ordinal);
        var enToZh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maxKeyLength = 1;
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            var zhKey = key.Trim();
            var enValue = value.Trim();
            zhToEn[zhKey] = enValue;
            if (zhKey.Length > maxKeyLength) maxKeyLength = zhKey.Length;
            var reverseKey = NormalizeEnglish(enValue);
            if (!enToZh.ContainsKey(reverseKey)) enToZh[reverseKey] = zhKey;
        }
        return new TranslateDictionary(zhToEn, enToZh, maxKeyLength);
    }

    /// <summary>英文方向的词条归一化：去首尾空白、连续空白折成一个空格、转小写。</summary>
    public static string NormalizeEnglish(string value) =>
        Whitespace().Replace(value.Trim(), " ").ToLowerInvariant();

    public bool TryLookup(string term, TranslateTarget target, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(term)) return false;
        var table = target == TranslateTarget.English ? zhToEn : enToZh;
        var key = target == TranslateTarget.English ? term.Trim() : NormalizeEnglish(term);
        return table.TryGetValue(key, out value!);
    }

    /// <summary>整句直译。一个词都没命中时返回 null，调用方据此判定“离线也无能为力”。</summary>
    public string? Translate(string text, TranslateTarget target) =>
        target == TranslateTarget.English ? TranslateToEnglish(text) : TranslateToChinese(text);

    private string? TranslateToEnglish(string text)
    {
        var result = MatchLongest(text, zhToEn, maxKeyLength, out var matched);
        return matched ? result : null;
    }

    /// <summary>
    /// 英译中：按空格与标点切词，先试双词短语（中间只允许空白）再试单词，
    /// 短语命中时把两个词和中间的空白一起吃掉。
    /// </summary>
    private string? TranslateToChinese(string text)
    {
        var tokens = Tokenize(text);
        var builder = new System.Text.StringBuilder();
        var hit = false;
        var index = 0;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (!token.IsWord)
            {
                builder.Append(token.Text);
                index++;
                continue;
            }

            string? value = null;
            var next = NextWordIndex(tokens, index);
            if (next > 0 && IsSpaceOnly(tokens, index, next) &&
                TryLookup($"{token.Text} {tokens[next].Text}", TranslateTarget.Chinese, out var phrase))
            {
                value = phrase;
                index = next + 1;
            }
            else
            {
                if (TryLookup(token.Text, TranslateTarget.Chinese, out var word)) value = word;
                index++;
            }

            if (value is null)
            {
                builder.Append(token.Text);
                continue;
            }
            hit = true;
            builder.Append(value);
        }
        return hit ? builder.ToString() : null;
    }

    /// <summary>下一个“单词”片段的下标；后面没有词时返回 -1。</summary>
    private static int NextWordIndex(List<Token> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++) if (tokens[i].IsWord) return i;
        return -1;
    }

    /// <summary>两个词之间是否只有空白。夹了标点就不是同一个短语，不做双词匹配。</summary>
    private static bool IsSpaceOnly(List<Token> tokens, int from, int to)
    {
        for (var i = from + 1; i < to; i++)
            if (!string.IsNullOrWhiteSpace(tokens[i].Text)) return false;
        return true;
    }

    /// <summary>
    /// 中译英的最长匹配：遇到汉字时从当前位置开始，尽量吃掉更长的词典词条；
    /// 非汉字字符原样保留，这样数字、标点、英文缩写不会被弄坏。
    /// </summary>
    internal static string MatchLongest(string text, IReadOnlyDictionary<string, string> table, int maxKeyLength, out bool matched)
    {
        var builder = new System.Text.StringBuilder();
        matched = false;
        var index = 0;
        while (index < text.Length)
        {
            var ch = text[index];
            if (!IsCjk(ch))
            {
                builder.Append(ch);
                index++;
                continue;
            }
            var consumed = false;
            var limit = Math.Min(maxKeyLength, text.Length - index);
            for (var length = limit; length >= 1; length--)
            {
                var key = text.Substring(index, length);
                if (table.TryGetValue(key, out var value))
                {
                    AppendWord(builder, value);
                    index += length;
                    consumed = true;
                    matched = true;
                    break;
                }
            }
            if (!consumed)
            {
                builder.Append(ch);
                index++;
            }
        }
        return builder.ToString();

        // 中文没有词间空格，逐词拼出来的英文必须补空格，否则会连写成一长串。
        static void AppendWord(System.Text.StringBuilder builder, string value)
        {
            if (builder.Length > 0 && char.IsLetterOrDigit(builder[^1]) && value.Length > 0 && char.IsLetterOrDigit(value[0]))
                builder.Append(' ');
            builder.Append(value);
        }
    }

    internal readonly record struct Token(string Text, bool IsWord);

    /// <summary>把英文句子切成“单词 / 非单词”两类片段，保留原始标点与空白。</summary>
    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            var isWord = IsWordChar(text[index]);
            while (index < text.Length && IsWordChar(text[index]) == isWord) index++;
            tokens.Add(new Token(text[start..index], isWord));
        }
        return tokens;
    }

    /// <summary>英文单词字符：字母与撇号（it's 这类缩写保持完整）。</summary>
    private static bool IsWordChar(char ch) => char.IsLetter(ch) || ch == '\'';

    private static bool IsCjk(char ch) => (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
