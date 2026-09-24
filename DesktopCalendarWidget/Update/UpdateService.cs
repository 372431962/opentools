using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopCalendarWidget.Update;

/// <summary>
/// 检查更新：向 GitHub 查询本仓库最新的正式版 Release，和当前程序版本比较。
/// 与天气、翻译同样的约定——URL 构造与响应解析都是纯函数，回归测试不联网。
/// </summary>
public sealed class UpdateService
{
    /// <summary>发布所在的 GitHub 仓库；换仓库只改这一处。</summary>
    public const string Repository = "372431962/opentools";

    /// <summary>检查间隔，与设置里的说明文字保持一致。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    /// <summary>Release 页面，取不到安装包时用来手动下载。</summary>
    public static string ReleasePageUrl => $"https://github.com/{Repository}/releases/latest";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>当前程序版本，取不到程序集版本时按 0.0.0 处理（相当于永远提示有新版）。</summary>
    public static Version CurrentVersion
    {
        get
        {
            var informational = typeof(UpdateService).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            // 源链接会往 InformationalVersion 后面追加 +commit，这里只要数字部分。
            var numeric = informational?.Split('+')[0];
            return ParseVersion(numeric) ?? ParseVersion(typeof(UpdateService).Assembly.GetName().Version?.ToString(3)) ?? new Version(0, 0, 0);
        }
    }

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>纯函数：latest 接口地址。</summary>
    public static string BuildLatestUrl() => $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>纯函数：把 v1.5.0 / 1.5 / 1.5.0-beta 都归一成三段式版本，认不出来返回 null。</summary>
    public static Version? ParseVersion(string? text)
    {
        var value = text?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(value)) return null;
        var dash = value.IndexOf('-');
        if (dash >= 0) value = value[..dash];
        var parts = value.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            // 空段（如 "1..3"）与非数字都算解析失败，不能让半截版本号变成 1.0.0。
            if (parts[i].Length == 0 || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        }
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>纯函数：解析 latest Release 响应，取出版本号、说明与 Windows 安装包地址。</summary>
    public static UpdateInfo? ParseLatestRelease(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var version = ParseVersion(StringValue(root, "tag_name") ?? StringValue(root, "name"));
            if (version is null) return null;

            var installerUrl = default(string);
            var installerBytes = default(long);
            var checksumsUrl = default(string);
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                // 安装包与校验文件都要找，因此扫完整个列表才停。
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object) continue;
                    var url = StringValue(asset, "browser_download_url");
                    if (url is null) continue;
                    if (installerUrl is null && url.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = url;
                        installerBytes = asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number
                            ? size.GetInt64()
                            : 0;
                    }
                    else if (checksumsUrl is null && IsChecksumFile(StringValue(asset, "name"), url))
                    {
                        checksumsUrl = url;
                    }
                }
            }

            return new UpdateInfo(
                version,
                StringValue(root, "name") ?? version.ToString(3),
                StringValue(root, "html_url") ?? ReleasePageUrl,
                installerUrl,
                installerBytes,
                checksumsUrl);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>有更新时返回该 Release，已是最新或解析失败返回 null。网络异常向上抛，由调用方提示。</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildLatestUrl());
        HttpSupport.ApplyUserAgent(request);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(token);
        return NewerRelease(json, CurrentVersion);
    }

    internal static UpdateInfo? NewerRelease(string json, Version current)
    {
        var info = ParseLatestRelease(json) ?? throw new InvalidDataException(Loc.UpdateInvalidRelease);
        return info.Version.CompareTo(current) > 0 ? info : null;
    }

    /// <summary>
    /// 下载安装包到 updates 目录，顺带清掉上一次留下的安装包。
    /// 先写 .part 再改名，中断的下载不会留下半截的 .msi。调用方需先确认 <see cref="UpdateInfo.InstallerUrl"/> 存在。
    /// </summary>
    public static async Task<string> DownloadAsync(
        UpdateInfo info, string updatesFolder, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(updatesFolder);
        foreach (var pattern in new[] { "*.msi", "*.part" })
        {
            foreach (var stale in Directory.EnumerateFiles(updatesFolder, pattern))
            {
                try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        var target = Path.Combine(updatesFolder, $"DesktopCalendarWidget-{info.VersionText}-win-x64.msi");
        try
        {
            using var response = await Client.GetAsync(info.InstallerUrl!, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = File.Create(target + ".part"))
            {
                var buffer = new byte[96 * 1024];
                var done = 0L;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    progress?.Report(new DownloadProgress(done, total));
                }
            }
            File.Move(target + ".part", target, overwrite: true);
            await VerifyAsync(info, target, token);
            return target;
        }
        catch
        {
            // 失败时不能留下看起来能双击的安装包，也不保留未完成的下载。
            try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.Delete(target + ".part"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Release 里随安装包一起发布的校验文件。</summary>
    private static bool IsChecksumFile(string? name, string url) =>
        (name is not null && name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) ||
        url.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>纯函数：从 SHA256SUMS.txt 里取某个文件的哈希；没有该条目时返回 null。</summary>
    public static string? FindChecksum(string text, string fileName)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            // 两种常见写法都要认："hash  file" 与 "hash *file"。
            if (string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase)) return parts[0];
        }
        return null;
    }

    /// <summary>
    /// 下载后校验：先比对 GitHub 给的字节数，再按 Release 自带的 SHA256SUMS.txt 校验哈希。
    /// 缺少校验文件、安装包条目或有效哈希都不能继续安装。
    /// </summary>
    private static async Task VerifyAsync(UpdateInfo info, string target, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(info.ChecksumsUrl))
            throw new InvalidDataException(Loc.UpdateVerifyFailed);

        var text = await Client.GetStringAsync(info.ChecksumsUrl, token);
        await VerifyChecksumAsync(info, target, text, token);
    }

    /// <summary>离线校验入口：与网络下载共用同一套大小、条目及 SHA-256 检查。</summary>
    internal static async Task VerifyChecksumAsync(UpdateInfo info, string target, string checksums, CancellationToken token = default)
    {
        if (info.InstallerBytes > 0 && new FileInfo(target).Length != info.InstallerBytes)
            throw new InvalidDataException(Loc.UpdateVerifyFailed);

        var expected = FindChecksum(checksums, Path.GetFileName(target));
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException(Loc.UpdateVerifyFailed);

        var actual = await ComputeSha256Async(target, token);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Loc.UpdateVerifyFailed);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = File.OpenRead(path);
        var buffer = new byte[96 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0) hasher.AppendData(buffer.AsSpan(0, read));
        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static string? StringValue(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>一条更新信息。InstallerUrl 为空表示这个 Release 没有带 Windows 安装包；ChecksumsUrl 为空表示没有校验文件。</summary>
public sealed record UpdateInfo(
    Version Version,
    string Title,
    string ReleaseUrl,
    string? InstallerUrl,
    long InstallerBytes,
    string? ChecksumsUrl = null)
{
    public string VersionText => Version.ToString(3);
}

/// <summary>下载进度，字节数。</summary>
public readonly record struct DownloadProgress(long DownloadedBytes, long TotalBytes)
{
    /// <summary>0~1；服务端没给 Content-Length 时为 null。</summary>
    public double? Ratio => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1) : null;
}
