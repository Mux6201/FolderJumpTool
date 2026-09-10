using Microsoft.Win32;

namespace FolderJumpTool;

/// <summary>
/// 开机自动启动：写 HKCU\...\CurrentVersion\Run 项（当前用户生效，**无需管理员权限**）。
/// 选注册表而不是启动文件夹快捷方式：程序化读写更直接，且任务管理器"启动"页
/// 同样能看到并禁用，用户随时可撤销。
///
/// 关键点：记录的是"当前 exe 的完整路径"。程序日后被移动/更新（换目录、换版本）时，
/// 旧路径会失效导致开机不启动——所以启动时用 <see cref="SyncPathIfEnabled"/> 校准：
/// 已开启自启但路径与当前不符，就改写为当前路径。
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>系统"启动项已禁用"标记的位置。任务管理器 / 设置里禁用启动项时只写这里，
    /// **不会删 Run 项**；因此判断"是否真的会开机启动"必须两处一起看。
    /// 值格式：byte[12]，首字节 0x02 = 启用、0x03 = 禁用（其余为时间戳）。</summary>
    private const string ApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Run 项名（显示在任务管理器启动页的名称）。</summary>
    private const string ValueName = "FolderJumpTool";

    /// <summary>
    /// 是否真的会开机自启：Run 项存在 **且** 没有被系统标记为禁用。
    /// （只看 Run 项会漏掉"用户在任务管理器里禁用过"的情况——那时 Run 项还在，
    /// 但系统不会启动它；此处一并考虑，菜单勾选始终反映真实状态。）
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(ValueName) is not string s || s.Length == 0)
                return false;

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } flag && flag[0] == 0x03)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 开启/关闭开机自启。开启时写入当前 exe 路径（加引号，路径可能含空格），
    /// 并清掉系统层面的"已禁用"标记；关闭时直接删除 Run 项。
    /// 注册表不可写（组策略限制等）时静默失败——调用方回读 <see cref="IsEnabled"/>
    /// 拿到真实状态，菜单勾选不会与实际不符。
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null)
                return;

            if (enabled)
            {
                if (Environment.ProcessPath is { Length: > 0 } exe)
                    key.SetValue(ValueName, $"\"{exe}\"");
                ClearDisabledFlag();
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 静默失败，由调用方回读真实状态
        }
    }

    /// <summary>把系统"已禁用"标记改写为"启用"（0x02）。否则用户曾在任务管理器里禁用过，
    /// 即使重新注册 Run 项也不会开机启动。键/值不存在时无需处理。</summary>
    private static void ClearDisabledFlag()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } flag)
            {
                flag[0] = 0x02;
                approved.SetValue(ValueName, flag);
            }
        }
        catch
        {
            // 标记清理失败不影响 Run 项本身
        }
    }

    /// <summary>
    /// 启动时校准路径：已开启自启但记录的路径与当前 exe 不一致（程序被移动/更新过），
    /// 自动改写为当前路径，避免开机启动指向不存在的旧文件。未开启则不写入。
    /// </summary>
    public static void SyncPathIfEnabled()
    {
        try
        {
            if (Environment.ProcessPath is not { Length: > 0 } exe)
                return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string current || current.Length == 0)
                return; // 未开启自启：什么都不做

            var expected = $"\"{exe}\"";
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, expected);
        }
        catch
        {
            // 校准失败不影响其它功能
        }
    }
}
