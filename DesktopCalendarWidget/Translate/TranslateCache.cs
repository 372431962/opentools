using System.IO;
using System.Text.Json;

namespace DesktopCalendarWidget.Translate;

/// <summary>
/// 翻译缓存。内存 LRU 用于即时命中，磁盘文件用于重启后仍能查到最近词条（也是离线兜底的补充）。
/// 缓存键包含方向，避免“bank→岸”和“岸→bank”互相污染。
/// </summary>
public sealed class TranslateCache
{
    public const int MemoryCapacity = 200;
    public const int DiskCapacity = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>最近命中在尾部，超出容量淘汰头部。</summary>
    private readonly LinkedList<(string Key, string Value)> order = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, string Value)>> nodes = new(StringComparer.Ordinal);

    /// <summary>
    /// 磁盘内容的常驻索引。翻译是交互式场景，每次未命中都去读一遍文件并反序列化会拖慢输入；
    /// 500 条字符串留在内存里代价很小，换来的是查缓存不再碰磁盘。
    /// </summary>
    private readonly Dictionary<string, string> diskIndex = new(StringComparer.Ordinal);
    private readonly string? filePath;
    private bool dirty;
    private long version;

    /// <param name="filePath">传 null 表示纯内存缓存（测试用）。</param>
    public TranslateCache(string? filePath = null)
    {
        this.filePath = filePath;
        LoadFromDisk();
    }

    public static string BuildKey(TranslateRequest request) => $"{(int)request.Target}|{request.Text}";

    public bool TryGet(TranslateRequest request, out string value)
    {
        value = "";
        var key = BuildKey(request);
        lock (order)
        {
            if (nodes.TryGetValue(key, out var node))
            {
                order.Remove(node);
                order.AddLast(node);
                value = node.Value.Value;
                return true;
            }
        }

        // 内存 LRU 只保留 200 条；其余最近 500 条从常驻的磁盘索引里按需提升（不再读文件）。
        lock (order)
        {
            if (!diskIndex.TryGetValue(key, out var diskValue)) return false;
            AddOrMoveToEnd(key, diskValue);
            TrimMemory();
            value = diskValue;
            return true;
        }
    }

    public void Set(TranslateRequest request, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var key = BuildKey(request);
        lock (order)
        {
            AddOrMoveToEnd(key, value);
            TrimMemory();
            version++;
            dirty = true;
        }
    }

    /// <summary>
    /// 把内存内容合并进磁盘最近 500 条。这样内存只保留 200 条，重启后磁盘仍能覆盖更长的历史。
    /// 写失败不影响翻译功能。
    /// </summary>
    public void FlushToDisk()
    {
        if (filePath is null) return;
        List<(string Key, string Value)> memorySnapshot;
        long snapshotVersion;
        lock (order)
        {
            if (!dirty) return;
            memorySnapshot = order.ToList();
            snapshotVersion = version;
        }

        try
        {
            List<KeyValuePair<string, string>> disk;
            lock (order) disk = diskIndex.ToList();
            var merged = new LinkedList<(string Key, string Value)>();
            var mergedNodes = new Dictionary<string, LinkedListNode<(string Key, string Value)>>(StringComparer.Ordinal);
            foreach (var pair in disk) AddOrMove(merged, mergedNodes, pair.Key, pair.Value);
            foreach (var pair in memorySnapshot) AddOrMove(merged, mergedNodes, pair.Key, pair.Value);
            while (merged.Count > DiskCapacity && merged.First is not null)
            {
                mergedNodes.Remove(merged.First.Value.Key);
                merged.RemoveFirst();
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath,
                JsonSerializer.Serialize(merged.ToDictionary(x => x.Key, x => x.Value), JsonOptions));
            lock (order)
            {
                // 写盘成功后索引才是准的；与文件保持一致，下次查缓存不必重读。
                diskIndex.Clear();
                foreach (var pair in merged) diskIndex[pair.Key] = pair.Value;
                // Set 可能在磁盘 I/O 期间并发发生，不能把那次变更的 dirty 状态清掉。
                if (version == snapshotVersion) dirty = false;
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

    private void LoadFromDisk()
    {
        if (filePath is null || !File.Exists(filePath)) return;
        try
        {
            var loaded = ReadDiskEntries();
            lock (order)
            {
                // 全部进常驻索引，最近的一部分再进内存 LRU。
                foreach (var pair in loaded) diskIndex[pair.Key] = pair.Value;
                foreach (var pair in loaded.TakeLast(MemoryCapacity)) AddOrMoveToEnd(pair.Key, pair.Value);
                TrimMemory();
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

    private List<KeyValuePair<string, string>> ReadDiskEntries()
    {
        if (filePath is null || !File.Exists(filePath)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath), JsonOptions)?.ToList() ?? [];
    }

    private void AddOrMoveToEnd(string key, string value)
    {
        if (nodes.TryGetValue(key, out var existing))
        {
            order.Remove(existing);
            nodes.Remove(key);
        }
        nodes[key] = order.AddLast((key, value));
    }

    private void TrimMemory()
    {
        while (nodes.Count > MemoryCapacity && order.First is not null)
        {
            nodes.Remove(order.First.Value.Key);
            order.RemoveFirst();
        }
    }

    private static void AddOrMove(
        LinkedList<(string Key, string Value)> target,
        Dictionary<string, LinkedListNode<(string Key, string Value)>> index,
        string key,
        string value)
    {
        if (index.TryGetValue(key, out var existing))
        {
            target.Remove(existing);
            index.Remove(key);
        }
        index[key] = target.AddLast((key, value));
    }
}
