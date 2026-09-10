using System.Text;

namespace FolderJumpTool;

/// <summary>
/// 全局监听系统文件选择/保存对话框的出现、移动、关闭。
/// 用 SetWinEventHook 实现，不需要往目标进程注入 DLL。
/// </summary>
internal sealed class DialogEventWatcher : IDisposable
{
    // 必须保留这个委托的引用，否则会被 GC 回收导致回调地址失效崩溃
    private readonly NativeMethods.WinEventDelegate _callback;

    private IntPtr _hookDialogStart;
    private IntPtr _hookObjectShow;
    private IntPtr _hookForeground;
    private IntPtr _hookLocationChange;
    private IntPtr _hookDestroy;
    private IntPtr _hookNameChange;

    private readonly System.Windows.Threading.DispatcherTimer _syncTimer;
    private readonly System.Windows.Threading.DispatcherTimer _safetyNetTimer;
    private IntPtr _currentDialog = IntPtr.Zero;

    public event Action<IntPtr>? DialogOpened;
    public event Action<IntPtr, NativeMethods.RECT>? DialogMoved;
    public event Action<IntPtr>? DialogClosed;

    /// <summary>对话框重新成为前台窗口（用户点回它、或者它自己刚弹出来）。</summary>
    public event Action<IntPtr>? DialogActivated;

    /// <summary>对话框失去前台焦点（用户切到了别的窗口）——这时悬浮窗应该跟着隐藏，
    /// 不然会出现"对话框已经不是当前窗口了，但悬浮窗还浮在所有窗口最上层"的问题。</summary>
    public event Action<IntPtr>? DialogDeactivated;

    /// <summary>资源管理器窗口标题变化（打开文件夹窗口 / 窗口内导航到新目录 / 切标签页）。
    /// 用于事件驱动地记录浏览历史——比定时轮询更及时，且只在用户真正操作时触发。</summary>
    public event Action? ExplorerWindowChanged;

    /// <summary>上次因资源管理器标题变化触发通知的时间（800ms 节流用：
    /// 一次导航可能连发多次标题变化，没必要每次都去枚举窗口）。</summary>
    private DateTime _lastExplorerNotifyUtc = DateTime.MinValue;

    public DialogEventWatcher()
    {
        _callback = OnWinEvent;

        // 只对"已经确认在跟踪的那一个对话框"做位置同步和关闭检测，开销极小（单窗口查询），
        // 不是用来"发现"对话框的，检测本身现在完全事件驱动。
        _syncTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _syncTimer.Tick += (_, _) => SyncCurrentDialog();

        // 真正意义上的最后一道保险：如果 DIALOGSTART 和 OBJECT_SHOW 两个事件都没触发
        // （极端情况，比如 hook 注册和对话框弹出有竞态），每隔 1 秒兜底扫一次。
        // 正常情况下事件驱动早就先一步捕获到了，这个定时器大部分时候什么都不做。
        _safetyNetTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _safetyNetTimer.Tick += (_, _) => SafetyNetScan();
    }

    public void Start()
    {
        _hookDialogStart = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_DIALOGSTART, NativeMethods.EVENT_SYSTEM_DIALOGSTART,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        // 新版基于 IFileOpenDialog/IFileSaveDialog 的对话框（Vista 之后大部分程序用的都是这套）
        // 不保证会触发 EVENT_SYSTEM_DIALOGSTART，但几乎所有顶层窗口显示时都会触发 EVENT_OBJECT_SHOW，
        // 加上这个 hook 之后新旧两种对话框都能事件驱动地捕获到。这是主要检测路径。
        _hookObjectShow = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        // LOCATIONCHANGE 不在此常驻注册：它是系统里最高频的 WinEvent 之一
        // （caret 闪烁、列表滚动、任何窗口内元素移动都会触发），空闲常驻会让每个
        // 事件都跨进程进回调空转。改为跟踪对话框期间才装（InstallLocationHook），
        // 关闭/失联即卸，空闲时零事件开销。
        _hookDestroy = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.EVENT_OBJECT_DESTROY,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _hookForeground = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        // 资源管理器窗口标题变化 = 用户打开文件夹窗口 / 导航进子目录 / 切标签页
        // （窗口标题就是当前文件夹名）。用它驱动浏览历史记录，取代定时轮询：
        // 只有用户真正操作时才触发，比定频枚举更省、更及时。
        // 该事件全系统窗口都有（浏览器/终端标题变化等），回调里先用类名过滤。
        _hookNameChange = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_NAMECHANGE, NativeMethods.EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _syncTimer.Start();
        _safetyNetTimer.Start();
    }

    // 回调必须又快又不能抛异常，否则会影响系统消息处理。
    // 这里只做最基本的判断，真正耗时的控件查找丢到 DialogNavigator 里按需再做。
    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || idObject != NativeMethods.OBJID_WINDOW)
            return;

        try
        {
            switch (eventType)
            {
                case NativeMethods.EVENT_SYSTEM_DIALOGSTART:
                    HandleDialogStart(hwnd);
                    break;

                case NativeMethods.EVENT_OBJECT_SHOW:
                    // idObject == OBJID_WINDOW 表示这是"整个顶层窗口"变为可见，
                    // 而不是窗口内某个子元素（菜单项、列表项等）变可见——过滤掉后面这种情况，
                    // 否则回调触发频率会非常高。
                    if (idObject == NativeMethods.OBJID_WINDOW && hwnd != _currentDialog)
                        HandleDialogStart(hwnd);
                    break;

                case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                    if (hwnd == _currentDialog)
                        RaiseMoved(hwnd);
                    break;

                case NativeMethods.EVENT_OBJECT_DESTROY:
                    if (hwnd == _currentDialog)
                    {
                        ClearCurrentDialog();
                        DialogClosed?.Invoke(hwnd);
                    }
                    break;

                case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                    if (_currentDialog != IntPtr.Zero)
                    {
                        if (IsRelatedToCurrentDialog(hwnd))
                            DialogActivated?.Invoke(_currentDialog);
                        else
                            DialogDeactivated?.Invoke(_currentDialog);
                    }
                    break;

                case NativeMethods.EVENT_OBJECT_NAMECHANGE:
                    HandleExplorerNameChange(hwnd);
                    break;
            }
        }
        catch
        {
            // 全局 hook 回调里绝不能让异常抛出去，吞掉即可（可以自行加日志）。
        }
    }

    private void HandleDialogStart(IntPtr hwnd)
    {
        // 同一对话框的去重：DIALOGSTART / OBJECT_SHOW（以及 OBJECT_SHOW 多次连发）会
        // 对同一个 hwnd 重复进来，不去重会让 DialogOpened 触发两次 → 悬浮窗把
        // "枚举 Explorer 窗口 + 解析最近 .lnk + 构建候选列表"整套重活干两遍。
        if (hwnd == _currentDialog)
            return;

        // 注意：EVENT_SYSTEM_DIALOGSTART 对系统里任何程序弹出的任何对话框都会触发，
        // 不只是文件对话框，所以这里大部分调用会被过滤掉——这是正常现象，
        // 不逐条打印"被过滤"的日志，否则控制台会被刷屏。
        if (!IsFileDialogClass(hwnd))
            return;

        if (!DialogNavigator.IsShellDialog(hwnd))
            return;

        Log.Info($"[FolderJumpTool] 确认是文件/文件夹选择对话框，触发 DialogOpened，hwnd={hwnd}");
        _currentDialog = hwnd;
        InstallLocationHook(); // 开始跟踪后才需要 LOCATIONCHANGE（空闲时零事件开销）
        DialogOpened?.Invoke(hwnd);
        RaiseMoved(hwnd);
    }

    /// <summary>跟踪开始时装载 LOCATIONCHANGE hook（拖动跟随靠它事件驱动）；空闲不注册。</summary>
    private void InstallLocationHook()
    {
        if (_hookLocationChange != IntPtr.Zero)
            return;
        _hookLocationChange = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    /// <summary>跟踪结束即卸——LOCATIONCHANGE 是最高频 WinEvent，常驻会让空闲时回调风暴。</summary>
    private void UninstallLocationHook()
    {
        if (_hookLocationChange == IntPtr.Zero)
            return;
        NativeMethods.UnhookWinEvent(_hookLocationChange);
        _hookLocationChange = IntPtr.Zero;
    }

    /// <summary>清掉当前跟踪目标（对话框关闭/失联）：卸 LOCATIONCHANGE + 复位句柄。</summary>
    private void ClearCurrentDialog()
    {
        UninstallLocationHook();
        _currentDialog = IntPtr.Zero;
    }

    private void SyncCurrentDialog()
    {
        if (_currentDialog == IntPtr.Zero)
            return;

        if (!NativeMethods.IsWindow(_currentDialog))
        {
            var closed = _currentDialog;
            ClearCurrentDialog();
            DialogClosed?.Invoke(closed);
            return;
        }

        RaiseMoved(_currentDialog);
    }

    private void SafetyNetScan()
    {
        if (_currentDialog != IntPtr.Zero)
            return; // 已经在跟踪一个对话框了，不需要扫

        var found = FindVisibleShellDialog();
        if (found == IntPtr.Zero)
            return;

        Log.Info(
            $"[FolderJumpTool] 事件驱动检测都没捕获到，安全网扫描兜底找到 hwnd={found}（可以反馈这个场景，方便补充针对性的事件处理）");
        _currentDialog = found;
        InstallLocationHook();
        DialogOpened?.Invoke(found);
        RaiseMoved(found);
    }

    private static IntPtr FindVisibleShellDialog()
    {
        IntPtr result = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true; // 继续枚举

            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() != "#32770")
                return true;

            if (!DialogNavigator.IsShellDialog(hwnd))
                return true;

            result = hwnd;
            return false; // 找到了，停止枚举
        }, IntPtr.Zero);

        return result;
    }

    private void RaiseMoved(IntPtr hwnd)
    {
        // 一律用视觉矩形（剔除 DWM 阴影装饰），保证悬浮窗贴的是"看得见的边"。
        if (NativeMethods.GetVisualRect(hwnd, out var rect))
            DialogMoved?.Invoke(hwnd, rect);
    }

    private bool IsRelatedToCurrentDialog(IntPtr hwnd)
    {
        if (hwnd == _currentDialog)
            return true;

        // 对话框自己弹出的附属小窗口（比如"新建文件夹"的重命名输入框）会被系统当成
        // 独立的前台窗口，但它们的 Owner 是这个对话框——这种情况不算真的失焦。
        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        return owner == _currentDialog;
    }

    private static bool IsFileDialogClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() == "#32770"; // 系统公共对话框的窗口类名
    }

    /// <summary>
    /// 窗口标题变化：只关心资源管理器窗口（打开文件夹窗口 / 导航进子目录 / 切标签页
    /// 都会改标题）。非资源管理器窗口（浏览器、终端等的标题变化）直接忽略。
    /// 800ms 节流：一次导航可能连发多次标题变化，合并成一次"去枚举窗口记录"的请求。
    /// </summary>
    private void HandleExplorerNameChange(IntPtr hwnd)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastExplorerNotifyUtc).TotalMilliseconds < 800)
            return;

        var sb = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        var cls = sb.ToString();
        if (cls != "CabinetWClass" && cls != "ExploreWClass")
            return;

        _lastExplorerNotifyUtc = now;
        ExplorerWindowChanged?.Invoke();
    }

    public void Dispose()
    {
        _syncTimer.Stop();
        _safetyNetTimer.Stop();
        if (_hookDialogStart != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookDialogStart);
        if (_hookObjectShow != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookObjectShow);
        if (_hookForeground != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookForeground);
        UninstallLocationHook();
        if (_hookNameChange != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookNameChange);
        if (_hookDestroy != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hookDestroy);
    }
}
