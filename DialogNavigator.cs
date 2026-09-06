using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace FolderJumpTool;

/// <summary>对话框种类。区分"打开/保存文件"对话框与"选择文件夹"对话框，
/// 两者 UI 结构不同（前者有文件名输入框，后者没有），识别与跳转策略也不同。</summary>
public enum DialogKind
{
    /// <summary>不是系统文件/文件夹选择对话框。</summary>
    None,

    /// <summary>打开/保存文件对话框（有"文件名"输入框，IFileOpenDialog / 老式 GetOpenFileName）。</summary>
    FileDialog,

    /// <summary>选择文件夹对话框。既可能是新式 IFileOpenDialog 的 pick-folders 模式
    ///（与打开/保存文件对话框是同一个控件，仅按钮/标签文字不同），也可能是老式
    /// SHBrowseForFolder 树形浏览。两者的识别与跳转策略见 ClassifyDialog 与 ActivatePath。</summary>
    FolderPicker,
}

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
    private const int FOLDER_EDIT_ID = 0x480;    // 1152，SHBrowseForFolder 新样式的"文件夹:"输入框

    /// <summary>
    /// 判断 #32770 对话框的种类。核心思路：Windows 的"打开文件"与"选择文件夹"
    /// 新式对话框（IFileOpenDialog 家族）是同一个控件，只是模式不同——最可靠
    /// 的区分信号是**底部按钮文字**（"打开/保存" vs "选择文件夹"），而不是有没有
    /// 文件名输入框（pick-folders 模式可能仍保留输入框）。
    /// 判定顺序：① 按钮文字命中"选择文件夹/Select Folder" → FolderPicker
    ///          ② 有文件名框(0x47C) + 取消按钮 → FileDialog（打开/保存）
    ///          ③ 老式树形浏览 SysTreeView32 → FolderPicker（SHBrowseForFolder）
    ///          ④ 其余 → None
    /// 每次调用会顺带把子控件侦察日志写入 %LocalAppData%\FolderJumpTool\，
    /// 用于在用户机器上核对真实对话框结构（对话框种类存疑时看 dump 最直接）。
    /// </summary>
    public static DialogKind ClassifyDialog(IntPtr hwnd)
    {
        var btnTexts = new List<string>();
        bool hasTree = false;
        var comboEx = NativeMethods.GetDlgItem(hwnd, FILENAME_COMBO_ID);
        var cancelBtn = NativeMethods.GetDlgItem(hwnd, IDCANCEL);
        bool hasFileNameBox = comboEx != IntPtr.Zero && cancelBtn != IntPtr.Zero;

        NativeMethods.EnumChildWindows(hwnd, (child, _) =>
        {
            var cls = new StringBuilder(256);
            NativeMethods.GetClassName(child, cls, cls.Capacity);
            var className = cls.ToString();

            if (className == "SysTreeView32")
                hasTree = true;

            var txt = new StringBuilder(512);
            NativeMethods.GetWindowText(child, txt, txt.Capacity);
            var text = txt.ToString().Trim();
            if (text.Length == 0)
                return true;

            if (className == "Button")
                btnTexts.Add(text);
            return true;
        }, IntPtr.Zero);

        DumpStructure(hwnd, btnTexts);

        // ① pick-folders 模式：按钮文字是"选择文件夹"（中英文都覆盖，含助记符 &
        //    的情况用 Contains 而非全等）
        foreach (var t in btnTexts)
        {
            var normalized = t.Replace("&", "");
            if (normalized.Contains("选择文件夹") || normalized.Contains("Select Folder"))
                return DialogKind.FolderPicker;
        }

        // ② 常规打开/保存文件对话框
        if (hasFileNameBox)
            return DialogKind.FileDialog;

        // ③ 老式树形浏览选择文件夹（SHBrowseForFolder，含"文件夹:"输入框变体
        //    BIF_NEWDIALOGSTYLE —— 树 + Edit 并存）
        if (hasTree)
            return DialogKind.FolderPicker;

        return DialogKind.None;
    }

    // 节流：同一个对话框 2 秒内只 dump 一次（对话框激活/移动事件会反复触发分类）
    private static IntPtr _lastDumpHwnd;
    private static long _lastDumpTicks;

    /// <summary>把对话框的完整子控件树（类名/ID/文本，含空文本控件）写入
    /// %LocalAppData%\FolderJumpTool\dialog-dump.log，用于核对真实对话框结构。</summary>
    private static void DumpStructure(IntPtr hwnd, List<string> btnTexts)
    {
        try
        {
            var now = DateTime.UtcNow.Ticks;
            if (hwnd == _lastDumpHwnd && now - _lastDumpTicks < TimeSpan.FromSeconds(2).Ticks)
                return;
            _lastDumpHwnd = hwnd;
            _lastDumpTicks = now;

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderJumpTool");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "dialog-dump.log");

            var title = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            var header = $"[{DateTime.Now:HH:mm:ss.fff}] hwnd={hwnd} 标题='{title}' 判定按钮={string.Join(" / ", btnTexts)}";

            var lines = new List<string> { header };
            WalkChildren(hwnd, 0, lines);
            File.AppendAllLines(path, lines);
        }
        catch { /* dump 失败不影响主流程 */ }
    }

    /// <summary>递归枚举子窗口树（GetTopWindow + GetWindow(GW_HWNDNEXT) 手动遍历，
    /// EnumChildWindows 不给层级；直接子用 GetWindow 也是同样的链）。</summary>
    private static void WalkChildren(IntPtr parent, int depth, List<string> lines)
    {
        var child = NativeMethods.GetTopWindow(parent);
        while (child != IntPtr.Zero)
        {
            var cls = new StringBuilder(256);
            NativeMethods.GetClassName(child, cls, cls.Capacity);
            var className = cls.ToString();
            if (className.Length == 0)
                className = "(无类名)";

            var id = NativeMethods.GetDlgCtrlID(child);

            var txt = new StringBuilder(512);
            NativeMethods.GetWindowText(child, txt, txt.Capacity);
            var text = txt.ToString().Trim();

            // 只记值得看的：所有非 DirectUI 控件全记；DirectUI 内部海量元素忽略
            if (!className.Contains("DirectUI"))
            {
                var indent = new string(' ', Math.Min(depth * 2, 24));
                lines.Add($"{indent}{className} id=0x{id:X4} 文本='{text}'");
            }

            // 深入子级（DirectUI 内部结构不展开，避免几千行日志）
            if (!className.Contains("DirectUI"))
                WalkChildren(child, depth + 1, lines);

            child = NativeMethods.GetWindow(child, NativeMethods.GW_HWNDNEXT);
        }
    }

    /// <summary>是否是系统文件/文件夹选择对话框（watcher 的捕获过滤条件）。</summary>
    public static bool IsShellDialog(IntPtr hwnd) => ClassifyDialog(hwnd) != DialogKind.None;

    /// <summary>枚举直接子窗口，判断是否存在指定类名的控件。</summary>
    private static bool HasChildOfClass(IntPtr hwnd, string className)
    {
        bool found = false;
        NativeMethods.EnumChildWindows(hwnd, (child, _) =>
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(child, sb, sb.Capacity);
            if (sb.ToString() == className)
            {
                found = true;
                return false; // 找到即停
            }
            return true;
        }, IntPtr.Zero);
        return found;
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

        return WritePathAndTriggerOk(dialogHwnd, editCtrl, path);
    }

    /// <summary>
    /// 文件夹选择对话框（SHBrowseForFolder 新样式 / IFileDialog pick-folders）专用：
    /// 找到"文件夹:"输入框 (0x480)，写入完整目录路径，点击"选择文件夹"按钮完成选择。
    /// 实测结构（侦察日志）：Static"文件夹:" + Edit 0x480 + Button"选择文件夹"(IDOK)。
    /// 找不到该输入框（老式纯树形 SHBrowseForFolder 等）返回 false，由调用方退地址栏方案。
    /// </summary>
    public static bool NavigateFolderDialog(IntPtr dialogHwnd, string path)
    {
        if (!NativeMethods.IsWindow(dialogHwnd))
            return false;

        // 老式纯树形（SysTreeView32 无输入框）：GetDlgItem 拿不到 0x480，
        // 直接返回 false 让上层走退路。
        var editCtrl = NativeMethods.GetDlgItem(dialogHwnd, FOLDER_EDIT_ID);
        Log.Info($"[FolderJumpTool] NavigateFolderDialog: folderEdit={editCtrl}");
        if (editCtrl == IntPtr.Zero)
            return false;

        return WritePathAndTriggerOk(dialogHwnd, editCtrl, path);
    }

    /// <summary>把路径写进指定 Edit 并点击对话框的"确定"按钮（写 + 校验 + 点按钮三段）。</summary>
    private static bool WritePathAndTriggerOk(IntPtr dialogHwnd, IntPtr editCtrl, string path)
    {
        // 1. 写入路径。注意如果路径带空格，建议加双引号，
        //    系统对话框对带引号的路径也能正确解析。
        var textToSend = path.Contains(' ') && !path.StartsWith('"')
            ? $"\"{path}\""
            : path;

        NativeMethods.SendMessage(editCtrl, NativeMethods.WM_SETTEXT, IntPtr.Zero, textToSend);

        // 2. 校验一下是否真的写进去了
        var verified = VerifyTextWritten(editCtrl, path);
        Log.Info($"[FolderJumpTool]   -> 写入校验 = {verified}");

        // 3. 点击"打开/保存/选择文件夹"按钮触发导航。
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

    /// <summary>
    /// 文件夹选择器的目录跳转（地址栏键盘方案，作为"没有文件名输入框"的老式
    /// SHBrowseForFolder / 特殊模式的退路）。IFileDialog 系列支持 Ctrl+L 聚焦顶部
    /// 地址栏——模拟真实键盘输入路径 + Enter，让系统把目录导航到目标位置
    /// （不会关闭对话框，用户可继续浏览后点"选择文件夹"确认）。
    /// 模拟输入必须走 SendInput（进目标进程消息队列，被其模态循环正常处理），
    /// 直接 SendMessage 按键不会触发对话框的导航逻辑。
    /// </summary>
    /// <returns>true 表示已成功注入按键序列；导航是否生效由系统行为决定。</returns>
    public static async Task<bool> NavigateFolderPickerAsync(IntPtr dialogHwnd, string path)
    {
        if (!NativeMethods.IsWindow(dialogHwnd))
            return false;

        NativeMethods.SetForegroundWindow(dialogHwnd);
        await Task.Delay(120); // 等窗口真正拿到前台再发键

        // Ctrl+L：聚焦地址栏（文件对话框/文件夹选择器的通用快捷键）
        SendKey(NativeMethods.VK_CONTROL, isUp: false);
        SendKey(NativeMethods.VK_L, isUp: false);
        SendKey(NativeMethods.VK_L, isUp: true);
        SendKey(NativeMethods.VK_CONTROL, isUp: true);
        await Task.Delay(220); // DUI 地址栏聚焦是异步的，多留点余量

        // 逐字符输入完整路径（KEYEVENTF_UNICODE 支持中文等任意字符）
        foreach (var ch in path)
        {
            SendUnicodeChar(ch);
            await Task.Delay(10);
        }

        await Task.Delay(100); // 让最后一个字符落定
        SendKey(NativeMethods.VK_RETURN, isUp: false);
        SendKey(NativeMethods.VK_RETURN, isUp: true);
        return true;
    }

    /// <summary>发送一次虚拟键按下/抬起（用于 Ctrl、L、Enter 这类控制键）。</summary>
    private static void SendKey(ushort vk, bool isUp)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = isUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                },
            },
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>以 Unicode 扫描码注入一个字符（按下+抬起一对），路径中的任意文字都能打进去。</summary>
    private static void SendUnicodeChar(char c)
    {
        var down = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT { wScan = c, dwFlags = NativeMethods.KEYEVENTF_UNICODE },
            },
        };
        var up = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wScan = c,
                    dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
                },
            },
        };
        NativeMethods.SendInput(2, new[] { down, up }, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
