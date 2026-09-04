using System.Text;

namespace FolderJumpTool;

/// <summary>
/// 负责识别对话框、找到"文件名"编辑框、写入路径并触发导航。
/// 全程走标准 Win32 消息，不依赖 UI Automation，兼容性和速度都更好。
/// </summary>
internal static class DialogNavigator
{
    // 系统公共对话框模板固定的控件 ID，Win7 ~ Win11 基本没变过
    private const int IDOK = 1;
    private const int IDCANCEL = 2;
    private const int FILENAME_COMBO_ID = 0x47C; // 1148，"文件名"下拉框外壳(ComboBoxEx32)

    /// <summary>
    /// 判断这个 #32770 窗口是不是"打开/保存文件"对话框（而不是普通消息弹窗）。
    /// 依据：是否存在文件名下拉框 + 打开/取消按钮。
    /// </summary>
    public static bool LooksLikeFileDialog(IntPtr hwnd)
    {
        var comboEx = NativeMethods.GetDlgItem(hwnd, FILENAME_COMBO_ID);
        var cancelBtn = NativeMethods.GetDlgItem(hwnd, IDCANCEL);
        return comboEx != IntPtr.Zero && cancelBtn != IntPtr.Zero;
    }

    /// <summary>
    /// 尝试找到真正接收文本输入的 Edit 控件。
    /// 结构通常是：ComboBoxEx32(0x47C) -&gt; ComboBox -&gt; Edit
    /// 少数情况下（旧样式对话框）ComboBoxEx32 下面直接就是 Edit。
    /// </summary>
    public static IntPtr FindFileNameEditControl(IntPtr dialogHwnd)
    {
        var comboEx = NativeMethods.GetDlgItem(dialogHwnd, FILENAME_COMBO_ID);
        if (comboEx == IntPtr.Zero)
            return IntPtr.Zero;

        // 先按标准三层结构找
        var innerCombo = NativeMethods.FindWindowEx(comboEx, IntPtr.Zero, "ComboBox", null);
        if (innerCombo != IntPtr.Zero)
        {
            var edit = NativeMethods.FindWindowEx(innerCombo, IntPtr.Zero, "Edit", null);
            if (edit != IntPtr.Zero)
                return edit;
        }

        // Fallback：某些皮肤/旧样式下少一层，直接在 comboEx 下找 Edit
        var directEdit = NativeMethods.FindWindowEx(comboEx, IntPtr.Zero, "Edit", null);
        return directEdit;
    }

    /// <summary>
    /// 核心方法：把目标路径写进文件名框，然后点击"打开/保存"按钮。
    /// 如果目标是文件夹，系统对话框会自动导航进去而不会关闭——这是系统原生行为。
    /// </summary>
    /// <returns>true 表示成功找到控件并完成了写入+触发；不代表路径一定合法存在。</returns>
    public static bool NavigateTo(IntPtr dialogHwnd, string path)
    {
        if (!NativeMethods.IsWindow(dialogHwnd))
        {
            Log.Info("[FolderJumpTool] NavigateTo: dialogHwnd 已失效");
            return false;
        }

        var editCtrl = FindFileNameEditControl(dialogHwnd);
        Log.Info($"[FolderJumpTool] NavigateTo: editCtrl={editCtrl}");
        if (editCtrl == IntPtr.Zero)
        {
            Log.Info("[FolderJumpTool]   -> 没找到文件名 Edit 控件，中止");
            return false;
        }

        // 1. 写入路径。注意如果路径带空格，建议加双引号，
        //    系统对话框对带引号的路径也能正确解析。
        var textToSend = path.Contains(' ') && !path.StartsWith('"')
            ? $"\"{path}\""
            : path;

        NativeMethods.SendMessage(editCtrl, NativeMethods.WM_SETTEXT, IntPtr.Zero, textToSend);

        // 2. 校验一下是否真的写进去了
        var verified = VerifyTextWritten(editCtrl, path);
        Log.Info($"[FolderJumpTool]   -> 写入校验 = {verified}");

        // 3. 点击"打开/保存"按钮触发导航。
        //    不要用模拟回车键（WM_KEYDOWN/WM_CHAR）去发给 Edit 控件，
        //    对话框的默认按钮响应是靠消息循环里的 IsDialogMessage 转发实现的，
        //    直接 SendMessage 按键消息大概率不会触发，必须直接点按钮。
        var okButton = NativeMethods.GetDlgItem(dialogHwnd, IDOK);
        Log.Info($"[FolderJumpTool]   -> okButton={okButton}");
        if (okButton == IntPtr.Zero)
            return false;

        var clickResult = NativeMethods.SendMessage(okButton, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        Log.Info($"[FolderJumpTool]   -> BM_CLICK SendMessage 返回 {clickResult}");
        return true;
    }

    private static bool VerifyTextWritten(IntPtr editCtrl, string expectedPath)
    {
        var len = (int)NativeMethods.SendMessage(editCtrl, NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
        if (len <= 0)
        {
            Log.Info("[FolderJumpTool]   -> VerifyTextWritten: WM_GETTEXTLENGTH 返回 0/负数，读不到任何文本");
            return false;
        }

        var sb = new StringBuilder(len + 1);
        NativeMethods.SendMessage(editCtrl, NativeMethods.WM_GETTEXT, (IntPtr)(len + 1), sb);
        var actual = sb.ToString().Trim('"').TrimEnd('\\');
        var expected = expectedPath.TrimEnd('\\');

        var match = actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        Log.Info($"[FolderJumpTool]   -> VerifyTextWritten: 读回='{actual}' 期望='{expected}' 匹配={match}");
        return match;
    }
}
