using System.IO;

namespace FolderJumpTool;

/// <summary>
/// 路径判断的公共工具。目前只有一件事：识别"网络位置"。
///
/// 为什么必须识别它：对网络路径做 Directory.Exists / File.Exists 会走 SMB，
/// 当对端（NAS、文件服务器、映射盘）离线或休眠时，**单次调用会一直等到 SMB 超时**
/// —— 实测可达数十秒。而这个调用出现在好几条热路径上：候选列表构建、浏览历史校验、
/// 最近文档 .lnk 目标解析、搜索结果类型判断。任何一条卡住，用户看到的就是
/// "文件选择对话框早出来了，我们的悬浮窗过几秒才跟出来"。
/// 所以这些地方一律先判断，网络路径直接跳过存在性校验——宁可显示一个暂时点不通的
/// 条目，也不能让 UI 线程卡死。
///
/// DriveInfo.DriveType 读的是本机驱动器类型（本地系统调用，不触网），开销可忽略。
/// </summary>
internal static class PathUtil
{
    /// <summary>映射盘符类型缓存（'Z' → 是否网络盘），避免重复构造 DriveInfo。</summary>
    private static readonly Dictionary<char, bool> DriveTypeCache = new();

    /// <summary>
    /// 是否为网络位置：UNC（<c>\\server\share</c>，含"添加网络位置"这种）或
    /// **映射的网络驱动器**（<c>Z:</c> 实际指向 <c>\\NAS\share</c>，形态与本地盘
    /// 完全一样，光看盘符是看不出来的）。
    /// </summary>
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        // 形如 "Z:\..." 或 "Z:" 的盘符路径
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            var letter = char.ToUpperInvariant(path[0]);

            lock (DriveTypeCache)
            {
                if (DriveTypeCache.TryGetValue(letter, out var cached))
                    return cached;
            }

            bool network;
            try
            {
                network = new DriveInfo(path.Substring(0, 2)).DriveType == DriveType.Network;
            }
            catch
            {
                network = false; // 查询失败（盘符不存在等）按本地处理
            }

            lock (DriveTypeCache)
                DriveTypeCache[letter] = network;
            return network;
        }

        return false;
    }
}
