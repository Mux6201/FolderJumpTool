using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FolderJumpTool;

/// <summary>
/// 行图标直接用 Windows Shell 图标（SHGetFileInfo），与资源管理器观感一致：
/// 目录 → 系统文件夹图标（桌面/下载/文档等特殊文件夹有自己的专属图标）；
/// 文件 → 按扩展名归类的真实文件类型图标（.jpg/.txt/.zip/.exe 各不相同）。
/// 提取一次后按 key 缓存（目录按完整路径、文件按扩展名），行图标懒加载、零重复开销。
/// </summary>
internal static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>缓存容量兜底：目录路径会随使用累积，超限整清（下次按需重取，开销极小）。</summary>
    private const int MaxCacheEntries = 640;

    /// <summary>取路径对应的 16x16 系统图标；失败返回 null（行内留空，不阻塞列表）。</summary>
    public static ImageSource? Get(string? path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // 目录按完整路径缓存（特殊文件夹图标依赖路径）；文件按扩展名缓存（同类图标相同，命中率高）。
        var key = isDirectory
            ? "D\u0001" + path.TrimEnd('\\')
            : "F\u0001" + (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit))
                return hit;
        }

        var icon = Extract(path, isDirectory);
        if (icon != null)
        {
            lock (Cache)
            {
                if (Cache.Count >= MaxCacheEntries)
                    Cache.Clear();
                Cache[key] = icon;
            }
        }
        return icon;
    }

    /// <summary>
    /// 预热：做一次真实提取，把进程内 Shell 图标子系统（首次 SHGetFileInfo 会初始化
    /// 图标缓存与各类图标处理器）的初始化成本提前到程序启动阶段的后台线程，
    /// 避免第一次搜索的结果列表渲染时，在 UI 线程上集中付出这笔开销。失败静默。
    /// </summary>
    public static void WarmUp()
    {
        try
        {
            var probe = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(probe))
                _ = Get(probe, isDirectory: true);
        }
        catch
        {
            // 预热失败不影响功能（真正取图标时还会再试）
        }
    }

    private static ImageSource? Extract(string path, bool isDirectory)
    {
        var shfi = new NativeMethods.SHFILEINFO();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON;
        uint attrs = isDirectory ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL;

        // 网络位置（UNC / 映射盘）改用 USEFILEATTRIBUTES：让 Shell 直接按"目录/文件"
        // 属性返回通用图标，不去访问真实路径。脱机或休眠的对端上，一次 SHGetFileInfo
        // 会等到 SMB 超时（数秒），而这里是 UI 线程的绑定回调——卡住就是整窗卡住，
        // 表现正是"文件选择对话框出来了、我们的悬浮窗过几秒才跟出来"。
        // 代价仅是这类条目显示通用文件夹图标。
        if (PathUtil.IsNetworkPath(path))
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;

        IntPtr ok;
        try
        {
            ok = NativeMethods.SHGetFileInfo(path, attrs, ref shfi,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);
        }
        catch
        {
            return null;
        }
        if (ok == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
            return null;

        try
        {
            // CreateBitmapSourceFromHIcon 取出后 hIcon 即由我们负责销毁；
            // Freeze 后该 ImageSource 可在任意线程/多次绑定间共享。
            var src = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(shfi.hIcon);
        }
    }
}

/// <summary>FavoriteFolder → 行图标（目录=文件夹图标，文件=按类型的图标）。</summary>
public sealed class ShellIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FavoriteFolder item)
            return null;
        return ShellIcons.Get(item.Path, item.IsDirectory);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
