using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
// csproj 启用了 UseWindowsForms（托盘用），System.Drawing/System.Windows.Forms 进了全局命名空间，
// Brush/Application 等与 WPF 撞名，这里显式别名到 WPF。
using Brush = System.Windows.Media.Brush;
using WpfApplication = System.Windows.Application;

namespace FolderJumpTool;

/// <summary>窗口背景材质：Solid=不透明纯色卡片（默认）/ Acrylic=亚克力毛玻璃。
/// 曾存在 Mica 选项：WPF 客户区被不透明渲染树盖死、DWM backdrop 无法透出，
/// 静态模拟与纯色无异，已从菜单与枚举移除；settings.json 旧值 "Mica" 解析失败自动回落 Solid。</summary>
public enum WindowMaterial
{
    Solid,
    Acrylic,
}

/// <summary>
/// 窗口材质管理器。
/// Acrylic：悬浮窗是分层窗口（AllowsTransparency=true），不走 DWM 合成，
/// DWM SystemBackdrop（DWMSBT_*）对它不渲染——改用 SetWindowCompositionAttribute
/// 的 ACCENT 真模糊（Win10 1607+）：分层窗口自带真 alpha 通道，
/// 半透明 tint 盖在模糊层上保证可读。
/// Solid / 带标题栏管理窗：只做标题栏暗色跟随 + 系统圆角（Apply），背景恒不透明纯色卡。
/// </summary>
public static class BackdropManager
{
    // ---------- DWM 属性（普通窗口外观：标题栏暗色 + 圆角） ----------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    // ---------- ACCENT 亚克力（SetWindowCompositionAttribute，Win10 1607+） ----------
    // 分层窗口上 DWM backdrop 不渲染；ACCENT 是老牌真模糊接口，模糊整个矩形窗口
    // （含内容底半透明 tint 之外的区域），窗口形状由 WPF 内容层自绘决定。

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor; // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEX versionInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct OSVERSIONINFOEX
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public byte[] szCSDVersion;
    }

    private static int? _buildNumber;

    /// <summary>当前 Windows 构建号（缓存；拿不到时返回 0）。</summary>
    private static int BuildNumber()
    {
        if (_buildNumber.HasValue)
            return _buildNumber.Value;
        var osvi = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEX>() };
        try
        {
            RtlGetVersion(ref osvi);
            _buildNumber = osvi.dwBuildNumber;
        }
        catch
        {
            _buildNumber = 0;
        }
        return _buildNumber.Value;
    }

    /// <summary>ACCENT 亚克力是否可用：Windows 10 1607 (build 14393)+。
    /// 历史上按 Win11 22H2 判断过——那是 DWM SystemBackdrop 的版本线；
    /// 现在只走 ACCENT，Win10 1607 起即支持，门槛随实现一起放宽。</summary>
    public static bool IsSupported() => BuildNumber() >= 14393;

    /// <summary>
    /// 对普通（非分层）窗口应用系统外观：标题栏暗色跟随主题 + 系统圆角。
    /// 材质仅 Solid 语义——带标题栏的窗口没有真 alpha 通道，半透明 tint 会被 WPF
    /// 按黑底合成导致整窗发黑，因此管理窗恒纯色；毛玻璃专属分层悬浮窗
    /// （走 ApplyAcrylicAccent）。背景画刷由调用方用 GetSurfaceBrush 同步切换。
    /// </summary>
    public static void Apply(Window window, bool darkTheme)
    {
        if (window == null)
            return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            int dark = darkTheme ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch
        {
            // DWM 调用失败（老系统/特殊环境）就保持原样，不影响功能
        }
    }

    /// <summary>
    /// 对分层窗口（AllowsTransparency=true）应用 ACCENT 亚克力真模糊。
    /// 采样窗口背后的内容做高斯模糊 + 叠 tint；GradientColor 为 0xAABBGGRR，
    /// alpha 越大底色越实（模糊越不明显），越小越透（文字可读性差）。
    /// 窗口自身的半透明内容层（WPF brush）会正常叠加在模糊上。
    /// </summary>
    public static void ApplyAcrylicAccent(Window window, System.Windows.Media.Color tint)
    {
        if (window == null)
            return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            int gradient = (tint.A << 24) | (tint.B << 16) | (tint.G << 8) | tint.R; // ARGB→ABGR
            var accent = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 0,
                GradientColor = gradient,
                AnimationId = 0,
            };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size,
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            // ACCENT 调用失败保持原样；菜单/启动已用 IsSupported 挡掉不支持的系统
        }
    }

    /// <summary>
    /// 材质对应的"卡片底色"：
    /// Solid = 原不透明卡片色（CardBackgroundBrush）；
    /// Acrylic = 半透明 tint（MaterialAcrylicBrush，盖在 ACCENT 模糊层上保证可读）。
    /// 从当前主题字典取，主题切换后字典重建，调用方需重新应用。
    /// </summary>
    public static Brush? GetSurfaceBrush(WindowMaterial material)
    {
        string key = material == WindowMaterial.Acrylic ? "MaterialAcrylicBrush" : "CardBackgroundBrush";
        try
        {
            return WpfApplication.Current.TryFindResource(key) as Brush;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>关闭窗口的 ACCENT 亚克力（切回纯色时调用，避免残留模糊）。</summary>
    public static void ClearAcrylic(Window window)
    {
        if (window == null)
            return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            var accent = new AccentPolicy { AccentState = ACCENT_DISABLED };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size,
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            // 忽略：关闭失败也无妨
        }
    }
}
