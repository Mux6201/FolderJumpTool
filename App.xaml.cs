using System.Drawing;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace FolderJumpTool;

public partial class App : System.Windows.Application
{
    private DialogEventWatcher? _watcher;
    private OverlayWindow? _overlay;
    private FavoritesWindow? _favWindow;
    /// <summary>当前对话框是否为前台窗口。此标记在 UI 线程（Dispatcher.Invoke）写，
    /// 但 DialogMoved 等 hook 回调在钩子线程读——跨线程无同步时可能读到陈旧值
    /// （偶发表现为"拖动对话框悬浮窗不跟随，切一下前后台就好"），故用 volatile 保证可见性。</summary>
    private volatile bool _dialogIsForeground;

    /// <summary>当前正在跟踪的文件对话框句柄；悬浮窗残留看门狗用它判断对话框是否还活着。</summary>
    private IntPtr _dialogHwnd = IntPtr.Zero;

    /// <summary>被用户手动停用的对话框句柄（标题行 ×）：停用期间该对话框不再触发悬浮窗，
    /// 直到它关闭（DialogClosed）自动清除恢复。用于误判窗口 / 悬浮窗没正常消失的场景。</summary>
    private IntPtr _suppressedHwnd = IntPtr.Zero;

    /// <summary>当前跟踪对话框的种类（文件 vs 文件夹选择器），决定搜索过滤与跳转策略。</summary>
    private DialogKind _dialogKind;

    /// <summary>悬浮窗"残留"兜底：对话框已销毁/隐藏但悬浮窗仍浮在屏幕上时的最后保险。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _zombieTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };

    private WinForms.NotifyIcon? _trayIcon;
    private IntPtr _trayIconHandle;

    /// <summary>当前生效的主题是否为暗色（用于热切换时比对，避免重复重建字典）。</summary>
    private bool _isDarkTheme;

    /// <summary>主题跟随模式："System"=跟随系统 / "Light" / "Dark"=手动指定。</summary>
    private string _themeMode = "System";

    private WinForms.ToolStripMenuItem? _menuThemeGroup;
    private WinForms.ToolStripMenuItem? _menuThemeSystem;
    private WinForms.ToolStripMenuItem? _menuThemeLight;
    private WinForms.ToolStripMenuItem? _menuThemeDark;
    private WinForms.ToolStripMenuItem? _menuEsStatus;

    /// <summary>窗口背景材质：Solid（纯色，默认）/ Acrylic（亚克力）。
    /// 托盘"窗口材质"子菜单可切，存 settings.json；亚克力需 Win10 1607+（ACCENT）支持。</summary>
    private WindowMaterial _material = WindowMaterial.Solid;
    private WinForms.ToolStripMenuItem? _menuMaterialGroup;
    private WinForms.ToolStripMenuItem? _menuMaterialSolid;
    private WinForms.ToolStripMenuItem? _menuMaterialAcrylic;

    /// <summary>悬浮窗水平对齐："Left"/"Center"/"Right"，托盘菜单"悬浮窗位置"可改，默认靠左。</summary>
    private string _overlayAlign = "Left";
    private WinForms.ToolStripMenuItem? _menuAlignGroup;
    private WinForms.ToolStripMenuItem? _menuAlignLeft;
    private WinForms.ToolStripMenuItem? _menuAlignCenter;
    private WinForms.ToolStripMenuItem? _menuAlignRight;

    /// <summary>界面语言："System"=跟随系统 UI 语言 / "zh" / "en"，托盘"语言"子菜单可改。</summary>
    private string _lang = "System";
    private WinForms.ToolStripMenuItem? _menuHeader;
    private WinForms.ToolStripMenuItem? _menuFavorites;
    private WinForms.ToolStripMenuItem? _menuLangGroup;
    private WinForms.ToolStripMenuItem? _menuLangSystem;
    private WinForms.ToolStripMenuItem? _menuLangZh;
    private WinForms.ToolStripMenuItem? _menuLangEn;
    private WinForms.ToolStripMenuItem? _menuSearchGroup;
    private WinForms.ToolStripMenuItem? _menuSearchToggle;
    private WinForms.ToolStripMenuItem? _menuChooseEs;
    private WinForms.ToolStripMenuItem? _menuExit;

    /// <summary>Everything 搜索框是否显示（托盘"Everything 搜索"开关）。
    /// 配了 es.exe 也允许手动关掉搜索框；存 settings.json，默认开。</summary>
    private bool _searchEnabled = true;

    /// <summary>可用的 es.exe 路径；null = 未配置/未找到（悬浮窗不显示搜索框）。</summary>
    private string? _esPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 读取上次保存的语言与主题模式（settings.json），并加载对应资源字典。
        // 必须在 new OverlayWindow() 之前调用，否则资源查找不到键。
        _lang = SettingsStore.Get("language", "System");
        if (_lang is not ("zh" or "en"))
            _lang = "System";

        _themeMode = SettingsStore.Get("themeMode", "System");
        ApplyTheme(IsDarkForMode(_themeMode));

        // 窗口材质（settings.json）。枚举仅 Solid/Acrylic——旧版本里存过 "Mica"
        // （WPF 客户区无真云母、静态模拟与纯色无异，已移除），解析失败自动回落 Solid；
        // 亚克力需要系统支持（ACCENT，Win10 1607+），不支持时同样回落纯色。
        _material = Enum.TryParse<WindowMaterial>(
            SettingsStore.Get("windowMaterial", "Solid"), out var saved) ? saved : WindowMaterial.Solid;
        if (_material == WindowMaterial.Acrylic && !BackdropManager.IsSupported())
            _material = WindowMaterial.Solid;

        // 系统明暗主题变化通知（Settings 里改"应用模式"，或系统自动切换）。
        // 仅"跟随系统"模式下响应；手动指定浅/深色时忽略系统切换。
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // 注意：不弹调试控制台窗口。需要看实时日志时从终端运行或直接跑 exe
        // 并重定向输出即可——Log.Info 同时写 Console、Debug 与 %LocalAppData%\FolderJumpTool\app.log，
        // 无控制台时静默、不打扰。
        _overlay = new OverlayWindow();
        _overlay.SetMaterial(_material, _isDarkTheme); // 背景材质立即生效（句柄未就绪由 SourceInitialized 补应用）

        // 悬浮窗标题行 ×（手动关闭）：把当前对话框加入停用名单并立即隐藏悬浮窗。
        // 被停用的对话框在关闭前不再触发悬浮窗（DialogOpened/Moved/Activated 均拦下），
        // 关闭时（DialogClosed）自动清除恢复。这是误判窗口 / 悬浮窗没正常消失的手动兜底。
        _overlay.DismissRequested += () =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_dialogHwnd != IntPtr.Zero && NativeMethods.IsWindow(_dialogHwnd))
                {
                    _suppressedHwnd = _dialogHwnd;
                    Log.Info($"[FolderJumpTool] 停用对话框 hwnd={_suppressedHwnd}（关闭前不再弹悬浮窗）");
                }
                _dialogIsForeground = false;
                _overlay.HideOverlay();
            });
        };

        // 悬浮窗水平对齐（托盘菜单"悬浮窗位置"可改）：靠左/居中/靠右，默认靠左，存 settings.json。
        _overlayAlign = SettingsStore.Get("overlayAlign", "Left");
        if (_overlayAlign is not ("Left" or "Center" or "Right"))
            _overlayAlign = "Left";
        _overlay.SetAlign(_overlayAlign);

        _watcher = new DialogEventWatcher();

        // Everything 搜索：探测 es.exe（配置路径 > PATH > 安装目录）。
        // 找到了且"显示搜索框"开关开着（默认开，可手动关）才给悬浮窗开搜索框；
        // 否则保持现状（收藏/最近列表）。配置与开关分离，配了 es.exe 也能选择不显示。
        _esPath = EverythingSearch.FindEsExe(SettingsStore.Get("esPath"));
        _searchEnabled = SettingsStore.Get("searchEnabled", "true") != "false";
        _overlay.EnableSearch(_searchEnabled ? _esPath : null);

        // 系统托盘图标：常驻右下角，左键单击弹运行提示，
        // 右键菜单：主题切换（跟随系统/浅色/深色）+ 退出。
        // 这是程序唯一的可见存在感 + 退出入口（无主窗口）。
        SetupTrayIcon();

        _watcher.DialogOpened += hwnd =>
        {
            Dispatcher.Invoke(() =>
            {
                _dialogHwnd = hwnd;
                _dialogKind = DialogNavigator.ClassifyDialog(hwnd);

                // 停用名单命中：该对话框被用户手动关掉悬浮窗（标题行 ×）。
                // 仍登记状态（供 DialogClosed 清除名单），但不显示、不延迟复查。
                if (_suppressedHwnd != IntPtr.Zero && hwnd == _suppressedHwnd)
                {
                    _dialogIsForeground = false;
                    Log.Info($"[FolderJumpTool] DialogOpened hwnd={hwnd} 命中停用名单 → 不显示悬浮窗");
                    return;
                }

                // 全新对话框 = 全新会话：清掉上一个对话框残留的搜索关键词/结果/在途查询，
                // 回到收藏+最近候选。同一对话框拖动/重回前台时的"搜索结果保留"不在这里，
                // 走 DialogActivated 的 RefreshForForegroundDialog（下方），互不干扰。
                _overlay.ResetForNewDialog();
                _overlay.SetDialogKind(_dialogKind);

                // 只有对话框真的是前台窗口（或它的附属小窗在前台）才立即显示悬浮窗。
                // 程序启动/兜底扫描会捕获到"开着但被别的窗口盖住"的历史遗留对话框——
                // 那种情况直接弹会把悬浮窗孤零零顶到最上层（能看到悬浮窗、看不到对话框）。
                bool isForeground = IsDialogForegroundWindow(hwnd);
                if (isForeground)
                {
                    _dialogIsForeground = true;
                    // 用视觉矩形（GetVisualRect 剔除 DWM 阴影装饰），悬浮窗贴"看得见的边"
                    if (NativeMethods.GetVisualRect(hwnd, out var rect))
                        _overlay.FollowDialog(hwnd, rect);
                    Log.Info($"[FolderJumpTool] DialogOpened hwnd={hwnd} kind={_dialogKind} 前台=对话框 → 立即显示");
                }
                else
                {
                    // 对话框此刻在后台：登记状态但不显示，等用户切到它（DialogActivated）
                    // 时再弹。补一个短延迟复查——对话框刚弹出时前台切换可能晚于捕获事件。
                    _dialogIsForeground = false;
                    Log.Info($"[FolderJumpTool] DialogOpened hwnd={hwnd} kind={_dialogKind} 前台≠对话框(后台遗留) → 暂不显示，延迟复查");
                    DelayedShowIfForeground(hwnd);
                }
            });
        };

        /// <summary>对话框刚弹出/被捕获时前台可能还没切过来：延迟 300ms 复查一次，
        /// 是前台才真正显示悬浮窗；仍不是前台则保持隐藏等 DialogActivated。</summary>
        async void DelayedShowIfForeground(IntPtr hwnd)
        {
            await Task.Delay(300);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_dialogHwnd != hwnd || !NativeMethods.IsWindow(hwnd))
                    return; // 已被更新的对话框替换/已关闭
                if (!IsDialogForegroundWindow(hwnd))
                {
                    Log.Info($"[FolderJumpTool] DialogOpened 延迟复查: hwnd={hwnd} 仍非前台 → 保持隐藏，等 DialogActivated");
                    return;
                }
                _dialogIsForeground = true;
                if (NativeMethods.GetVisualRect(hwnd, out var rect))
                    _overlay.FollowDialog(hwnd, rect);
                Log.Info($"[FolderJumpTool] DialogOpened 延迟复查: hwnd={hwnd} 已是前台 → 显示");
            });
        }

        /// <summary>前台窗口是否是目标对话框（或对话框弹出的附属小窗，如重命名输入框）。</summary>
        bool IsDialogForegroundWindow(IntPtr hwnd)
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == hwnd)
                return true;
            return NativeMethods.GetWindow(fg, NativeMethods.GW_OWNER) == hwnd;
        }

        _watcher.DialogMoved += (hwnd, rect) =>
        {
            // 停用名单命中：位置同步不复活悬浮窗（× 后 150ms 定时器会反复触发这里）。
            if (_suppressedHwnd != IntPtr.Zero && hwnd == _suppressedHwnd)
            {
                _dialogIsForeground = false;
                return;
            }

            // 只有对话框还是前台窗口时才跟着重新定位/显示；
            // 否则失焦隐藏之后，这个每 150ms 触发一次的位置同步会把悬浮窗又弹出来。
            if (!_dialogIsForeground)
            {
                // 异常态诊断（偶发"拖动不跟随"现场）：对话框明明是前台窗口，
                // 跟随却被状态闸拦下 = 状态丢失 bug。正常后台场景不打（每 150ms 会刷）。
                if (IsDialogForegroundWindow(hwnd))
                {
                    Log.Info($"[FolderJumpTool] !! DialogMoved 被挡: fg=false 但前台确为对话框 hwnd={hwnd} 前台={NativeMethods.GetForegroundWindow()} overlay可见={_overlay?.IsVisible}");
                    _dialogIsForeground = true; // 状态自愈：前台确实是对话框，翻回 true
                }
                return;
            }

            Dispatcher.Invoke(() =>
            {
                _dialogHwnd = hwnd;
                _overlay.FollowDialog(hwnd, rect);
            });
        };

        _watcher.DialogActivated += hwnd =>
        {
            Dispatcher.Invoke(() =>
            {
                Log.Info($"[FolderJumpTool] DialogActivated hwnd={hwnd} 前状态: fg={_dialogIsForeground} overlay可见={_overlay.IsVisible} 前台={NativeMethods.GetForegroundWindow()}");
                // 停用名单命中：对话框回到前台也不复活悬浮窗（直到它关闭自动恢复）。
                if (_suppressedHwnd != IntPtr.Zero && hwnd == _suppressedHwnd)
                {
                    _dialogIsForeground = false;
                    Log.Info($"[FolderJumpTool] DialogActivated 命中停用名单 → 保持隐藏");
                    return;
                }
                _dialogIsForeground = true;
                _dialogHwnd = hwnd;
                _dialogKind = DialogNavigator.ClassifyDialog(hwnd);
                _overlay.SetDialogKind(_dialogKind);
                // 对话框重新回到前台（典型场景：用户去新开了资源管理器窗口/tab 再点回对话框，
                // 悬浮窗重新出现）——重新抓候选，让新窗口/激活 tab 按 Z 序自动置顶，
                // 而不是继续显示对话框刚弹出那一刻的旧快照。开销仅几十 ms，低频触发可忽略。
                // 注意：Everything 搜索模式（搜索框有字）下不重刷——否则拖动对话框/悬浮窗
                // 触发激活事件时，候选列表会把当前搜索结果顶掉。
                _overlay.RefreshForForegroundDialog();
                if (NativeMethods.GetVisualRect(hwnd, out var rect))
                    _overlay.FollowDialog(hwnd, rect); // FollowDialog 内部会在需要时重新 Show()
                Log.Info($"[FolderJumpTool] DialogActivated 处理完: overlay可见={_overlay.IsVisible}");
            });
        };

        _watcher.DialogDeactivated += _ =>
        {
            Dispatcher.Invoke(() =>
            {
                // 悬浮窗为 Everything 搜索被用户点击激活（前台就是它自己）时不视为
                // "对话框失焦"——否则一进搜索框窗口就被隐藏，搜索没法用。
                if (_overlay is { IsVisible: true } &&
                    NativeMethods.GetForegroundWindow() == _overlay.NativeHandle)
                {
                    Log.Info($"[FolderJumpTool] DialogDeactivated 忽略(前台=悬浮窗，搜索输入中)");
                    return;
                }

                Log.Info($"[FolderJumpTool] DialogDeactivated: 前台={NativeMethods.GetForegroundWindow()} overlay可见={_overlay.IsVisible} → 隐藏");
                _dialogIsForeground = false;
                _overlay.HideOverlay();
            });
        };

        _watcher.DialogClosed += _ =>
        {
            Dispatcher.Invoke(() =>
            {
                Log.Info($"[FolderJumpTool] DialogClosed: overlay可见={_overlay.IsVisible} → 重置会话并隐藏");
                _dialogIsForeground = false;
                _dialogHwnd = IntPtr.Zero;
                // 对话框关闭 = 停用到期：清除停用名单，下次任何对话框都恢复正常触发。
                _suppressedHwnd = IntPtr.Zero;
                // 对话框关闭 = 会话结束：清掉搜索关键词/结果，下次新弹窗从干净状态开始
                _overlay.ResetForNewDialog();
                _dialogKind = DialogKind.None;
                _overlay.SetDialogKind(DialogKind.None);
                _overlay.HideOverlay();
            });
        };

        _watcher.Start();

        // 悬浮窗残留兜底（看门狗）：对话框销毁/隐藏事件的钩子偶有漏网场景，
        // 只要悬浮窗可见而跟踪的对话框已不在，250ms 内强制隐藏，杜绝
        // "对话框没了、悬浮窗还钉在屏幕上关不掉"的僵尸窗口。
        _zombieTimer.Tick += (_, _) =>
        {
            if (_overlay == null || !_overlay.IsVisible)
                return;
            bool alive = _dialogHwnd != IntPtr.Zero &&
                         NativeMethods.IsWindow(_dialogHwnd) &&
                         NativeMethods.IsWindowVisible(_dialogHwnd);
            if (alive)
                return;
            Log.Info($"[FolderJumpTool] 看门狗：对话框已消失 (hwnd={_dialogHwnd})，收起悬浮窗");
            _dialogHwnd = IntPtr.Zero;
            _dialogKind = DialogKind.None;
            _suppressedHwnd = IntPtr.Zero; // 对话框消失同样到期停用名单，避免残留
            _overlay.HideOverlay();
        };
        _zombieTimer.Start();

        // ShutdownMode 设为 OnExplicitShutdown，因为这是个常驻后台的小工具，
        // 悬浮窗隐藏/显示不应该导致整个 App 退出；退出走托盘菜单 -> Shutdown()。
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退订系统主题事件，避免进程退出时回调悬空
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_trayIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }

        _watcher?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 系统托盘：运行时画一个 32px 蓝底白箭头图标（表示"跳转"），
    /// 左键单击弹运行提示；右键菜单做主题/语言/悬浮窗位置切换 + 退出。
    /// 菜单文案全部来自语言资源字典（S()），切换语言后由 UpdateTrayTexts() 刷新。
    /// </summary>
    private void SetupTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = S("Tray.Tooltip"),
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        _menuHeader = new WinForms.ToolStripMenuItem(S("Tray.Header")) { Enabled = false };
        menu.Items.Add(_menuHeader);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        // 收藏夹管理：打开管理窗口；收藏改动会同步刷新悬浮窗候选/星标。
        _menuFavorites = new WinForms.ToolStripMenuItem(S("Tray.Favorites"), null, (_, _) => OpenFavoritesWindow());
        menu.Items.Add(_menuFavorites);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        // 主题切换三选一：手动指定浅/深色，或回到跟随系统。
        // 与材质/位置同型（互斥单选），统一收进二级子菜单，避免一级菜单选项杂乱。
        // 选择会持久化到 %AppData%\FolderJumpTool\settings.json，下次启动仍生效。
        _menuThemeGroup = new WinForms.ToolStripMenuItem(S("Tray.Theme"));
        _menuThemeSystem = new WinForms.ToolStripMenuItem(S("Tray.ThemeSystem")) { Tag = "System" };
        _menuThemeLight = new WinForms.ToolStripMenuItem(S("Tray.ThemeLight")) { Tag = "Light" };
        _menuThemeDark = new WinForms.ToolStripMenuItem(S("Tray.ThemeDark")) { Tag = "Dark" };
        foreach (var item in new[] { _menuThemeSystem, _menuThemeLight, _menuThemeDark })
            item.Click += OnThemeMenuClick;
        _menuThemeGroup.DropDownItems.AddRange(
            new WinForms.ToolStripItem[] { _menuThemeSystem, _menuThemeLight, _menuThemeDark });
        menu.Items.Add(_menuThemeGroup);

        // 窗口材质二选一：纯色卡片（默认）/ 亚克力（毛玻璃）。
        // 即时应用并持久化。老系统（< Win10 1607）不支持亚克力，菜单项置灰。
        _menuMaterialGroup = new WinForms.ToolStripMenuItem(S("Tray.Material"));
        _menuMaterialSolid = new WinForms.ToolStripMenuItem(S("Tray.MaterialSolid")) { Tag = WindowMaterial.Solid.ToString() };
        _menuMaterialAcrylic = new WinForms.ToolStripMenuItem(S("Tray.MaterialAcrylic")) { Tag = WindowMaterial.Acrylic.ToString() };
        bool materialSupported = BackdropManager.IsSupported();
        foreach (var item in new[] { _menuMaterialSolid, _menuMaterialAcrylic })
            item.Click += OnMaterialMenuClick;
        if (!materialSupported)
        {
            _menuMaterialAcrylic.Enabled = false;
            _menuMaterialAcrylic.ToolTipText = S("Tray.MaterialUnsupported");
        }
        _menuMaterialGroup.DropDownItems.AddRange(
            new WinForms.ToolStripItem[] { _menuMaterialSolid, _menuMaterialAcrylic });
        menu.Items.Add(_menuMaterialGroup);

        // 悬浮窗位置：相对文件对话框的水平对齐三选一（默认靠左），存 settings.json。
        _menuAlignGroup = new WinForms.ToolStripMenuItem(S("Tray.Align"));
        _menuAlignLeft = new WinForms.ToolStripMenuItem(S("Tray.AlignLeft")) { Tag = "Left" };
        _menuAlignCenter = new WinForms.ToolStripMenuItem(S("Tray.AlignCenter")) { Tag = "Center" };
        _menuAlignRight = new WinForms.ToolStripMenuItem(S("Tray.AlignRight")) { Tag = "Right" };
        foreach (var item in new[] { _menuAlignLeft, _menuAlignCenter, _menuAlignRight })
            item.Click += OnAlignMenuClick;
        _menuAlignGroup.DropDownItems.AddRange(
            new WinForms.ToolStripItem[] { _menuAlignLeft, _menuAlignCenter, _menuAlignRight });
        menu.Items.Add(_menuAlignGroup);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // Everything 搜索组：显示开关（勾选 = 悬浮窗显示搜索框，配了 es.exe 也可手动关掉）
        // + 手动选择 es.exe。状态行（置灰）反映"未配置/已启用/搜索框已停用"。
        _menuSearchGroup = new WinForms.ToolStripMenuItem(S("Tray.Search"));
        _menuEsStatus = new WinForms.ToolStripMenuItem(EsStatusText()) { Enabled = false };
        _menuSearchToggle = new WinForms.ToolStripMenuItem(S("Tray.SearchToggle")) { CheckOnClick = true };
        _menuSearchToggle.Click += OnSearchToggleClick;
        _menuChooseEs = new WinForms.ToolStripMenuItem(S("Tray.ChooseEs"), null, (_, _) => ChooseEsExe());
        _menuSearchGroup.DropDownItems.Add(_menuEsStatus);
        _menuSearchGroup.DropDownItems.Add(new WinForms.ToolStripSeparator());
        _menuSearchGroup.DropDownItems.Add(_menuSearchToggle);
        _menuSearchGroup.DropDownItems.Add(new WinForms.ToolStripSeparator());
        _menuSearchGroup.DropDownItems.Add(_menuChooseEs);
        menu.Items.Add(_menuSearchGroup);

        // 语言三选一：跟随系统（中文系统→中文）/ 强制中文 / 强制 English，存 settings.json。
        _menuLangGroup = new WinForms.ToolStripMenuItem(S("Tray.Language"));
        _menuLangSystem = new WinForms.ToolStripMenuItem(S("Tray.LangSystem")) { Tag = "System" };
        _menuLangZh = new WinForms.ToolStripMenuItem(S("Tray.LangZh")) { Tag = "zh" };
        _menuLangEn = new WinForms.ToolStripMenuItem(S("Tray.LangEn")) { Tag = "en" };
        foreach (var item in new[] { _menuLangSystem, _menuLangZh, _menuLangEn })
            item.Click += OnLanguageMenuClick;
        _menuLangGroup.DropDownItems.AddRange(
            new WinForms.ToolStripItem[] { _menuLangSystem, _menuLangZh, _menuLangEn });
        menu.Items.Add(_menuLangGroup);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        _menuExit = new WinForms.ToolStripMenuItem(S("Tray.Exit"), null, (_, _) => Shutdown());
        menu.Items.Add(_menuExit);

        _trayIcon.ContextMenuStrip = menu;
        UpdateThemeMenuChecks();
        UpdateAlignMenuChecks();
        UpdateLanguageMenuChecks();
        UpdateMaterialMenuChecks();
        UpdateSearchMenuState();

        // 左键单击 = 弹个气泡确认在运行；防止误触，仅提示一次后靠悬浮窗本身验证也行，
        // 这里简单处理：每次左键都提示 2 秒，想关掉再右键菜单。
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                _trayIcon.ShowBalloonTip(2000, S("Tray.BalloonTitle"),
                    S("Tray.BalloonBody"),
                    WinForms.ToolTipIcon.Info);
        };
    }

    // ---------- 收藏夹管理窗口 ----------

    /// <summary>
    /// 打开/激活收藏夹管理窗口（单例）。收藏改动会触发 FavoritesChanged → 刷新悬浮窗候选，
    /// 悬浮窗里的星标状态、候选尾部的收藏段都会跟着变。
    /// </summary>
    private void OpenFavoritesWindow()
    {
        if (_favWindow == null)
        {
            _favWindow = new FavoritesWindow();
            _favWindow.SetMaterial(_material, _isDarkTheme); // 背景材质（Show 后 SourceInitialized 补 DWM）
            _favWindow.Closed += (_, _) => _favWindow = null;
            _favWindow.FavoritesChanged += () => _overlay?.RefreshCandidates();
        }

        if (_favWindow.IsVisible)
        {
            _favWindow.Activate();
            return;
        }

        _favWindow.Show();
        _favWindow.Activate();
    }

    /// <summary>
    /// 运行时画托盘图标：蓝底白色右箭头（跳转语义）。
    /// 底色 Fluent 蓝 #0067C0，深浅任务栏上都醒目，因此不随任务栏明暗换色。
    /// 画在 32px 画布上，底形为 28% 大圆角矩形——与收藏夹窗图标（Assets/
    /// favorites-window.ico）同款圆角造型，色系不同（收藏夹为焦糖橙）。
    /// 从圆形改为圆角矩形的原因：通知区多数程序图标是圆角方块/占满造型，
    /// 单独一个纯圆形显得与其他图标不搭；大圆角矩形占满画布（不透明边缘
    /// 留白会显得比旁边图标小一圈），系统缩到 16px 后仍和其他托盘图标同尺寸。
    /// 箭头头大杆短，16px 下仍能看清"向右跳转"的方向感。
    /// </summary>
    private Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 蓝底大圆角矩形：几何范围 [0.5, 31.5]，铺满画布但不越界。
            // 圆角比例与收藏夹窗图标一致（72/256 ≈ 28%），半径按矩形边长
            // 31 换算 ≈ 8.7px；GDI+ 路径绘制保证 16px 缩放下四角干净。
            // 注意不能用 (0,0,32,32)：GDI+ 填充边界会外扩到 32 这条线，
            // 超出位图最右/最下一列，右下边缘被硬切出截断感。
            using var bg = new SolidBrush(Color.FromArgb(0x00, 0x67, 0xC0));
            float radius = 31f * (72f / 256f);
            using var path = RoundedRectPath(0.5f, 0.5f, 31f, 31f, radius);
            g.FillPath(bg, path);

            // 白色实心右箭头（相应放大）：杆 y11-21（粗 10px），箭头头部 y6-26
            using var fg = new SolidBrush(Color.White);
            PointF[] arrow =
            {
                new(9, 11), new(17, 11), new(17, 6), new(27, 16), new(17, 26), new(17, 21), new(9, 21)
            };
            g.FillPolygon(fg, arrow);
        }

        _trayIconHandle = bmp.GetHicon();
        return Icon.FromHandle(_trayIconHandle);
    }

    /// <summary>构造 GDI+ 圆角矩形路径（四段弧 + 直线收尾，抗锯齿圆角）。
    /// radius 超矩形半短边时自动收敛到可画的最大圆角，避免 AddArc 越界。</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(
        float x, float y, float w, float h, float radius)
    {
        float d = Math.Min(radius * 2f, Math.Min(w, h));
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);            // 左上
        path.AddArc(x + w - d, y, d, d, 270, 90);    // 右上
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);  // 右下
        path.AddArc(x, y + h - d, d, d, 90, 90);     // 左下
        path.CloseFigure();
        return path;
    }

    /// <summary>手动主题菜单点击：切换到对应模式并持久化。</summary>
    private void OnThemeMenuClick(object? sender, EventArgs e)
    {
        if (sender is not WinForms.ToolStripMenuItem item || item.Tag is not string mode)
            return;
        if (mode == _themeMode)
            return;

        ApplyThemeMode(mode);
        Log.Info($"[FolderJumpTool] 手动主题切换: {mode}");
    }

    /// <summary>菜单勾选状态与当前模式同步。</summary>
    private void UpdateThemeMenuChecks()
    {
        if (_menuThemeSystem != null)
            _menuThemeSystem.Checked = _themeMode == "System";
        if (_menuThemeLight != null)
            _menuThemeLight.Checked = _themeMode == "Light";
        if (_menuThemeDark != null)
            _menuThemeDark.Checked = _themeMode == "Dark";
    }

    /// <summary>材质菜单点击：切换窗口材质并持久化；应用所有已打开窗口。</summary>
    private void OnMaterialMenuClick(object? sender, EventArgs e)
    {
        if (sender is not WinForms.ToolStripMenuItem item || item.Tag is not string tag)
            return;
        if (!Enum.TryParse<WindowMaterial>(tag, out var material) || material == _material)
            return;
        if (material != WindowMaterial.Solid && !BackdropManager.IsSupported())
            return; // 老系统不支持亚克力（菜单项已禁用，双重保险）

        _material = material;
        SettingsStore.Set("windowMaterial", material.ToString());
        UpdateMaterialMenuChecks();
        ApplyMaterialToAllWindows();
        Log.Info($"[FolderJumpTool] 窗口材质: {material}");
    }

    /// <summary>菜单勾选与当前材质同步。</summary>
    private void UpdateMaterialMenuChecks()
    {
        if (_menuMaterialSolid != null)
            _menuMaterialSolid.Checked = _material == WindowMaterial.Solid;
        if (_menuMaterialAcrylic != null)
            _menuMaterialAcrylic.Checked = _material == WindowMaterial.Acrylic;
    }

    /// <summary>Everything 搜索开关点击：切换搜索框显示并持久化（es.exe 未配置时开关不可用）。</summary>
    private void OnSearchToggleClick(object? sender, EventArgs e)
    {
        _searchEnabled = _menuSearchToggle is { Checked: true };
        SettingsStore.Set("searchEnabled", _searchEnabled ? "true" : "false");
        // 开关状态直接决定悬浮窗是否显示搜索框；es.exe 路径只作为"能不能开"的前提。
        _overlay?.EnableSearch(_searchEnabled ? _esPath : null);
        UpdateSearchMenuState();
        Log.Info($"[FolderJumpTool] Everything 搜索框: {(_searchEnabled ? "开" : "关")}");
    }

    /// <summary>同步 Everything 搜索菜单状态：开关勾选/可用性、状态行文案（未配置/启用/停用）。</summary>
    private void UpdateSearchMenuState()
    {
        if (_menuSearchToggle == null)
            return;

        bool configured = _esPath != null;
        _menuSearchToggle.Checked = _searchEnabled && configured;
        _menuSearchToggle.Enabled = configured; // 未配置 es.exe 时不允许"开"
        _menuSearchToggle.ToolTipText = configured
            ? string.Format(S("Tray.SearchToggleTip"), _esPath)
            : S("Tray.SearchToggleTipNoEs");

        if (_menuEsStatus != null)
            _menuEsStatus.Text = EsStatusText();
    }

    /// <summary>把当前材质/主题应用到所有已创建的窗口（主题切换后 tint 画刷会变，需重刷）。</summary>
    private void ApplyMaterialToAllWindows()
    {
        _overlay?.SetMaterial(_material, _isDarkTheme);
        _favWindow?.SetMaterial(_material, _isDarkTheme);
    }

    /// <summary>悬浮窗位置菜单点击：切换水平对齐并持久化。</summary>
    private void OnAlignMenuClick(object? sender, EventArgs e)
    {
        if (sender is not WinForms.ToolStripMenuItem item || item.Tag is not string align)
            return;
        if (align == _overlayAlign)
            return;

        _overlayAlign = align;
        SettingsStore.Set("overlayAlign", align);
        UpdateAlignMenuChecks();
        _overlay?.SetAlign(align); // 对话框正开着的话立即重定位
        Log.Info($"[FolderJumpTool] 悬浮窗位置: {align}");
    }

    /// <summary>菜单勾选与当前对齐方式同步。</summary>
    private void UpdateAlignMenuChecks()
    {
        if (_menuAlignLeft != null)
            _menuAlignLeft.Checked = _overlayAlign == "Left";
        if (_menuAlignCenter != null)
            _menuAlignCenter.Checked = _overlayAlign == "Center";
        if (_menuAlignRight != null)
            _menuAlignRight.Checked = _overlayAlign == "Right";
    }

    // ---------- 语言（托盘"语言"子菜单） ----------

    /// <summary>语言菜单点击：切换语言模式（System 跟随系统 / zh / en）并持久化。</summary>
    private void OnLanguageMenuClick(object? sender, EventArgs e)
    {
        if (sender is not WinForms.ToolStripMenuItem item || item.Tag is not string lang)
            return;
        if (lang == _lang)
            return;

        ApplyLanguage(lang);
        Log.Info($"[FolderJumpTool] 语言切换: mode={lang} effective={EffectiveLanguage()}");
    }

    /// <summary>应用语言：持久化 + 重建资源字典（悬浮窗 DynamicResource 字符串随之刷新）+ 同步菜单文案。</summary>
    private void ApplyLanguage(string lang)
    {
        if (lang is not ("zh" or "en"))
            lang = "System";
        if (lang == _lang)
            return;
        _lang = lang;
        SettingsStore.Set("language", lang);
        RebuildResources();
        UpdateLanguageMenuChecks();
        UpdateTrayTexts();
    }

    /// <summary>菜单勾选与当前语言模式同步（勾选的是"模式"，跟随系统时可能实际走中文/英文）。</summary>
    private void UpdateLanguageMenuChecks()
    {
        if (_menuLangSystem != null)
            _menuLangSystem.Checked = _lang == "System";
        if (_menuLangZh != null)
            _menuLangZh.Checked = _lang == "zh";
        if (_menuLangEn != null)
            _menuLangEn.Checked = _lang == "en";
    }

    /// <summary>实际生效的语言（zh / en）：手动指定优先，跟随系统则按系统 UI 语言判断。</summary>
    private string EffectiveLanguage()
    {
        if (_lang is "zh" or "en")
            return _lang;
        try
        {
            return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
                ? "zh"
                : "en";
        }
        catch
        {
            return "en";
        }
    }

    /// <summary>从当前语言资源字典取词；键不存在时原样返回键名（便于发现漏词）。</summary>
    private string S(string key) => TryFindResource(key) as string ?? key;

    /// <summary>按指定模式计算应使用暗色还是亮色。</summary>
    private bool IsDarkForMode(string mode) => mode switch
    {
        "Light" => false,
        "Dark" => true,
        _ => GetAppDarkTheme(), // System：跟随系统"应用模式"
    };

    /// <summary>切换主题模式：应用对应字典 + 持久化 + 同步菜单勾选。</summary>
    private void ApplyThemeMode(string mode)
    {
        _themeMode = mode;
        SettingsStore.Set("themeMode", mode);
        ApplyTheme(IsDarkForMode(mode));
        UpdateThemeMenuChecks();
    }

    /// <summary>读取系统当前"应用模式"是否为暗色（注册表 AppsUseLightTheme）。</summary>
    private static bool GetAppDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false; // 读不到就按亮色处理
        }
    }

    /// <summary>
    /// 应用主题（亮/暗）：重建全部资源字典（语言 + 主题）。
    /// 悬浮窗刷子/文案都是 DynamicResource，字典一换全部即时刷新。
    /// 托盘/WinForms 菜单是代码创建的，语言变化时需另外调用 UpdateTrayTexts()。
    /// </summary>
    private void ApplyTheme(bool dark)
    {
        _isDarkTheme = dark;
        RebuildResources();
        // 材质 tint 画刷随主题字典变（亚克力 tint / 纯色卡均换色），已建窗口需重刷背景
        ApplyMaterialToAllWindows();
        Log.Info($"[FolderJumpTool] 主题已切换: {(dark ? "Dark" : "Light")}  语言: {EffectiveLanguage()}");
    }

    /// <summary>重建合并字典：先语言字典（Strings.zh/en.xaml），后主题字典（Themes/Light|Dark.xaml）。键名不冲突。</summary>
    private void RebuildResources()
    {
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(EffectiveLanguage() == "zh"
                ? "Strings/Strings.zh.xaml"
                : "Strings/Strings.en.xaml", UriKind.Relative)
        });
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(_isDarkTheme ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        });
    }

    /// <summary>刷新托盘菜单/WinForms 文案（语言切换后调用；悬浮窗 XAML 走 DynamicResource 无需处理）。</summary>
    private void UpdateTrayTexts()
    {
        if (_trayIcon == null)
            return;
        _trayIcon.Text = S("Tray.Tooltip");
        if (_menuHeader != null)
            _menuHeader.Text = S("Tray.Header");
        if (_menuFavorites != null)
            _menuFavorites.Text = S("Tray.Favorites");
        if (_menuThemeGroup != null)
            _menuThemeGroup.Text = S("Tray.Theme");
        if (_menuThemeSystem != null)
            _menuThemeSystem.Text = S("Tray.ThemeSystem");
        if (_menuThemeLight != null)
            _menuThemeLight.Text = S("Tray.ThemeLight");
        if (_menuThemeDark != null)
            _menuThemeDark.Text = S("Tray.ThemeDark");
        if (_menuMaterialGroup != null)
            _menuMaterialGroup.Text = S("Tray.Material");
        if (_menuMaterialSolid != null)
            _menuMaterialSolid.Text = S("Tray.MaterialSolid");
        if (_menuMaterialAcrylic != null)
            _menuMaterialAcrylic.Text = S("Tray.MaterialAcrylic");
        if (_menuAlignGroup != null)
            _menuAlignGroup.Text = S("Tray.Align");
        if (_menuAlignLeft != null)
            _menuAlignLeft.Text = S("Tray.AlignLeft");
        if (_menuAlignCenter != null)
            _menuAlignCenter.Text = S("Tray.AlignCenter");
        if (_menuAlignRight != null)
            _menuAlignRight.Text = S("Tray.AlignRight");
        if (_menuSearchGroup != null)
            _menuSearchGroup.Text = S("Tray.Search");
        if (_menuSearchToggle != null)
            _menuSearchToggle.Text = S("Tray.SearchToggle");
        if (_menuEsStatus != null)
        {
            _menuEsStatus.Text = EsStatusText();
            _menuEsStatus.ToolTipText = _esPath ?? "";
        }
        if (_menuChooseEs != null)
            _menuChooseEs.Text = S("Tray.ChooseEs");
        if (_menuLangGroup != null)
            _menuLangGroup.Text = S("Tray.Language");
        if (_menuLangSystem != null)
            _menuLangSystem.Text = S("Tray.LangSystem");
        if (_menuLangZh != null)
            _menuLangZh.Text = S("Tray.LangZh");
        if (_menuLangEn != null)
            _menuLangEn.Text = S("Tray.LangEn");
        if (_menuExit != null)
            _menuExit.Text = S("Tray.Exit");

        // 开关勾选/可用性/tooltip 与状态行文案随语言与配置刷新
        UpdateSearchMenuState();
    }

    /// <summary>
    /// 系统偏好变化通知。SystemEvents 回调线程不保证是 UI 线程，
    /// 且并非每次通知都是明暗切换（任何 General 偏好变化都会进来），
    /// 所以统一回 UI 线程后重新读注册表比对，值没变就是无关通知，直接忽略。
    /// 只有"跟随系统"模式才响应系统明暗变化；手动浅/深色时忽略，避免被覆盖。
    /// </summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_themeMode != "System")
                return;

            var dark = GetAppDarkTheme();
            if (dark != _isDarkTheme)
                ApplyTheme(dark);
        });
    }

    // ---------- Everything 搜索配置（托盘菜单） ----------

    /// <summary>托盘菜单里 Everything 搜索的状态行文案（三态：未配置 / 已启用 / 搜索框停用）。</summary>
    private string EsStatusText() =>
        _esPath == null ? S("Tray.EsNotConfigured")
        : _searchEnabled ? S("Tray.EsEnabled")
        : S("Tray.EsHidden");

    /// <summary>手动选择一个 es.exe（自动探测全部落空时用）。选定即存配置；
    /// 搜索框是否显示由"显示搜索框"开关决定（默认开）。</summary>
    private void ChooseEsExe()
    {
        using var dlg = new WinForms.OpenFileDialog
        {
            Title = S("Es.ChooseTitle"),
            Filter = "es.exe|es.exe|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK)
            return;

        _esPath = EverythingSearch.FindEsExe(dlg.FileName);
        SettingsStore.Set("esPath", _esPath ?? "");

        // 只有"搜索框开关开着"才实际显示；关了则只保存配置路径，开关开启时再显示。
        _overlay?.EnableSearch(_searchEnabled ? _esPath : null);
        UpdateSearchMenuState();
        UpdateTrayTexts();

        _trayIcon?.ShowBalloonTip(2500, S("Tray.BalloonTitle"),
            _esPath == null
                ? S("Es.NotUsable")
                : string.Format(S("Es.EnabledMsg"), _esPath),
            WinForms.ToolTipIcon.Info);
    }
}
