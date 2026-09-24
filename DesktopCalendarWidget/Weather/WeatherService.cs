using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopCalendarWidget.Weather;

/// <summary>
/// 天气服务：把城市名解析成坐标，按优先级找可用数据源，缓存到本地供日历随时取用。
/// 拉取失败时继续用上次的快照（哪怕已经过期），这样断网时日历不会一夜之间全空。
/// </summary>
public sealed class WeatherService
{
    public const string DefaultCity = WidgetSettings.DefaultWeatherCity;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly string cachePath;
    private readonly List<IWeatherProvider> providers;

    /// <summary>同一进程内串行化刷新：缓存文件只有一个，两个实例同时写会互相覆盖。</summary>
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    public WeatherService(string cachePath, IEnumerable<IWeatherProvider>? providers = null)
    {
        this.cachePath = cachePath;
        this.providers = providers?.ToList() ?? [new OpenMeteoWeatherProvider(), new WttrInWeatherProvider()];
    }

    /// <summary>当前可用的天气快照；一次都没成功过时为 null。</summary>
    public WeatherSnapshot? Current { get; private set; }

    /// <summary>按日期索引的日历取数入口。</summary>
    public IReadOnlyDictionary<DateTime, WeatherDay> Map { get; private set; } = new Dictionary<DateTime, WeatherDay>();

    /// <summary>是否正在拉取，UI 用来避免重复触发。</summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// 当前快照是旧数据：要么这次拉取没成功、还在用上次的缓存，要么缓存属于别的城市。
    /// 断网时不能让日历一夜之间全空，所以继续显示旧数据，但要标出来。
    /// </summary>
    public bool IsStale { get; private set; }

    /// <summary>同步载入本地缓存，让首屏直接显示上次的天气，不必等第一次联网刷新。</summary>
    public void LoadFromCache(string? requestedCity = null)
    {
        try
        {
            if (!File.Exists(cachePath)) return;
            var snapshot = ParseCache(File.ReadAllText(cachePath));
            if (snapshot is not null)
            {
                SetCurrent(snapshot);
                IsStale = requestedCity is not null &&
                    !string.Equals(snapshot.City, requestedCity.Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 取天气。<paramref name="force"/> 为 false 时，缓存没过期就直接返回缓存；
    /// 拉取全部失败时回退到旧缓存。返回是否发生了真正的网络更新。
    /// </summary>
    public async Task<bool> LoadAsync(string city, TimeSpan maxAge, bool force, CancellationToken token = default)
    {
        if (IsLoading && !force) return false;
        var wanted = string.IsNullOrWhiteSpace(city) ? DefaultCity : city.Trim();
        var cached = await ReadCacheAsync();
        token.ThrowIfCancellationRequested();
        if (cached is not null && string.Equals(cached.City, wanted, StringComparison.OrdinalIgnoreCase))
        {
            SetCurrent(cached);
            IsStale = false;
            if (!force && DateTime.UtcNow - cached.FetchedAtUtc < maxAge) return false;
        }

        await refreshGate.WaitAsync(token);
        IsLoading = true;
        try
        {
            var (latitude, longitude) = await ResolveCoordinateAsync(wanted, cached, token);
            var query = new WeatherQuery(wanted, latitude, longitude);
            foreach (var provider in providers)
            {
                List<WeatherDay>? days;
                try
                {
                    days = await provider.GetDaysAsync(query, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    continue;
                }
                if (days is null || days.Count == 0) continue;

                var snapshot = new WeatherSnapshot
                {
                    City = wanted,
                    Latitude = latitude ?? 0,
                    Longitude = longitude ?? 0,
                    FetchedAtUtc = DateTime.UtcNow,
                    Provider = provider.Name,
                    Days = days.Where(x => x is not null).ToList()
                };
                token.ThrowIfCancellationRequested();
                await WriteCacheAsync(snapshot);
                token.ThrowIfCancellationRequested();
                SetCurrent(snapshot);
                IsStale = false;
                return true;
            }
            // 全都失败了：继续用旧快照，但标成过期数据，界面上要说明这不是当前城市的实时天气。
            IsStale = Current is not null && Current.Days.Count > 0;
            return false;
        }
        finally
        {
            IsLoading = false;
            refreshGate.Release();
        }
    }

    /// <summary>坐标解析：优先复用同名城市的缓存坐标，其次查地理编码，最后退化为 null（交给支持城市名的数据源）。</summary>
    private async Task<(double? Latitude, double? Longitude)> ResolveCoordinateAsync(
        string city, WeatherSnapshot? cached, CancellationToken token)
    {
        if (cached is not null && string.Equals(cached.City, city, StringComparison.OrdinalIgnoreCase) &&
            (cached.Latitude != 0 || cached.Longitude != 0))
            return (cached.Latitude, cached.Longitude);
        try
        {
            return await GeocodeAsync(city, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Open-Meteo 地理编码接口，纯函数解析部分见 <see cref="ParseGeocode"/>。</summary>
    public static string BuildGeocodeUrl(string city) =>
        $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city.Trim())}&count=1&language=zh&format=json";

    public static (double Latitude, double Longitude)? ParseGeocode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
            var first = results[0];
            if (first.ValueKind != JsonValueKind.Object ||
                !first.TryGetProperty("latitude", out var lat) || lat.ValueKind != JsonValueKind.Number ||
                !first.TryGetProperty("longitude", out var lon) || lon.ValueKind != JsonValueKind.Number) return null;
            return (lat.GetDouble(), lon.GetDouble());
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<(double? Latitude, double? Longitude)> GeocodeAsync(string city, CancellationToken token)
    {
        var json = await WeatherHttp.GetStringAsync(BuildGeocodeUrl(city), token);
        var result = ParseGeocode(json);
        return result is null ? (null, null) : (result.Value.Latitude, result.Value.Longitude);
    }

    private void SetCurrent(WeatherSnapshot snapshot)
    {
        Current = snapshot;
        Map = snapshot.ToMap();
    }

    private async Task<WeatherSnapshot?> ReadCacheAsync()
    {
        try
        {
            if (!File.Exists(cachePath)) return null;
            return ParseCache(await File.ReadAllTextAsync(cachePath));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static WeatherSnapshot? ParseCache(string text)
    {
        var snapshot = JsonSerializer.Deserialize<WeatherSnapshot>(text, JsonOptions);
        if (snapshot?.Days is null) return null;
        snapshot.Days = snapshot.Days.Where(x => x is not null).ToList();
        return snapshot;
    }

    private async Task WriteCacheAsync(WeatherSnapshot snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
