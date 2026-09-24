using DesktopCalendarWidget;
using DesktopCalendarWidget.Translate;
using DesktopCalendarWidget.Update;
using DesktopCalendarWidget.Weather;

internal static class Program
{
    private static int assertions;
    private static int failures;
    private static int tests;

    private static void Equal<T>(T expected, T actual, string message)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
    }

    private static void True(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Run(string name, Action test)
    {
        tests++;
        try { test(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
    }

    private static int Main()
    {
        // All network samples below are hand-written fixtures, not live responses.
        const string googleSample = """[[["Good weather today.","今天天气很好",null,null,10]],null,"zh-CN"]""";
        const string myMemorySample = """{"responseData":{"translatedText":"It's a nice day."},"responseStatus":200}""";
        const string myMemoryQuota = """{"responseData":{"translatedText":"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY"} ,"responseStatus":200}""";
        const string openMeteoSample =
            """{"latitude":39.89,"daily":{"time":["2026-09-21","2026-09-22","2026-09-23"],"weather_code":[2,61,3],"temperature_2m_max":[28.4,19.2,21.0],"temperature_2m_min":[18.1,12.3,null],"precipitation_probability_max":[10,80,null]}}""";
        const string wttrSample =
            """{"weather":[{"date":"2026-09-21","maxtempC":"28","mintempC":"18","hourly":[{"weatherDesc":[{"value":"Partly cloudy"}]}]}]}""";
        const string geocodeSample =
            """{"results":[{"id":1,"name":"北京","latitude":39.9075,"longitude":116.39723}],"generationtime_ms":0.01}""";

        // ---- 语种识别 ----
        Run("detect pure chinese", () => Equal(TextLanguage.Chinese, LanguageDetector.Detect("今天天气很好"), "language"));
        Run("detect pure english", () => Equal(TextLanguage.English, LanguageDetector.Detect("good morning"), "language"));
        Run("detect mixed", () => Equal(TextLanguage.Mixed, LanguageDetector.Detect("今天 weather 很好 ok"), "language"));
        Run("detect digits and punctuation only", () => Equal(TextLanguage.Empty, LanguageDetector.Detect("123 456 ！！"), "language"));
        Run("detect empty", () => Equal(TextLanguage.Empty, LanguageDetector.Detect("   "), "language"));
        Run("target of chinese is english", () => Equal(TranslateTarget.English, LanguageDetector.TargetOf("今天"), "target"));
        Run("target of english is chinese", () => Equal(TranslateTarget.Chinese, LanguageDetector.TargetOf("today"), "target"));
        Run("target of digits is refused", () => Equal(null, LanguageDetector.TargetOf("1024"), "target"));

        // ---- 离线词典 ----
        var dictionary = TranslateDictionary.FromEntries(new Dictionary<string, string>
        {
            ["春"] = "spring",
            ["春节"] = "Spring Festival",
            ["今天"] = "today",
            ["天气"] = "weather",
            ["好"] = "good",
            ["Spring Festival"] = "春节",
            ["good"] = "好"
        });
        Run("dictionary prefers the longest entry", () =>
            Equal("Spring Festival", dictionary.Translate("春节", TranslateTarget.English), "zh to en"));
        Run("dictionary keeps unknown characters", () =>
            // 输入里的全角感叹号原样保留，这里也按全角断言。
            Equal("today weather good！", dictionary.Translate("今天天气好！", TranslateTarget.English), "zh to en"));
        Run("dictionary translates a phrase before single words", () =>
            Equal("春节", dictionary.Translate("Spring Festival", TranslateTarget.Chinese), "en to zh"));
        Run("dictionary keeps untranslated words in place", () =>
            Equal("好 xyz", dictionary.Translate("good xyz", TranslateTarget.Chinese), "en to zh"));
        Run("normalize english folds whitespace and case", () =>
            Equal("spring festival", TranslateDictionary.NormalizeEnglish("  Spring   Festival "), "normalize"));

        var embedded = TranslateDictionary.LoadEmbedded();
        Run("embedded dictionary is packaged", () => True(embedded.Count > 500, $"embedded count={embedded.Count}"));
        Run("embedded dictionary covers calendar terms", () =>
            True(embedded.TryLookup("春节", TranslateTarget.English, out var value) && value.Length > 0, "春节 missing"));
        Run("embedded dictionary translates in both directions", () =>
            True(embedded.Translate("国庆节", TranslateTarget.English)!.Length > 0, "zh to en"));
        var offline = new OfflineDictionaryProvider(embedded);
        Run("offline provider returns null when nothing matches", () =>
            Equal(null, offline.TranslateAsync(new TranslateRequest("zzzzz", TranslateTarget.Chinese), default).Result, "no match"));

        // ---- 在线接口的 URL 构造与响应解析（不联网） ----
        Run("google url escapes text", () =>
            True(GoogleTranslateProvider.BuildUrl("今天 天气", TranslateTarget.English).Contains("q=%E4%BB%8A%E5%A4%A9%20", StringComparison.Ordinal), "escape"));
        Run("google url picks target language", () =>
            True(GoogleTranslateProvider.BuildUrl("hi", TranslateTarget.Chinese).Contains("tl=zh-CN", StringComparison.Ordinal), "target"));
        Run("google response joins segments", () =>
            Equal("Good weather today.", GoogleTranslateProvider.ParseResponse(googleSample), "parse"));
        Run("google response rejects junk", () =>
            Equal(null, GoogleTranslateProvider.ParseResponse("{\"a\":1}"), "parse"));
        Run("mymemory url uses language pair", () =>
            True(MyMemoryTranslateProvider.BuildUrl("hi", TranslateTarget.English).Contains("langpair=zh-CN%7Cen", StringComparison.Ordinal), "pair"));
        Run("mymemory response returns text", () =>
            Equal("It's a nice day.", MyMemoryTranslateProvider.ParseResponse(myMemorySample), "parse"));
        Run("mymemory quota message counts as failure", () =>
            Equal(null, MyMemoryTranslateProvider.ParseResponse(myMemoryQuota), "parse"));

        // ---- 翻译缓存 ----
        Run("cache stores and returns by direction", () =>
        {
            var cache = new TranslateCache();
            var zhToEn = new TranslateRequest("今天", TranslateTarget.English);
            cache.Set(zhToEn, "today");
            True(cache.TryGet(zhToEn, out var hit), "miss");
            Equal("today", hit, "value");
            True(!cache.TryGet(new TranslateRequest("今天", TranslateTarget.Chinese), out _), "direction leaked");
        });
        Run("cache evicts the oldest entry", () =>
        {
            var cache = new TranslateCache();
            for (var i = 0; i < TranslateCache.MemoryCapacity + 10; i++)
                cache.Set(new TranslateRequest($"w{i}", TranslateTarget.English), $"t{i}");
            True(!cache.TryGet(new TranslateRequest("w0", TranslateTarget.English), out _), "oldest should be evicted");
            True(cache.TryGet(new TranslateRequest($"w{TranslateCache.MemoryCapacity + 9}", TranslateTarget.English), out _), "newest should survive");
        });
        Run("cache survives a disk round trip", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "translate-cache-roundtrip.json");
            try
            {
                File.Delete(path);
                var writer = new TranslateCache(path);
                writer.Set(new TranslateRequest("今天", TranslateTarget.English), "today");
                writer.FlushToDisk();
                var reader = new TranslateCache(path);
                True(reader.TryGet(new TranslateRequest("今天", TranslateTarget.English), out var hit), "reload miss");
                Equal("today", hit, "reload value");
            }
            finally { File.Delete(path); }
        });

        // ---- 翻译编排 ----
        Run("auto provider order keeps online first then offline", () =>
        {
            var service = new TranslateService(null, embedded);
            var chain = service.SelectProviders(TranslateService.SettingAuto, true);
            Equal(3, chain.Count, "chain length");
            Equal(TranslateSource.OfflineDictionary, chain[^1].Source, "offline last");
        });
        Run("explicit offline setting skips online providers", () =>
        {
            var service = new TranslateService(null, embedded);
            var chain = service.SelectProviders(TranslateService.SettingOffline, true);
            Equal(1, chain.Count, "chain length");
            Equal(TranslateSource.OfflineDictionary, chain[0].Source, "only offline");
        });
        Run("failed provider falls through to the next", () =>
        {
            var service = new TranslateService(null, embedded, [new FailingProvider(), new FakeProvider("ok")]);
            var outcome = service.TranslateAsync("今天", TranslateService.SettingAuto, false).Result;
            Equal(TranslateStatus.Ok, outcome.Status, "status");
            Equal("ok", outcome.Text, "text");
        });
        Run("offline fallback marks the result as offline", () =>
        {
            var service = new TranslateService(null, embedded, [new FailingProvider()]);
            var outcome = service.TranslateAsync("春节", TranslateService.SettingAuto, true).Result;
            Equal(TranslateStatus.Ok, outcome.Status, "status");
            Equal(true, outcome.IsOffline, "offline flag");
        });
        Run("all providers failing without fallback reports failure", () =>
        {
            var service = new TranslateService(null, embedded, [new FailingProvider()]);
            Equal(TranslateStatus.Failed, service.TranslateAsync("春节", TranslateService.SettingAuto, false).Result.Status, "status");
        });
        Run("empty input is refused without touching providers", () =>
            Equal(TranslateStatus.Empty, new TranslateService(null, embedded, [new FakeProvider("x")]).TranslateAsync("  ", TranslateService.SettingAuto, true).Result.Status, "status"));
        Run("digits only input is refused", () =>
            Equal(TranslateStatus.Unsupported, new TranslateService(null, embedded, [new FakeProvider("x")]).TranslateAsync("2026", TranslateService.SettingAuto, true).Result.Status, "status"));
        Run("oversized input is truncated and chunked", () =>
        {
            LengthRecordingProvider.Reset();
            var service = new TranslateService(null, embedded, [new LengthRecordingProvider()]);
            var outcome = service.TranslateAsync(new string('好', TranslateService.MaxLength + 50), TranslateService.SettingAuto, false).Result;
            Equal(true, outcome.Truncated, "truncated flag");
            Equal(TranslateService.MaxLength, LengthRecordingProvider.TotalLength, "total length");
            True(LengthRecordingProvider.MaxLengthSeen <= TranslateHttp.ChunkLength, "chunk too large");
        });
        Run("translation direction follows truncated text", () =>
        {
            LengthRecordingProvider.Reset();
            var service = new TranslateService(null, embedded, [new LengthRecordingProvider()]);
            var outcome = service.TranslateAsync(new string('a', TranslateService.MaxLength) + new string('好', TranslateService.MaxLength + 1),
                TranslateService.SettingAuto, false).Result;
            Equal(TranslateTarget.Chinese, outcome.Target, "direction of submitted text");
            Equal(TranslateTarget.Chinese, LengthRecordingProvider.TargetSeen, "provider direction");
        });
        Run("second call is served from cache", () =>
        {
            var provider = new CountingProvider();
            var service = new TranslateService(null, embedded, [provider]);
            service.TranslateAsync("今天", TranslateService.SettingAuto, false).Wait();
            var outcome = service.TranslateAsync("今天", TranslateService.SettingAuto, false).Result;
            Equal(true, outcome.FromCache, "cache flag");
            Equal(1, provider.Calls, "provider should be called once");
        });

        // ---- 天气：编码映射与响应解析 ----
        Run("wmo codes map to conditions", () =>
        {
            Equal(WeatherCondition.Clear, WeatherCodes.FromWmo(0), "0");
            Equal(WeatherCondition.PartlyCloudy, WeatherCodes.FromWmo(2), "2");
            Equal(WeatherCondition.Fog, WeatherCodes.FromWmo(45), "45");
            Equal(WeatherCondition.Drizzle, WeatherCodes.FromWmo(53), "53");
            Equal(WeatherCondition.HeavyRain, WeatherCodes.FromWmo(65), "65");
            Equal(WeatherCondition.Snow, WeatherCodes.FromWmo(73), "73");
            Equal(WeatherCondition.Thunderstorm, WeatherCodes.FromWmo(95), "95");
            Equal(WeatherCondition.Unknown, WeatherCodes.FromWmo(-1), "unknown");
        });
        Run("descriptions map to conditions", () =>
        {
            Equal(WeatherCondition.Thunderstorm, WeatherCodes.FromDescription("Thundery outbreaks possible"), "thunder");
            Equal(WeatherCondition.Snow, WeatherCodes.FromDescription("Light snow"), "snow");
            Equal(WeatherCondition.HeavyRain, WeatherCodes.FromDescription("Heavy rain"), "heavy rain");
            Equal(WeatherCondition.Drizzle, WeatherCodes.FromDescription("Light drizzle"), "drizzle");
            Equal(WeatherCondition.PartlyCloudy, WeatherCodes.FromDescription("Partly cloudy"), "partly");
            Equal(WeatherCondition.Overcast, WeatherCodes.FromDescription("Overcast"), "overcast");
            Equal(WeatherCondition.Clear, WeatherCodes.FromDescription("Sunny"), "sunny");
            Equal(WeatherCondition.Unknown, WeatherCodes.FromDescription(""), "empty");
        });
        Run("open-meteo url requests daily fields", () =>
        {
            var url = OpenMeteoWeatherProvider.BuildUrl(39.9075, 116.39723);
            True(url.Contains("weather_code", StringComparison.Ordinal), "weather_code");
            True(url.Contains("temperature_2m_max", StringComparison.Ordinal), "max");
            True(url.Contains("latitude=39.9075", StringComparison.Ordinal), "latitude");
        });
        Run("open-meteo response parses days", () =>
        {
            var days = OpenMeteoWeatherProvider.ParseResponse(openMeteoSample);
            True(days is not null && days.Count == 3, "count");
            Equal(WeatherCondition.PartlyCloudy, days![0].Condition, "first condition");
            Equal(28.4, days[0].TempMax, "first max");
            Equal(10, days[0].PrecipitationProbability, "first precip");
            Equal(false, days[2].HasTemperature, "missing temperature");
            Equal(null, days[2].PrecipitationProbability, "missing precip");
        });
        Run("open-meteo response rejects junk", () =>
            Equal(null, OpenMeteoWeatherProvider.ParseResponse("{\"daily\":{}}"), "parse"));
        Run("wttr response parses days", () =>
        {
            var days = WttrInWeatherProvider.ParseResponse(wttrSample);
            True(days is not null && days.Count == 1, "count");
            Equal(WeatherCondition.PartlyCloudy, days![0].Condition, "condition");
            Equal(28.0, days[0].TempMax, "max");
            Equal(18.0, days[0].TempMin, "min");
        });
        Run("wttr url escapes the city", () =>
            True(WttrInWeatherProvider.BuildUrl("纽约 北京").Contains("%20", StringComparison.Ordinal), "escape"));
        Run("geocode response parses coordinates", () =>
        {
            var result = WeatherService.ParseGeocode(geocodeSample);
            True(result is not null, "null");
            Equal(39.9075, result!.Value.Latitude, "latitude");
            Equal(116.39723, result.Value.Longitude, "longitude");
        });
        Run("geocode response rejects an empty result", () =>
            Equal(null, WeatherService.ParseGeocode("{\"results\":[]}"), "parse"));
        Run("snapshot map keys by date", () =>
        {
            var snapshot = new WeatherSnapshot { Days = [new WeatherDay { Date = "2026-09-21", TempMax = 1, TempMin = 0 }] };
            True(snapshot.ToMap().ContainsKey(new DateTime(2026, 9, 21)), "key");
        });
        Run("startup weather cache warns when city differs", () =>
        {
            var path = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? AppContext.BaseDirectory,
                $"weather-startup-{Guid.NewGuid():N}.json");
            try
            {
                var snapshot = new WeatherSnapshot { City = "上海", Days = [new WeatherDay { Date = "2026-09-21", TempMax = 20, TempMin = 10 }] };
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(snapshot));
                var service = new WeatherService(path, []);
                service.LoadFromCache("北京");
                True(service.IsStale, "old city's cached weather must be marked stale on first render");
                service.LoadFromCache("上海");
                True(!service.IsStale, "matching city's cache stays fresh");
            }
            finally { File.Delete(path); }
        });
        Run("disk cache retains five hundred entries across flushes", () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "translate-cache-capacity.json");
            try
            {
                File.Delete(path);
                var cache = new TranslateCache(path);
                for (var batch = 0; batch < 3; batch++)
                {
                    for (var i = batch * 200; i < (batch + 1) * 200; i++)
                        cache.Set(new TranslateRequest($"disk{i}", TranslateTarget.English), $"value{i}");
                    cache.FlushToDisk();
                }
                var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                Equal(TranslateCache.DiskCapacity, json!.Count, "disk capacity");
                var reloaded = new TranslateCache(path);
                True(reloaded.TryGet(new TranslateRequest("disk100", TranslateTarget.English), out var oldValue), "disk fallback miss");
                Equal("value100", oldValue, "disk fallback value");
                True(!reloaded.TryGet(new TranslateRequest("disk0", TranslateTarget.English), out _), "oldest should be evicted from disk");
            }
            finally { File.Delete(path); }
        });
        Run("weather snapshot ignores null entries", () =>
        {
            var snapshot = new WeatherSnapshot
            {
                Days = [null!, new WeatherDay { Date = "2026-09-21", TempMax = 1, TempMin = 0 }]
            };
            Equal(1, snapshot.ToMap().Count, "map count");
        });
        Run("weather defaults missing temperatures to unavailable", () =>
            Equal(false, new WeatherDay { Date = "2026-09-21" }.HasTemperature, "temperature"));
        Run("weather parsers reject wrong field types", () =>
        {
            Equal(null, OpenMeteoWeatherProvider.ParseResponse("{\"daily\":{\"time\":[123]}}"), "open-meteo wrong type");
            Equal(null, WttrInWeatherProvider.ParseResponse("{\"weather\":[{\"date\":123}]}"), "wttr wrong type");
        });
        Run("small displays clamp window dimensions and visible position", () =>
        {
            Equal(600d, WindowPlacement.ClampSize(820, 720, 600), "height below the usual minimum");
            Equal(560d, WindowPlacement.ClampSize(400, 560, 800), "width minimum");
            Equal(0d, WindowPlacement.ClampPosition(80, 0, 600, 600, 80), "full-height window origin");
            Equal(148d, WindowPlacement.ClampPosition(1000, 48, 800, 700, 80), "offscreen position");
            Equal(-900d, WindowPlacement.ClampPosition(double.NaN, -900, 600, 600, 80), "secondary monitor origin");
        });
        Run("forced weather refresh waits for an active request", () =>
            ForcedWeatherRefreshAsync().GetAwaiter().GetResult());
        Run("update url is built from the repository constant", () =>
            Equal($"https://api.github.com/repos/{UpdateService.Repository}/releases/latest",
                UpdateService.BuildLatestUrl(), "latest url"));
        Run("update parser picks the msi asset and ignores others", () =>
        {
            var info = UpdateService.ParseLatestRelease("""
                {"tag_name":"v1.6.0","name":"DesktopCalendarWidget 1.6.0","html_url":"https://github.com/x/y/releases/tag/v1.6.0",
                 "assets":[{"name":"notes.txt","browser_download_url":"https://example.test/notes.txt"},
                           {"name":"app-win-x64.msi","browser_download_url":"https://example.test/app.msi","size":2097152}]}
                """);
            True(info is not null, "parsed");
            Equal("1.6.0", info!.VersionText, "version text");
            Equal("DesktopCalendarWidget 1.6.0", info.Title, "title");
            Equal("https://example.test/app.msi", info.InstallerUrl, "installer url");
            Equal(2097152L, info.InstallerBytes, "installer bytes");
        });
        Run("update parser keeps releases without an installer", () =>
        {
            var info = UpdateService.ParseLatestRelease("""{"tag_name":"1.7.0","assets":[]}""");
            True(info is not null, "parsed");
            Equal(null, info!.InstallerUrl, "no installer");
            Equal(UpdateService.ReleasePageUrl, info.ReleaseUrl, "release url fallback");
            Equal("1.7.0", info.Title, "title falls back to version");
        });
        Run("update parser rejects malformed releases", () =>
        {
            foreach (var json in new[] { "[]", "{}", "{", """{"tag_name":"vabc"}""", """{"tag_name":1.6}""", """{"assets":"msi"}""" })
                Equal(null, UpdateService.ParseLatestRelease(json), "junk release");
        });
        Run("invalid latest release is a failed check, not up to date", () =>
        {
            Equal(null, UpdateService.NewerRelease("{\"tag_name\":\"v1.5.0\"}", new Version(1, 5, 1)), "older release");
            Equal("1.6.0", UpdateService.NewerRelease("{\"tag_name\":\"v1.6.0\"}", new Version(1, 5, 1))!.VersionText, "new release");
            try
            {
                UpdateService.NewerRelease("{\"tag_name\":\"broken\"}", new Version(1, 5, 1));
                throw new InvalidOperationException("unparseable release treated as up to date");
            }
            catch (InvalidDataException) { assertions++; }
        });
        Run("update version parser normalizes tags", () =>
        {
            Equal("1.6.0", UpdateService.ParseVersion("v1.6.0")!.ToString(3), "v prefix");
            Equal("1.6.0", UpdateService.ParseVersion(" 1.6 ")!.ToString(3), "two segments pad");
            Equal("1.6.0", UpdateService.ParseVersion("1.6.0-beta")!.ToString(3), "prerelease suffix");
            Equal(null, UpdateService.ParseVersion("1..6"), "empty segment");
            Equal(null, UpdateService.ParseVersion("1.6.0.0.0"), "too many segments");
            Equal(null, UpdateService.ParseVersion(null), "null");
        });
        Run("current version is read from the assembly", () =>
        {
            var current = UpdateService.CurrentVersion;
            True(current.Major > 0, $"major={current.Major}");
            Equal(current.ToString(3), UpdateService.CurrentVersionText, "version text");
        });
        Run("download ratio handles unknown content length", () =>
        {
            Equal(0.5, new DownloadProgress(50, 100).Ratio, "halfway");
            Equal(null, new DownloadProgress(50, 0).Ratio, "no total");
        });
        Run("checksum file lookup handles both formats", () =>
        {
            const string sums = "abc123  DesktopCalendarWidget-1.6.0-win-x64.msi\nDEF456 *other.zip\n";
            Equal("abc123", UpdateService.FindChecksum(sums, "DesktopCalendarWidget-1.6.0-win-x64.msi"), "plain entry");
            Equal("DEF456", UpdateService.FindChecksum(sums, "other.zip"), "starred entry");
            Equal(null, UpdateService.FindChecksum(sums, "missing.msi"), "absent entry");
        });
        Run("installer checksum verification fails closed", () =>
        {
            var path = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? AppContext.BaseDirectory,
                $"DesktopCalendarWidget-{Guid.NewGuid():N}.msi");
            var bytes = new byte[] { 10, 20, 30, 40 };
            File.WriteAllBytes(path, bytes);
            try
            {
                var info = new UpdateInfo(new Version(1, 6, 0), "", "", "", bytes.Length, "https://example.test/SHA256SUMS.txt");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                void Verify(UpdateInfo release, string sums) => UpdateService.VerifyChecksumAsync(release, path, sums).GetAwaiter().GetResult();
                void Reject(UpdateInfo release, string sums)
                {
                    try { Verify(release, sums); throw new InvalidOperationException("unverified installer accepted"); }
                    catch (InvalidDataException) { assertions++; }
                }
                Verify(info, $"{hash}  {Path.GetFileName(path)}");
                Reject(info, "");
                Reject(info, $"abc123  {Path.GetFileName(path)}");
                Reject(info, $"{new string('0', 64)}  {Path.GetFileName(path)}");
                Reject(info with { InstallerBytes = bytes.Length + 1 }, $"{hash}  {Path.GetFileName(path)}");
            }
            finally { File.Delete(path); }
        });
        Run("release parser also collects the checksum asset", () =>
        {
            var info = UpdateService.ParseLatestRelease("""
                {"tag_name":"v1.6.0","assets":[
                   {"name":"app-win-x64.msi","browser_download_url":"https://example.test/app.msi","size":1024},
                   {"name":"SHA256SUMS.txt","browser_download_url":"https://example.test/SHA256SUMS.txt"}]}
                """);
            Equal("https://example.test/app.msi", info!.InstallerUrl, "installer");
            Equal("https://example.test/SHA256SUMS.txt", info.ChecksumsUrl, "checksums");
        });

        // ---- 节假日解析 ----
        Run("holiday parser reads timor style payload", () =>
        {
            var list = HolidayService.Parse(System.Text.Json.JsonDocument.Parse("""
                {"code":0,"holiday":{
                  "2026-10-01":{"holiday":true,"name":"国庆节","date":"2026-10-01"},
                  "2026-09-26":{"holiday":false,"name":"国庆节补班","date":"2026-09-26","target":"国庆节"}}}
                """).RootElement);
            Equal(2, list.Count, "count");
            var off = list.Single(x => x.Date == "2026-10-01");
            Equal(true, off.IsHoliday, "holiday flag");
            Equal(false, off.IsWorkday, "not a workday");
            var work = list.Single(x => x.Date == "2026-09-26");
            Equal(true, work.IsWorkday, "workday flag");
            Equal(false, work.IsHoliday, "workday is not a holiday");
            Equal("国庆节调休", work.Name, "workday name from target");
        });
        Run("holiday parser prefers workday on duplicate dates", () =>
        {
            var list = HolidayService.Parse(System.Text.Json.JsonDocument.Parse("""
                [{"date":"2026-05-01","name":"劳动节","isHoliday":true},
                 {"date":"2026-05-01","name":"劳动节补班","isWorkday":true}]
                """).RootElement);
            Equal(1, list.Count, "deduplicated");
            Equal(true, list[0].IsWorkday, "workday wins");
        });
        Run("holiday parser drops unusable records", () =>
        {
            var list = HolidayService.Parse(System.Text.Json.JsonDocument.Parse("""
                {"holiday":{"a":{"holiday":true,"name":"x","date":"not-a-date"},
                            "b":{"holiday":true,"name":"y"}}}
                """).RootElement);
            Equal(0, list.Count, "all dropped");
        });

        // ---- 配置迁移 ----
        Run("legacy settings migrate to weekly rest", () =>
        {
            var legacy = new WidgetSettings { SingleRestDay = SingleRestDay.Sunday };
            SettingsService.Migrate(legacy);
            Equal(RestPattern.Weekly, legacy.RestPattern, "pattern");
            Equal((int?)WidgetSettings.CurrentSettingsVersion, legacy.SettingsVersion, "version stamped");
        });
        Run("legacy settings without a rest day stay unmarked", () =>
        {
            var legacy = new WidgetSettings { SingleRestDay = SingleRestDay.None };
            SettingsService.Migrate(legacy);
            Equal(RestPattern.None, legacy.RestPattern, "pattern");
        });
        Run("already migrated settings are left untouched", () =>
        {
            var current = new WidgetSettings
            {
                SettingsVersion = WidgetSettings.CurrentSettingsVersion,
                SingleRestDay = SingleRestDay.Saturday
            };
            SettingsService.Migrate(current);
            Equal(RestPattern.None, current.RestPattern, "pattern kept");
        });
        Run("copy from keeps the same reference and transfers values", () =>
        {
            var target = new WidgetSettings();
            target.CopyFrom(new WidgetSettings { Width = 700, WeatherCity = "上海" });
            Equal(700d, target.Width, "width");
            Equal("上海", target.WeatherCity, "city");
        });
        Run("legacy opacity field is ignored while other settings load", () =>
        {
            var restored = System.Text.Json.JsonSerializer.Deserialize<WidgetSettings>(
                """{"Opacity":0.5,"Width":700,"WeatherCity":"上海"}""");
            True(restored is not null, "settings loaded");
            Equal(700d, restored!.Width, "width");
            Equal("上海", restored.WeatherCity, "city");
        });
        Run("copy from transfers every persisted field", () =>
        {
            // 配置字段全是可写属性；任何一个加进 WidgetSettings 却忘了加进 CopyFrom 的字段，
            // 在这里都会表现为「拷不过去」。
            static object Marker(string name, Type type)
            {
                if (type == typeof(string)) return "marker-" + name;
                if (type == typeof(bool)) return true;
                if (type == typeof(int)) return 987654;
                if (type == typeof(int?)) return 987654;
                if (type == typeof(double)) return 0.314;
                if (type == typeof(DateTime) || type == typeof(DateTime?))
                    return new DateTime(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
                if (type.IsEnum)
                {
                    var values = Enum.GetValues(type);
                    return values.GetValue(values.Length - 1)!;
                }
                throw new InvalidOperationException("unhandled type " + type);
            }
            var props = typeof(WidgetSettings).GetProperties()
                .Where(p => p.CanWrite && p.SetMethod is { IsPublic: true } &&
                            p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false).Length == 0)
                .ToList();
            True(props.Count >= 28, $"expected the persisted fields, got {props.Count}");
            var source = new WidgetSettings();
            var target = new WidgetSettings();
            foreach (var p in props) p.SetValue(source, Marker(p.Name, p.PropertyType));
            target.CopyFrom(source);
            foreach (var p in props)
            {
                var expected = p.GetValue(source);
                var actual = p.GetValue(target);
                True(Equals(expected, actual), $"{p.Name} not copied: expected={expected} actual={actual}");
            }
        });

        // ---- 农历 ----
        Run("settings edit preserves update state written while dialog was open", () =>
        {
            var live = new WidgetSettings { WeatherCity = "北京" };
            var editor = live.Clone();
            editor.WeatherCity = "上海";
            var checkedAt = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            live.LastUpdateCheckUtc = checkedAt;
            live.SkippedUpdateVersion = "1.6.0";
            live.ApplyEditedSettings(editor);
            Equal("上海", live.WeatherCity, "edited preference");
            Equal(checkedAt, live.LastUpdateCheckUtc, "fresh check timestamp");
            Equal("1.6.0", live.SkippedUpdateVersion, "skip choice");
        });
        Run("lunar converter labels the new year day", () =>
        {
            Equal("正月", LunarCalendarConverter.Format(new DateTime(2026, 2, 17)), "first day of the first month");
            Equal("初二", LunarCalendarConverter.Format(new DateTime(2026, 2, 18)), "second day");
        });
        Run("lunar stem branch follows the sixty year cycle", () =>
        {
            Equal("乙巳", LunarCalendarConverter.StemBranchOf(2025), "2025");
            Equal("丙午", LunarCalendarConverter.StemBranchOf(2026), "2026");
            Equal("甲子", LunarCalendarConverter.StemBranchOf(1984), "cycle start");
            Equal("", LunarCalendarConverter.StemBranchOf(3), "before the epoch");
        });
        Run("lunar year label is built from the stem branch", () =>
        {
            // 测试工程没有嵌入 resx，Loc 会退回键名，这里只确认标签能算出来且不抛异常。
            True(LunarCalendarConverter.GetYearLabel(new DateTime(2026, 1, 15)).Length > 0, "before spring festival");
            True(LunarCalendarConverter.GetYearLabel(new DateTime(2026, 6, 1)).Length > 0, "after spring festival");
        });
        Run("lunar converter handles out-of-range dates", () =>
        {
            Equal("", LunarCalendarConverter.Format(new DateTime(1900, 1, 1)), "too early");
            Equal("", LunarCalendarConverter.GetYearLabel(new DateTime(1900, 1, 1)), "too early label");
        });

        Console.WriteLine($"{tests} tests, {assertions} assertions, {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static async Task ForcedWeatherRefreshAsync()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("PI_SCRATCH_DIR") ?? AppContext.BaseDirectory,
            $"weather-refresh-{Guid.NewGuid():N}.json");
        var provider = new BlockingWeatherProvider();
        try
        {
            var snapshot = new WeatherSnapshot
            {
                City = "北京", Latitude = 39.9, Longitude = 116.4, FetchedAtUtc = DateTime.UtcNow,
                Provider = "Cached", Days = [new WeatherDay { Date = "2026-09-21", TempMax = 20, TempMin = 10 }]
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(snapshot));
            var service = new WeatherService(path, [provider]);
            var first = service.LoadAsync("北京", TimeSpan.Zero, force: true);
            await provider.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));
            var second = service.LoadAsync("北京", TimeSpan.Zero, force: true);
            provider.ReleaseFirst();
            True(await first, "initial refresh");
            True(await second, "forced request must run after initial refresh");
            Equal(2, provider.Calls, "both requests reached provider");
        }
        finally
        {
            provider.ReleaseFirst();
            File.Delete(path);
        }
    }

    private sealed class BlockingWeatherProvider : IWeatherProvider
    {
        private readonly TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "Test";
        public int Calls { get; private set; }
        public Task FirstStarted => firstStarted.Task;
        public void ReleaseFirst() => release.TrySetResult();
        public async Task<List<WeatherDay>?> GetDaysAsync(WeatherQuery query, CancellationToken token)
        {
            Calls++;
            if (Calls == 1)
            {
                firstStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return [new WeatherDay { Date = "2026-09-21", TempMax = 20, TempMin = 10 }];
        }
    }

    private sealed class FakeProvider(string text) : ITranslateProvider
    {
        public string Name => "Fake";
        public TranslateSource Source => TranslateSource.Google;
        public Task<string?> TranslateAsync(TranslateRequest request, CancellationToken token) => Task.FromResult<string?>(text);
    }

    private sealed class FailingProvider : ITranslateProvider
    {
        public string Name => "Failing";
        public TranslateSource Source => TranslateSource.MyMemory;
        public Task<string?> TranslateAsync(TranslateRequest request, CancellationToken token) =>
            throw new HttpRequestException("fixture failure");
    }

    private sealed class CountingProvider : ITranslateProvider
    {
        public int Calls { get; private set; }
        public string Name => "Counting";
        public TranslateSource Source => TranslateSource.Google;
        public Task<string?> TranslateAsync(TranslateRequest request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult<string?>("counted");
        }
    }

    private sealed class LengthRecordingProvider : ITranslateProvider
    {
        public static int TotalLength;
        public static int MaxLengthSeen;
        public static TranslateTarget? TargetSeen;
        public string Name => "Length";
        public TranslateSource Source => TranslateSource.Google;
        public static void Reset() { TotalLength = 0; MaxLengthSeen = 0; TargetSeen = null; }
        public Task<string?> TranslateAsync(TranslateRequest request, CancellationToken token)
        {
            TotalLength += request.Text.Length;
            MaxLengthSeen = Math.Max(MaxLengthSeen, request.Text.Length);
            TargetSeen = request.Target;
            return Task.FromResult<string?>("x");
        }
    }
}
