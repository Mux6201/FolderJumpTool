using System.IO;
using System.Text.Json;

namespace FolderJumpTool;

/// <summary>
/// 悬浮窗"历史"页的浏览历史——FolderJumpTool 自己记录用过的文件夹路径。
///
/// 为什么自建：候选的"最近"来源原本是 Windows 的 %Recent%\*.lnk（解析快捷方式目标），
/// 但实测在 Win11 上该目录**恒为空**（系统不再往那里写），于是资源管理器窗口一关，
/// 那个文件夹就从候选里彻底消失。自己记一份最可靠，也不受系统行为变化影响。
///
/// 性能约定（与 2026-09 的性能修复保持一致，别再往 UI 线程塞磁盘 IO）：
///  1. 存在性检查走 60 秒 TTL 缓存 —— 悬浮窗每次刷新候选都会问一遍，直接问盘会重复 IO；
///     网络位置（UNC 或**映射的网络驱动器**）一律跳过验证，离线时 Exists 可阻塞数百毫秒以上；
///  2. 来源"必然存在"的路径（刚枚举到的资源管理器窗口）跳过验证；
///  3. 落盘异步且合并 —— 调用方（UI 线程）只更新内存快照并触发一次后台写，
///     写任务在途时后续请求直接合并（写的就是最新快照）。
///
/// 两个记录点都调 <see cref="Touch"/> / <see cref="TouchMany"/>：已存在则置顶，
/// 超过上限丢弃最旧，内容变化才写盘。存储：%AppData%\FolderJumpTool\history.json。
/// </summary>
internal static class HistoryStore
{
    private const int MaxEntries = 20;
    private const int ExistsCacheSoftLimit = 256;

    private static readonly object Gate = new();
    private static readonly List<string> Cache = LoadFromDisk();

    /// <summary>存在性检查的 TTL 缓存（path → 上次结果 + 时间）。只在 UI 线程访问，无需额外加锁。</summary>
    private static readonly Dictionary<string, (bool Exists, DateTime CheckedAt)> ExistsCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ExistsTtl = TimeSpan.FromSeconds(60);

    /// <summary>是否有后台写盘任务在途（1 = 在途）。在途时新的写请求直接合并。</summary>
    private static int _writePending;

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FolderJumpTool", "history.json");

    /// <summary>按最近使用倒序取出至多 max 条仍然存在的目录路径。</summary>
    public static List<string> GetRecent(int max)
    {
        lock (Gate)
        {
            var result = new List<string>(Math.Min(max, Cache.Count));
            foreach (var path in Cache)
            {
                if (result.Count >= max)
                    break;
                if (ExistsCached(path))
                    result.Add(path); // 已删除/暂不可用的跳过（不从历史里抹掉，可能只是盘没挂载）
            }
            return result;
        }
    }

    /// <summary>把一条路径置顶（不存在则加入，已存在则移到最前）。</summary>
    public static void Touch(string path) => TouchMany(new[] { path });

    /// <summary>
    /// 批量置顶。列表顺序约定为"越靠前越活跃"（如资源管理器窗口按 Z 序），
    /// 因此反向遍历，让最活跃的最终排在最前。只在内容真正变化时落盘。
    /// <paramref name="skipValidation"/> = true 用于来源必然存在的路径
    /// （如刚枚举到的资源管理器窗口），省掉每个路径的磁盘存在性检查。
    /// </summary>
    public static void TouchMany(IReadOnlyList<string> paths, bool skipValidation = false)
    {
        if (paths.Count == 0)
            return;

        lock (Gate)
        {
            var before = string.Join('\u0001', Cache);

            for (int i = paths.Count - 1; i >= 0; i--)
            {
                var normalized = NormalizeRecordPath(paths[i], skipValidation);
                if (normalized.Length == 0)
                    continue;

                int idx = Cache.FindIndex(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    Cache.RemoveAt(idx);
                Cache.Insert(0, normalized);
            }

            if (Cache.Count > MaxEntries)
                Cache.RemoveRange(MaxEntries, Cache.Count - MaxEntries);

            if (string.Join('\u0001', Cache) != before)
                SaveToDisk();
        }
    }

    /// <summary>映射盘符类型的缓存（'Z' → 是否网络盘），避免重复构造 DriveInfo。</summary>
    private static readonly Dictionary<char, bool> DriveTypeCache = new();

    /// <summary>
    /// 路径是否位于网络位置——UNC（<c>\\server\share</c>，含"添加网络位置"）或
    /// **映射的网络驱动器**（<c>Z:</c> → <c>\\NAS\share</c>，形态与本地盘完全一样，
    /// 靠盘符判断不出来）。这类路径的 Exists 会走 SMB，离线时可能阻塞数百毫秒以上。
    /// DriveInfo.DriveType 查的是本机驱动器类型（本地系统调用，不触网），开销可忽略。
    /// </summary>
    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        // 形如 "Z:\..." 或 "Z:" 的盘符路径
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            char letter = char.ToUpperInvariant(path[0]);
            if (!DriveTypeCache.TryGetValue(letter, out bool network))
            {
                try
                {
                    network = new DriveInfo(path.Substring(0, 2)).DriveType == DriveType.Network;
                }
                catch
                {
                    network = false; // 查询失败（盘符不存在等）当本地处理
                }
                DriveTypeCache[letter] = network;
            }
            return network;
        }

        return false;
    }

    /// <summary>带 TTL 的存在性检查：60 秒内同一路径不重复问盘；网络位置直接放行。</summary>
    private static bool ExistsCached(string path)
    {
        // 网络位置跳过验证：网络盘离线时 Exists 可能阻塞数百毫秒甚至更久，
        // 而历史里存的多是用户常用位置——宁可显示一个暂时点不通的条目，也不卡住 UI 线程。
        if (IsNetworkPath(path))
            return true;

        var now = DateTime.UtcNow;
        if (ExistsCache.TryGetValue(path, out var cached) && now - cached.CheckedAt < ExistsTtl)
            return cached.Exists;

        if (ExistsCache.Count > ExistsCacheSoftLimit)
            PruneExistsCache(now);

        bool exists = Directory.Exists(path);
        ExistsCache[path] = (exists, now);
        return exists;
    }

    /// <summary>清掉已过期的存在性缓存（避免长期运行后无限累积）。</summary>
    private static void PruneExistsCache(DateTime now)
    {
        var stale = new List<string>();
        foreach (var kv in ExistsCache)
        {
            if (now - kv.Value.CheckedAt >= ExistsTtl)
                stale.Add(kv.Key);
        }
        foreach (var key in stale)
            ExistsCache.Remove(key);
    }

    /// <summary>
    /// 把待记录的路径规整为"目录路径"：目录直接用；文件取所在目录
    /// （用户跳转到一个文件，也代表他"去过那个目录"）；两者都不成立则返回空。
    /// </summary>
    private static string NormalizeRecordPath(string? path, bool skipValidation)
    {
        var trimmed = (path ?? string.Empty).TrimEnd('\\');
        if (trimmed.Length == 0)
            return string.Empty;

        // 来源必然存在（如枚举到的资源管理器窗口）或网络位置（验证可能阻塞）→ 直接信任
        if (skipValidation || IsNetworkPath(trimmed))
            return trimmed;

        if (Directory.Exists(trimmed))
            return trimmed;

        if (File.Exists(trimmed))
        {
            var dir = System.IO.Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                return dir.TrimEnd('\\');
        }

        return string.Empty;
    }

    private static List<string> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new List<string>();

            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath))
                   ?? new List<string>();
        }
        catch
        {
            return new List<string>(); // 损坏/不可读都当"没有历史"，不影响启动
        }
    }

    /// <summary>
    /// 异步落盘：写入"调用时刻的最新快照"，不阻塞调用方（UI 线程）。
    /// 已有写任务在途时直接返回——它写的就是最新内容，无需排队重复写。
    /// </summary>
    private static void SaveToDisk()
    {
        if (Interlocked.Exchange(ref _writePending, 1) == 1)
            return;

        string json;
        lock (Gate)
            json = JsonSerializer.Serialize(Cache);

        Task.Run(() =>
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // 历史写盘失败不影响功能（最多丢最近几次记录）
            }
            finally
            {
                Interlocked.Exchange(ref _writePending, 0);
            }
        });
    }
}
