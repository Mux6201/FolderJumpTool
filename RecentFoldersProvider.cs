using System.IO;
using System.Reflection;

namespace FolderJumpTool;

/// <summary>
/// 提供动态候选路径：当前打开的资源管理器窗口 + 最近使用的文件夹 + 固定收藏夹。
/// "当前打开的窗口"用反射做 COM 晚绑定调用 Shell.Application，不需要额外的 NuGet 包。
/// </summary>
internal static class RecentFoldersProvider
{
    /// <summary>
    /// 组合出完整候选列表：已打开的资源管理器窗口排最前面，然后是最近使用的文件夹，
    /// 最后补上固定收藏夹兜底，整体按路径去重。
    /// 资源管理器窗口之间按 Z 序排列（激活中的 tab / 窗口在最顶上）。
    /// </summary>
    public static List<FavoriteFolder> GetCandidates(int maxRecent = 6, int maxTotal = 8)
    {
        var result = new List<FavoriteFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(string name, string path)
        {
            if (result.Count >= maxTotal)
                return;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;
            var normalized = path.TrimEnd('\\');
            if (!seen.Add(normalized))
                return;
            result.Add(new FavoriteFolder { Name = name, Path = normalized });
        }

        foreach (var (_, path) in GetOpenExplorerFolders())
            AddIfNew(new DirectoryInfo(path).Name is { Length: > 0 } n ? n : path, path);

        foreach (var path in GetRecentFolders(maxRecent))
            AddIfNew(new DirectoryInfo(path).Name is { Length: > 0 } n ? n : path, path);

        // 收藏夹（无持久化收藏时用桌面/下载/文档兜底展示，不落盘）
        foreach (var fav in FavoritesManager.LoadWithDefaults())
            AddIfNew(fav.Name, fav.Path);

        return result;
    }

    /// <summary>
    /// 悬浮窗"最近"页数据源：仅已打开的资源管理器窗口（Z 序）+ 最近使用文件夹，
    /// 不含收藏夹（收藏走独立页签）。整体去重并封顶。
    /// </summary>
    public static List<FavoriteFolder> GetRecentAndExplorer(int maxRecent = 6, int maxTotal = 8)
    {
        var result = new List<FavoriteFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(string name, string path)
        {
            if (result.Count >= maxTotal)
                return;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;
            var normalized = path.TrimEnd('\\');
            if (!seen.Add(normalized))
                return;
            result.Add(new FavoriteFolder { Name = name, Path = normalized });
        }

        foreach (var (_, path) in GetOpenExplorerFolders())
            AddIfNew(new DirectoryInfo(path).Name is { Length: > 0 } n ? n : path, path);

        foreach (var path in GetRecentFolders(maxRecent))
            AddIfNew(new DirectoryInfo(path).Name is { Length: > 0 } n ? n : path, path);

        return result;
    }

    /// <summary>
    /// 拿到当前所有打开的资源管理器窗口/标签页所在的文件夹路径，返回 (hwnd, path)。
    /// 用 Type.InvokeMember 做 COM 晚绑定（IDispatch），不需要 dynamic / 不需要额外包。
    /// </summary>
    private static List<(IntPtr Hwnd, string Path)> GetOpenExplorerFolders()
    {
        var windows = new List<(IntPtr Hwnd, string Path)>();

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return windows;

            object? shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return windows;

            object? allWindows = Invoke(shell, "Windows");
            if (allWindows == null)
                return windows;

            int count = (int)(Invoke(allWindows, "Count") ?? 0);

            for (int i = 0; i < count; i++)
            {
                try
                {
                    object? item = InvokeIndexed(allWindows, "Item", i);
                    if (item == null) continue;

                    object? document = GetProp(item, "Document");
                    if (document == null) continue; // 不是资源管理器窗口（比如 IE），跳过

                    object? folder = GetProp(document, "Folder");
                    if (folder == null) continue;

                    object? self = GetProp(folder, "Self");
                    if (self == null) continue;

                    if (GetProp(self, "Path") is not string path || string.IsNullOrWhiteSpace(path))
                        continue;

                    // Shell.Application 的 HWND 属性以 Int64 返回（实测，不是 int）。
                    // 用 `is int` 做拆箱匹配永远失败 -> hwnd 全落成 0 -> Z 深度全部相同、
                    // 排序失效、列表退化成 Shell 原始枚举顺序（表现为"新开的窗口反而在底下"）。
                    // 统一走 Convert.ToInt64，兼容 int/uint/long 各种装箱形态。
                    IntPtr hwnd = IntPtr.Zero;
                    try
                    {
                        if (GetProp(item, "HWND") is { } raw)
                            hwnd = new IntPtr(Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture));
                    }
                    catch
                    {
                        // 转换失败保持 IntPtr.Zero（Z 序排最后，防御性兜底）
                    }

                    windows.Add((hwnd, path));
                }
                catch
                {
                    // 单个窗口取失败就跳过，不影响其它窗口
                }
            }
        }
        catch
        {
            // Shell.Application 不可用（极少见），直接返回空列表，上层会用其它候选兜底
        }

        // 按 Z 序排序：资源管理器每开一个 tab/窗口，切换激活时会被带到桌面 Z 序的
        // 最上面，因此当前正在看的那个目录必然排最前。取不到 hwnd 的（防御性）
        // 保持原枚举顺序排到后面，反正前面能排的都排了。
        windows.Sort((a, b) => ZOrderIndex(a.Hwnd).CompareTo(ZOrderIndex(b.Hwnd)));

        // 顺带记入浏览历史：这些窗口关掉后，路径仍能在悬浮窗"历史"页找回。
        // 放在枚举出口，任何取候选的调用都会记录；记录与"显示历史页签"开关无关。
        // skipValidation：这些路径刚由 Shell 枚举出来、必然存在，省掉逐个磁盘检查。
        HistoryStore.TouchMany(windows.Select(w => w.Path).ToList(), skipValidation: true);

        return windows;
    }

    /// <summary>
    /// 把"当前打开的资源管理器窗口"路径记入浏览历史（枚举本身即记录，见 GetOpenExplorerFolders 出口）。
    ///
    /// 供 App 的低频后台采样调用：我们本来只在"弹对话框"时才枚举资源管理器窗口，
    /// 于是"打开资源管理器 → 逛一圈 → 关掉 → 再弹对话框"这种顺序下，窗口关闭时
    /// 已经没有枚举机会，那些目录会漏记。窗口存活期间被采样到一次即可留住。
    /// </summary>
    public static void RecordOpenExplorerFolders() => _ = GetOpenExplorerFolders();

    /// <summary>
    /// 悬浮窗"历史"页数据源：自己记录的浏览历史（最近使用倒序），
    /// 关掉资源管理器窗口的文件夹也能在这里找回。目录已被删除的条目会自动跳过。
    /// </summary>
    public static List<FavoriteFolder> GetHistory(int max)
    {
        var result = new List<FavoriteFolder>();
        foreach (var path in HistoryStore.GetRecent(max))
        {
            var name = new DirectoryInfo(path).Name;
            result.Add(new FavoriteFolder
            {
                Name = name.Length > 0 ? name : path,
                Path = path,
                IsDirectory = true,
            });
        }
        return result;
    }

    /// <summary>计算某顶层窗口在桌面 Z 序中的深度：0 = 最顶层，越大越靠底。</summary>
    private static int ZOrderIndex(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return int.MaxValue;

        int depth = 0;
        for (var w = NativeMethods.GetTopWindow(IntPtr.Zero);
             w != IntPtr.Zero;
             w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDNEXT))
        {
            if (w == hwnd)
                return depth;
            depth++;
        }
        return int.MaxValue; // 没在 Z 序里找到（防御），排最后
    }

    /// <summary>最近文件夹缓存：Recent 目录可能有几百上千个 .lnk，逐个 COM 解析
    /// （ShellLinkResolver，每个 1~10ms）不能在每次打开对话框时重做。
    /// 30 秒内复用同一份结果——Recent 目录变化频率远低于此，感知不到差异。</summary>
    private static List<string>? _recentCache;
    private static DateTime _recentCacheUtc;

    /// <summary>
    /// 读取"最近使用的文件"里的 .lnk 快捷方式，解析出目标路径，
    /// 如果目标本身是文件夹就直接用，是文件就取它所在的文件夹。
    /// 按最后写入时间倒序，取前 N 个去重后的文件夹。
    /// 解析尝试上限 40 个：倒序靠后的 .lnk 大多指向已删除/无关目标，
    /// 排序已保证最新的在最前，40 个凑不满 N 个说明真的没有更多有效项。
    /// </summary>
    private static List<string> GetRecentFolders(int max)
    {
        if (_recentCache is { } cached && (DateTime.UtcNow - _recentCacheUtc).TotalSeconds < 30)
            return cached;

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (!Directory.Exists(recentDir))
                return result;

            var lnkFiles = new DirectoryInfo(recentDir)
                .GetFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTime);

            int attempts = 0;
            foreach (var lnk in lnkFiles)
            {
                if (result.Count >= max || ++attempts > 40)
                    break;

                var target = ShellLinkResolver.ResolveTarget(lnk.FullName);
                if (string.IsNullOrWhiteSpace(target))
                    continue;

                string? folder = Directory.Exists(target) ? target
                    : File.Exists(target) ? Path.GetDirectoryName(target)
                    : null;

                if (folder == null)
                    continue;

                if (seen.Add(folder))
                    result.Add(folder);
            }
        }
        catch
        {
            // 忽略，返回目前已经收集到的结果
        }

        _recentCache = result;
        _recentCacheUtc = DateTime.UtcNow;
        return result;
    }

    private static object? Invoke(object target, string member) =>
        target.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, null);

    private static object? InvokeIndexed(object target, string member, int index) =>
        target.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, new object[] { index });

    private static object? GetProp(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
}
