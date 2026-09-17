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

    /// <summary>发现文件/文件夹对话框。第二个参数是分类结果——在 watcher 里分类一次后
    /// 带出来复用，省掉上层再枚举一遍子控件（那一步要跨进程读每个子控件的类名与文字）。</summary>
    public event Action<IntPtr, DialogKind>? DialogOpened;
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

        // 真正意义上的最后一道保险：如果 DIALOGSTART / OBJECT_SHOW / FOREGROUND 都没捕获到
        // （极端情况，比如 hook 注册和对话框弹出有竞态），兜底扫一次。
        // 间隔 300ms（原为 1 秒）：扫一次只是 EnumWindows 遍历可见顶层窗口 + 读类名（~1ms），
        // 但兜底一旦用上，这个间隔就是用户感知的延迟上限——1 秒明显能感觉到"慢半拍"，
        // 300ms 基本无感。已在跟踪对话框时 Tick 直接 return，几乎零开销。
        _safetyNetTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
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
                    HandleDialogStart(hwnd, dwmsEventTime);
                    break;

                case NativeMethods.EVENT_OBJECT_SHOW:
                    // idObject == OBJID_WINDOW 表示这是"整个顶层窗口"变为可见，
                    // 而不是窗口内某个子元素（菜单项、列表项等）变可见——过滤掉后面这种情况，
                    // 否则回调触发频率会非常高。
                    if (idObject == NativeMethods.OBJID_WINDOW && hwnd != _currentDialog)
                        HandleDialogStart(hwnd, dwmsEventTime);
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
                    if (_currentDialog == IntPtr.Zero)
                    {
                        // 还没跟踪任何对话框：这个新成为前台的窗口本身可能就是刚弹出的文件对话框。
                        // 多这一条检测路径的原因：SHOW / DIALOGSTART 偶有漏检（日志里出现过只能
                        // 靠 1 秒兜底扫描才找到的情况），而"对话框成为前台"是它弹出时几乎必然
                        // 发生的事件——抓住它就能免掉那最多 1 秒的等待。
                        HandleDialogStart(hwnd, dwmsEventTime);
                    }
                    else if (IsRelatedToCurrentDialog(hwnd))
                    {
                        DialogActivated?.Invoke(_currentDialog);
                    }
                    else
                    {
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

    private void HandleDialogStart(IntPtr hwnd, uint eventTimeMs = 0)
    {
        // 同一对话框的去重：DIALOGSTART / OBJECT_SHOW（以及 OBJECT_SHOW 多次连发）会
        // 对同一个 hwnd 重复进来，不去重会让 DialogOpened 触发两次 → 悬浮窗把
        // "枚举 Explorer 窗口 + 解析最近 .lnk + 构建候选列表"整套重活干两遍。
        if (hwnd == _currentDialog)
            return;

        // 注意：DIALOGSTART / FOREGROUND 对系统里任何程序弹出的任何对话框都会触发，
        // 不只是文件对话框，所以这里大部分调用会被过滤掉——这是正常现象，
        // 不逐条打印"被过滤"的日志，否则控制台会被刷屏。
        // 先用最便宜的类名检查挡掉绝大多数，再做较贵的控件级分类。
        if (!IsFileDialogClass(hwnd))
            return;

        // 分类只做这一次，结果随事件带出去复用（上层原本还会再分一次）。
        // 已经判过"不是我们的目标"的窗口直接跳过：EVENT_OBJECT_SHOW 对同一个窗口可能触发多次，
        // 每次都重跑 ClassifyDialog（要枚举整棵子控件树、逐个跨进程读类名与文字）才是真正的浪费。
        if (_notOurDialog.Contains(hwnd))
            return;

        var kind = DialogNavigator.ClassifyDialog(hwnd);
        if (kind == DialogKind.None)
        {
            // 记住结论，避免同类窗口反复分类。上限兜底：窗口句柄会被系统复用，
            // 定期重置既是防集合膨胀，也避免因句柄复用而误跳过真正的新对话框。
            if (_notOurDialog.Count >= 128)
                _notOurDialog.Clear();
            _notOurDialog.Add(hwnd);

            // 记下"类名是 #32770、但不是我们认识的文件对话框"的场景。
            // 用途：用户想支持某些第三方软件的对话框（例如 WinRAR 的解压界面）时，
            // 靠这条日志就能拿到它的标题，据此判断该不该扩判定规则。
            // 按标题去重，同一个窗口反复出现只记一次，不刷屏。
            LogUnrecognizedDialog(hwnd);
            return;
        }

        // 事件延迟留痕：dwmsEventTime 是事件发生时刻（与 Environment.TickCount 同基准的毫秒数），
        // 差值大说明回调被积压的工作拖慢了——正是"对话框出来了、悬浮窗慢半拍才跟上"的形态。
        if (eventTimeMs != 0)
        {
            var lag = unchecked(Environment.TickCount - (int)eventTimeMs);
            if (lag >= 200)
                Log.Info($"[FolderJumpTool] 对话框事件延迟 {lag}ms（hwnd={hwnd}）：消息队列有积压");
        }

        Log.Info($"[FolderJumpTool] 确认是文件/文件夹选择对话框，触发 DialogOpened，hwnd={hwnd}");
        _currentDialog = hwnd;
        InstallLocationHook(); // 开始跟踪后才需要 LOCATIONCHANGE（空闲时零事件开销）
        DialogOpened?.Invoke(hwnd, kind);
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

        var (found, kind) = FindVisibleShellDialog();
        if (found == IntPtr.Zero)
            return;

        Log.Info(
            $"[FolderJumpTool] 事件驱动检测都没捕获到，安全网扫描兜底找到 hwnd={found}（可以反馈这个场景，方便补充针对性的事件处理）");
        _currentDialog = found;
        InstallLocationHook();
        DialogOpened?.Invoke(found, kind);
        RaiseMoved(found);
    }

    /// <summary>兜底扫描：找一个可见的、确实是目标的 shell 对话框，连同分类结果一起返回
    /// （避免调用方再枚举一遍子控件）。</summary>
    private static (IntPtr Hwnd, DialogKind Kind) FindVisibleShellDialog()
    {
        IntPtr result = IntPtr.Zero;
        var kind = DialogKind.None;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true; // 继续枚举

            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() != "#32770")
                return true;

            var k = DialogNavigator.ClassifyDialog(hwnd);
            if (k == DialogKind.None)
                return true;

            result = hwnd;
            kind = k;
            return false; // 找到了，停止枚举
        }, IntPtr.Zero);

        return (result, kind);
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

    /// <summary>已记录过的未识别对话框标题（按标题去重，避免同类窗口反复刷日志）。</summary>
    private static readonly HashSet<string> SeenUnrecognizedDialogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已判定"不是我们的目标对话框"的窗口句柄。缓存这个结论是因为分类本身不便宜
    /// （枚举整棵子控件树 + 逐个子控件跨进程读类名/文字），而 EVENT_OBJECT_SHOW 对同一窗口
    /// 可能反复触发。只在 hook 回调（UI 线程）访问，无需加锁。</summary>
    private readonly HashSet<IntPtr> _notOurDialog = new();

    /// <summary>
    /// 记录"是 #32770 但没被认定为文件对话框"的窗口（按标题去重）。
    /// 这是给"想支持某个第三方对话框"留的观测口：比如 WinRAR 的解压界面同样是 #32770，
    /// 但结构跟系统文件对话框不同，是否该认它需要先看实际标题再定。
    /// </summary>
    private static void LogUnrecognizedDialog(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0)
                return;

            lock (SeenUnrecognizedDialogs)
            {
                if (!SeenUnrecognizedDialogs.Add(title))
                    return; // 这个标题已经记过
            }

            Log.Info($"[FolderJumpTool] 未识别的 #32770 对话框：'{title}'（未弹悬浮窗；"
                + "若要支持它，可据此扩展 DialogNavigator.ClassifyDialog 的判定）");
        }
        catch
        {
            // 纯诊断，失败不影响主流程
        }
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
