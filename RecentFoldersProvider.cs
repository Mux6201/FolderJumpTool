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
        return windows;
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

    /// <summary>
    /// 读取"最近使用的文件"里的 .lnk 快捷方式，解析出目标路径，
    /// 如果目标本身是文件夹就直接用，是文件就取它所在的文件夹。
    /// 按最后写入时间倒序，取前 N 个去重后的文件夹。
    /// </summary>
    private static List<string> GetRecentFolders(int max)
    {
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

            foreach (var lnk in lnkFiles)
            {
                if (result.Count >= max)
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

        return result;
    }

    private static object? Invoke(object target, string member) =>
        target.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, null);

    private static object? InvokeIndexed(object target, string member, int index) =>
        target.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, new object[] { index });

    private static object? GetProp(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
}
