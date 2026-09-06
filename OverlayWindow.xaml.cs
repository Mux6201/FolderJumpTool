using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FolderJumpTool;

public partial class OverlayWindow : Window
{
    private IntPtr _targetDialog = IntPtr.Zero;
    private NativeMethods.RECT _lastRect;

    /// <summary>可用的 es.exe 路径；null 表示未配置，搜索框隐藏。</summary>
    private string? _esPath;

    /// <summary>当前跟随对话框的种类：FolderPicker 时搜索结果只保留文件夹（folder: 语法）；
    /// FileDialog 时混合返回文件+文件夹（点击文件 = 直接完成文件选择）。</summary>
    private DialogKind _dialogKind;

    /// <summary>悬浮窗相对文件对话框的水平对齐：Left（靠左，默认）/ Center（居中）/ Right（靠右）。</summary>
    private string _align = "Left";

    /// <summary>当前窗口材质与明暗（托盘"窗口材质"切换；SourceInitialized/切换时应用 DWM）。</summary>
    private WindowMaterial _material = WindowMaterial.Solid;
    private bool _darkTheme;

    /// <summary>输入去抖：停止打字 220ms 后才真正查询，避免每个字符都拉起 es.exe。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce;

    /// <summary>查询序号，用于丢弃过期结果（旧查询晚到不能覆盖新关键词的结果）。</summary>
    private int _searchSeq;

    /// <summary>全局热键 Ctrl+G 的注册 ID；0 = 未注册（不可见时不注册，避免吞掉其他程序的 Ctrl+G）。</summary>
    private int _hotkeyId;

    /// <summary>自定义热键 ID（只要进程内唯一、非 0 即可）。</summary>
    private const int HotkeyId = 0x4777;

    /// <summary>用户点了标题行 ×（手动关闭）：请求 App 把当前对话框加入停用名单。
    /// App 收到后隐藏悬浮窗并记录——被停用的对话框不再触发悬浮窗，直到它关闭自动恢复。</summary>
    public event Action? DismissRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        RefreshCandidates();

        _searchDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220),
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RunSearch();
        };

        SourceInitialized += (_, _) =>
        {
            ApplyNoActivateStyle();
            ApplyMaterialNow(); // 句柄就绪后补一次材质应用（首次 Show 前 App 已 SetMaterial 存好字段）
        };

        // Ctrl+G 全局热键只在悬浮窗可见时生效：
        // 可见即注册（首选路径直达），隐藏即注销，不干扰其他程序使用 Ctrl+G。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                RegisterGlobalHotKey();
                ApplyMaterialNow(); // 窗口实际可见后再应用材质（ACCENT 需窗口显示后才渲染）
            }
            else
            {
                UnregisterGlobalHotKey();
            }
        };

        // 窗口自身尺寸变化（输入搜索词 → 条目变长/变多）时按当前对齐重定位：
        // FollowDialog 只在对话框矩形变化时才被调用，窗口自己变宽它不知道——
        // 结果就是居中/靠右模式下宽度只向右扩、看起来不居中，要等拖动对话框
        // 才"跳"回正确位置。置空 _lastRect 绕过短路强制重算（与 SetAlign 同款手法）。
        SizeChanged += (_, _) =>
        {
            if (IsVisible && _targetDialog != IntPtr.Zero &&
                NativeMethods.IsWindow(_targetDialog) &&
                NativeMethods.GetVisualRect(_targetDialog, out var rect))
            {
                _lastRect = default;
                FollowDialog(_targetDialog, rect);
            }
        };
    }

    /// <summary>注册 Ctrl+G 全局热键；失败（被其他程序占用）则放弃并留日志。</summary>
    private void RegisterGlobalHotKey()
    {
        if (_hotkeyId != 0)
            return; // 已注册
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        _hotkeyId = HotkeyId;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(Key.G);
        if (!NativeMethods.RegisterHotKey(hwnd, _hotkeyId, NativeMethods.MOD_CONTROL, vk))
        {
            _hotkeyId = 0;
            Log.Info("[FolderJumpTool] Ctrl+G 全局热键注册失败（可能被其他程序占用），跳过");
        }
    }

    private void UnregisterGlobalHotKey()
    {
        if (_hotkeyId == 0)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(hwnd, _hotkeyId);
        _hotkeyId = 0;
    }

    /// <summary>
    /// 配置或取消 Everything 搜索（es.exe 路径）。
    /// 传入 null/空 = 未配置：整个搜索框隐藏，界面与纯收藏版一致。
    /// </summary>
    public void EnableSearch(string? esPath)
    {
        _esPath = string.IsNullOrWhiteSpace(esPath) ? null : esPath;
        SearchRow.Visibility = _esPath == null ? Visibility.Collapsed : Visibility.Visible;

        if (_esPath == null && SearchBox.Text.Length > 0)
            SearchBox.Text = ""; // 清空会触发 TextChanged -> 恢复收藏/最近列表
    }

    /// <summary>
    /// 更新当前对话框种类（App 在对话框打开/激活/关闭时调用）。
    /// 类型变化且正处于搜索状态时，立即按新过滤规则重查一次
    /// （切到文件夹选择器 → 只出文件夹；切到文件对话框 → 恢复文件+文件夹）。
    /// </summary>
    public void SetDialogKind(DialogKind kind)
    {
        if (kind == _dialogKind)
            return;
        _dialogKind = kind;
        if (SearchBox.Text.Trim().Length > 0)
        {
            _searchDebounce.Stop();
            RunSearch();
        }
    }

    /// <summary>
    /// 切换水平对齐（靠左/居中/靠右，取值 Left/Center/Right）。
    /// 悬浮窗正跟着某个对话框时立即按新对齐重定位，无需重新弹框。
    /// </summary>
    public void SetAlign(string align)
    {
        if (align is not ("Left" or "Center" or "Right"))
            align = "Left";
        if (align == _align)
            return;
        _align = align;

        if (_targetDialog != IntPtr.Zero && IsVisible &&
            NativeMethods.IsWindow(_targetDialog) &&
            NativeMethods.GetVisualRect(_targetDialog, out var rect))
        {
            _lastRect = default; // 绕过"矩形没变就跳过"的短路，强制按新对齐重算一次
            FollowDialog(_targetDialog, rect);
        }
    }

    /// <summary>候选列表来源页签（仅在有持久化收藏时显示；无收藏整行隐藏回老版单列表）。
    /// 最近是默认主视图（老版"资源管理器 + 最近使用"），收藏只是附加页。</summary>
    private enum CandidateTab { Recent, Favorites }

    private CandidateTab _activeTab = CandidateTab.Recent;

    /// <summary>键盘上下导航的当前选中行索引；-1 = 无选中（鼠标 hover 高亮不受影响）。
    /// 列表数据源每次重建（换页签/搜索/清空）都要归零，索引对应的行视觉已销毁。</summary>
    private int _navIndex = -1;

    /// <summary>键盘选中行的高亮刷子（主题 NavHighlightBrush = 强调色低透明度，
    /// 区别于鼠标 hover 的 ItemHoverBrush；颜色在 Light/Dark 主题里定义，不在此现算）。</summary>
    private System.Windows.Media.SolidColorBrush? _navBrush;

    /// <summary>
    /// 重新拉取候选路径列表。
    /// 有收藏（favorites.json 非空）：分"最近 / 收藏"两页签，默认停最近页；
    /// 没有任何收藏：页签行隐藏，保持老版单一混合列表
    /// （已打开的资源管理器窗口 + 最近使用 + 桌面/下载/文档兜底展示）。
    /// 每次新对话框弹出/对话框重回前台时调用一次，保证列表是"新鲜"的；
    /// 顺便给每条打上"是否已在收藏夹"标记，驱动行尾星标的空心/实心。
    /// </summary>
    public void RefreshCandidates()
    {
        _navIndex = -1; // 数据源即将重建，旧选中索引作废

        var favorites = FavoritesManager.Load();

        if (favorites.Count == 0)
        {
            // 无收藏 = 老版形态：无页签，混合候选（资源管理器+最近+兜底目录）
            _activeTab = CandidateTab.Recent;
            TabRow.Visibility = Visibility.Collapsed;
            var items = RecentFoldersProvider.GetCandidates();
            MarkFavorites(items);
            FavoritesList.ItemsSource = items;
            UpdateLocationHint();
            UpdateEmptyHint(searchMode: false);
            return;
        }

        // 有收藏：页签模式。最近页（默认主视图）= 资源管理器+最近使用，
        // 收藏页 = 只列真实收藏（不再混兜底目录）
        var recentItems = RecentFoldersProvider.GetRecentAndExplorer();
        var favItems = new List<FavoriteFolder>(favorites);
        UpdateTabRow();

        var active = _activeTab == CandidateTab.Recent ? recentItems : favItems;
        MarkFavorites(active);
        FavoritesList.ItemsSource = active;
        UpdateLocationHint();
        UpdateEmptyHint(searchMode: false);
    }

    /// <summary>页签点击切换数据源；搜索模式页签已隐藏，不会触发。</summary>
    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b)
            return;
        var tab = ReferenceEquals(b, TabRecentButton) ? CandidateTab.Recent : CandidateTab.Favorites;
        if (tab == _activeTab)
            return;
        _activeTab = tab;
        RefreshCandidates();
    }

    /// <summary>更新页签行选中态：选中页 = 浅蓝 chip 底 + 主题蓝字；未选中 = 透明底 + 主文字色。
    /// 字号字重恒定，切换选中不引起字形宽度变化，两标签始终对齐。标签纯文字不带计数。</summary>
    private void UpdateTabRow()
    {
        bool recent = _activeTab == CandidateTab.Recent;
        var bgOn = GetBrush("ItemHoverBrush");
        var bgOff = System.Windows.Media.Brushes.Transparent;
        var fgOn = GetBrush("FolderIconHoverBrush");
        var fgOff = GetBrush("TextMainBrush");

        TabRecentButton.Background = recent ? bgOn : bgOff;
        TabFavButton.Background = recent ? bgOff : bgOn;
        if (TabRecentText != null) TabRecentText.Foreground = recent ? fgOn : fgOff;
        if (TabFavText != null) TabFavText.Foreground = recent ? fgOff : fgOn;
        TabRow.Visibility = Visibility.Visible;
    }

    /// <summary>对话框重新回到前台时调用：搜索模式（搜索框有字）保留现有结果不重刷——
    /// Everything 结果与当前关键词绑定，重抓候选会把搜索结果顶掉（表现为"拖动一下结果没了"）；
    /// 普通模式才刷新候选，让新开/切回的 Explorer 窗口按 Z 序置顶。</summary>
    public void RefreshForForegroundDialog()
    {
        if (SearchBox.Text.Trim().Length > 0)
            return; // 搜索模式：保留搜索结果
        RefreshCandidates();
    }

    /// <summary>
    /// 全新对话框会话开始/旧会话作废时调用（App 在 DialogOpened / DialogClosed 触发）：
    /// 清掉上一个对话框残留的搜索关键词、结果与在途查询，回到收藏/最近候选。
    /// 与 RefreshForForegroundDialog 的区别：后者用于"同一个对话框"拖动/重回前台，
    /// 需要保留搜索结果；这里是"换了一个对话框"，旧搜索没有意义，必须清干净，
    /// 否则重开一个文件选择器还会看到上一个选择器里搜出来的陈列表。
    /// </summary>
    public void ResetForNewDialog()
    {
        _searchDebounce.Stop();
        _searchSeq++; // 作废上一会话在途的 es 查询，防止迟到的结果写回新会话列表

        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = ""; // 触发 TextChanged 空文本分支 → RefreshCandidates()
        }
        else
        {
            RefreshCandidates();
        }
    }

    /// <summary>按当前收藏夹给候选打 IsFavorite 标记（星标实心/空心用）。</summary>
    private static void MarkFavorites(List<FavoriteFolder> items)
    {
        var favPaths = new HashSet<string>(
            FavoritesManager.Load().Select(f => f.Path?.TrimEnd('\\') ?? ""),
            StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
            it.IsFavorite = !string.IsNullOrEmpty(it.Path) && favPaths.Contains(it.Path.TrimEnd('\\'));
    }

    /// <summary>标题行右侧的候选位置数提示（仅普通列表模式显示；搜索模式被搜索框占据、隐藏）。</summary>
    private void UpdateLocationHint()
    {
        int n = CurrentCount();
        LocationHint.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (n == 0)
            return;
        var fmt = TryFindResource("Overlay.LocationCount") as string ?? "{0}";
        LocationHint.Text = string.Format(fmt, n);
    }

    /// <summary>列表条目数（兼容 List / IReadOnlyList）。</summary>
    private int CurrentCount() => FavoritesList.ItemsSource switch
    {
        System.Collections.ICollection c => c.Count,
        IReadOnlyCollection<FavoriteFolder> r => r.Count,
        _ => 0,
    };

    /// <summary>列表为空时显示对应来源的空状态提示（普通/搜索），并收起列表本身。</summary>
    private void UpdateEmptyHint(bool searchMode)
    {
        bool empty = CurrentCount() == 0;
        FavoritesList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHintFav.Visibility = empty && !searchMode ? Visibility.Visible : Visibility.Collapsed;
        EmptyHintSearch.Visibility = empty && searchMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>搜索框文字变化：清空恢复收藏/最近；有字则去抖后查询 Everything。
    /// 占位提示与清空按钮的显隐都跟文字长度走。</summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearButton.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            _searchDebounce.Stop();
            RefreshCandidates();
            return;
        }

        LocationHint.Visibility = Visibility.Collapsed; // 搜索模式：右上角计数让位
        TabRow.Visibility = Visibility.Collapsed;       // 搜索模式：页签让位（清空后由 RefreshCandidates 按收藏情况恢复）
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>搜索条内清空按钮：与 Esc 等效，一键回到收藏/最近列表，焦点留在输入框方便重输。</summary>
    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Length == 0)
            return;
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    /// <summary>搜索框获得键盘焦点：搜索条描边变主题蓝（扁平化焦点反馈，不抢背景色）。</summary>
    private void SearchBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        SearchFrame.BorderBrush = GetBrush("AccentBrush") ?? SearchFrame.BorderBrush;
    }

    /// <summary>搜索框失去键盘焦点：描边还原为普通卡片边框色。</summary>
    private void SearchBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        SearchFrame.BorderBrush = GetBrush("CardBorderBrush") ?? SearchFrame.BorderBrush;
    }

    /// <summary>真正发起 es.exe 查询（后台线程），回来时用序号防旧结果覆盖新结果。</summary>
    private void RunSearch()
    {
        if (_esPath == null)
            return;

        var keyword = SearchBox.Text.Trim();
        if (keyword.Length == 0)
        {
            RefreshCandidates();
            return;
        }

        var seq = ++_searchSeq;
        var es = _esPath;
        var foldersOnly = _dialogKind == DialogKind.FolderPicker;
        // 结果取 40 条再本地排序（Everything 只保证自己的排序，跳转相关性要客户端重排），
        // 列表可视高度 8 行，多的滚动查看。
        _ = Task.Run(() => EverythingSearch.Search(es, keyword, 40, foldersOnly))
            .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_searchSeq != seq)
                    return; // 已有更新的查询，这次的结果过期，丢弃

                var items = t.IsCompletedSuccessfully
                    ? t.Result
                    : new List<FavoriteFolder>();
                MarkFavorites(items);
                FavoritesList.ItemsSource = items;
                _navIndex = -1; // 搜索结果替换列表，键盘选中索引归零
                UpdateEmptyHint(searchMode: true);
            })));
    }

    /// <summary>搜索框键盘操作（候选列表与搜索结果共用同一列表，键盘导航两者通用）：
    /// ↑/↓：选中行上下移动（高亮 + 滚动跟随）；
    /// Enter：有选中行 → 跳选中行；无选中但搜索框有字 → 跳第一条（原行为）；
    ///        无选中且无字则不拦截（交给对话框自己的默认按钮）；
    /// Esc：清空关键词，立即恢复收藏/最近列表（配合 TextChanged 事件）。</summary>
    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = MoveNavSelection(e.Key == Key.Down ? 1 : -1);
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = ActivateNavOrFallback();
        }
    }

    /// <summary>键盘上下移动选中行。首次按键从"无选中"起步：↓ 选第一行、↑ 选最后一行；
    /// 到边界不再循环。返回 true 表示已处理（有列表可导航）。</summary>
    private bool MoveNavSelection(int delta)
    {
        if (FavoritesList.ItemsSource is not IReadOnlyList<FavoriteFolder> { Count: > 0 } list)
            return false;

        int next;
        if (_navIndex < 0)
            next = delta > 0 ? 0 : list.Count - 1;
        else
            next = Math.Clamp(_navIndex + delta, 0, list.Count - 1);

        if (next == _navIndex)
            return true; // 已在边界，按键被消费但不移动

        SetNavSelection(next);
        return true;
    }

    /// <summary>把高亮从旧行移到新行并滚动到可见（行高 30px，直接按行号算偏移）。</summary>
    private void SetNavSelection(int index)
    {
        ClearRowHighlight(_navIndex);
        _navIndex = index;
        ApplyRowHighlight(_navIndex);

        if (_navIndex >= 0)
            ListScroller.ScrollToVerticalOffset(_navIndex * 30.0);
    }

    /// <summary>行按钮高亮 = 主题强调色低透明度底（区别于鼠标 hover 的 ItemHoverBrush）。
    /// 直接赋本地值会压过样式的 IsMouseOver 触发器，所以本地值只在高亮行上；
    /// 清除时必须 ClearValue 还原，让 hover 背景重新生效。</summary>
    private void ApplyRowHighlight(int index)
    {
        if (index < 0 || GetRowButton(index) is not { } btn)
            return;
        if (_navBrush == null)
        {
            _navBrush = GetBrush("NavHighlightBrush") as System.Windows.Media.SolidColorBrush;
            if (_navBrush == null)
                return; // 主题缺键时不做高亮（正常主题必含，防御兜底）
        }
        btn.Background = _navBrush;
    }

    private void ClearRowHighlight(int index)
    {
        if (index < 0 || GetRowButton(index) is not { } btn)
            return;
        btn.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
    }

    /// <summary>取某行索引对应的行按钮（DataTemplate 根元素）。容器未生成时返回 null。</summary>
    private System.Windows.Controls.Button? GetRowButton(int index)
    {
        var container = FavoritesList.ItemContainerGenerator.ContainerFromIndex(index);
        return container == null ? null : FindDescendantButton(container);
    }

    /// <summary>深度优先找第一个 Button 后代（行根按钮位于容器最外层，星标等嵌套按钮在其内部，不会误取）。</summary>
    private static System.Windows.Controls.Button? FindDescendantButton(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.Button b)
                return b;
            if (FindDescendantButton(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    /// <summary>Enter 落点：优先键盘选中的行；其次搜索框有字时跳第一条（Ctrl+G 语义）；
    /// 都无则返回 false（不拦截 Enter，交给对话框默认按钮）。</summary>
    private bool ActivateNavOrFallback()
    {
        if (FavoritesList.ItemsSource is not IReadOnlyList<FavoriteFolder> { Count: > 0 } list)
            return false;
        if (_navIndex >= 0 && _navIndex < list.Count)
        {
            ActivatePath(list[_navIndex].Path);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            JumpToFirst();
            return true;
        }
        return false;
    }

    /// <summary>跳到当前列表第一条（首选路径）。Ctrl+G 全局热键与搜索框回车共用；
    /// 悬浮窗不可见时不动作（热键那时本已注销，双保险）。</summary>
    private void JumpToFirst()
    {
        if (!IsVisible)
            return;
        if (FavoritesList.ItemsSource is not IReadOnlyList<FavoriteFolder> { Count: > 0 } list)
            return;
        var path = list[0].Path;
        if (!string.IsNullOrWhiteSpace(path))
            ActivatePath(path);
    }

    /// <summary>悬浮窗是无激活窗口（ShowActivated=False + 行点击拦截激活），
    /// 但 Everything 搜索框需要真实键盘焦点——点击搜索框时放行激活，
    /// 让窗口成为前台窗口、文本框获得输入。这里手动补一次 Activate+Focus
    /// （WM_MOUSEACTIVATE 默认路径已放行，双保险，不改变对话框跟踪状态）。</summary>
    private void SearchBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsVisible && !IsActive)
            Activate();
        if (!SearchBox.IsKeyboardFocusWithin)
            Keyboard.Focus(SearchBox);
    }

    /// <summary>
    /// 悬浮窗常驻不抢焦点策略：窗口不带 WS_EX_NOACTIVATE（那样会连搜索框的
    /// 键盘焦点一起锁死——非激活窗口永远收不到按键，Everything 搜索就无法输入）。
    /// 改为 ShowActivated=False（Show 时不让它成为前台窗口）+ 显式接管
    /// WM_MOUSEACTIVATE：点击条目行返回 MA_NOACTIVATE 不抢对话框焦点；
    /// 只有点击搜索框时放行默认激活，让文本框能正常接收键盘输入。
    /// </summary>
    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW);

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProcHook);
    }

    /// <summary>切换窗口材质（纯色/亚克力）。App 在启动、托盘切换、主题切换时调用。</summary>
    public void SetMaterial(WindowMaterial material, bool darkTheme)
    {
        _material = material;
        _darkTheme = darkTheme;
        ApplyMaterialNow();
    }

    /// <summary>
    /// 把当前材质落到悬浮窗（分层窗口，真 alpha 合成）：
    ///  Solid：卡片底色 = 不透明 CardBackgroundBrush，清除 ACCENT 模糊；
    ///  Acrylic：ACCENT 真模糊（SetWindowCompositionAttribute），WPF 层盖半透明 tint
    ///           （MaterialAcrylicBrush）保证可读——模糊与底色均由 DWM 与 WPF 分层合成。
    /// 亚克力的内容底切直角 + 不裁 region：ACCENT 模糊作用于整个矩形窗口，
    /// 圆角底会露出矩形 halo，内容底与模糊同为直角才无轮廓（WPF 自绘圆角只在 Solid 用）。
    /// </summary>
    private void ApplyMaterialNow()
    {
        System.Windows.Media.Brush? brush = BackdropManager.GetSurfaceBrush(_material);
        if (brush != null)
            CardBorder.Background = brush;

        // 搜索框底跟随材质：亚克力用 60% 半透明版（透出玻璃感），纯色用实心版。
        // SetResourceReference 让键随主题字典重建自动换值（本地赋值会杀死 DynamicResource）。
        SearchFrame.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
            _material == WindowMaterial.Acrylic ? "AcrylicInputBackgroundBrush" : "InputBackgroundBrush");

        if (_material == WindowMaterial.Acrylic)
        {
            // ACCENT tint 直接透传主题画刷原色（alpha 一并取自主题，不再在 code-behind
            // 二次覆盖）——调透明度只改 Themes/Light.xaml + Dark.xaml 的
            // MaterialAcrylicBrush alpha 一处即可，WPF 层与 DWM 层自动同步。
            var c = (brush as System.Windows.Media.SolidColorBrush)?.Color
                    ?? System.Windows.Media.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);
            BackdropManager.ApplyAcrylicAccent(this, c);
            CardBorder.CornerRadius = new System.Windows.CornerRadius(0);
        }
        else
        {
            CardBorder.CornerRadius = new System.Windows.CornerRadius(6);
            BackdropManager.ClearAcrylic(this);
        }
    }

    /// <summary>窗口消息钩子：WM_MOUSEACTIVATE 时区分点击目标——点击搜索框放行激活
    /// （获得键盘输入能力），点击其他区域返回 MA_NOACTIVATE 不抢对话框焦点；
    /// 处理 WM_HOTKEY（Ctrl+G）触发首选路径直达。</summary>
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            if (!IsClickInSearchBox())
            {
                handled = true;
                return (IntPtr)NativeMethods.MA_NOACTIVATE;
            }
            // 命中搜索框：不拦截，走系统默认 = 允许激活（handled 保持 false）
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_HOTKEY && _hotkeyId != 0 && wParam == (IntPtr)_hotkeyId)
        {
            JumpToFirst();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>WM_MOUSEACTIVATE 时判断鼠标点击是否落在搜索框内（决定放行激活与否）。
    /// 搜索框隐藏/未配置时恒返回 false——点击任何地方都不抢焦点。</summary>
    private bool IsClickInSearchBox()
    {
        if (SearchRow.Visibility != Visibility.Visible)
            return false;
        if (SearchBox.ActualWidth <= 0 || SearchBox.ActualHeight <= 0)
            return false;

        var pt = Mouse.GetPosition(SearchBox);
        return pt.X >= 0 && pt.Y >= 0 &&
               pt.X <= SearchBox.ActualWidth && pt.Y <= SearchBox.ActualHeight;
    }

    /// <summary>悬浮窗句柄（App 失焦守卫判断"前台是否就是我们自己"用）。</summary>
    public IntPtr NativeHandle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 跟随目标对话框重新定位：默认贴在对话框下方靠左（和 Listary 一致），
    /// 水平对齐可切左/中/右（见 SetAlign）；候选多时按可用屏幕空间自适应限高；
    /// 下方空间不够依次尝试贴上方，最后无论如何都会把窗口钳制在屏幕可见范围内，
    /// 保证不会出现"算出来的位置飞到屏幕外"的情况。
    /// </summary>
    internal void FollowDialog(IntPtr dialogHwnd, NativeMethods.RECT dialogRect)
    {
        _targetDialog = dialogHwnd;

        // 位置和上次完全一样就不用重复算/重复打日志了，
        // 位置同步定时器每 150ms 跑一次，大部分时候对话框根本没动。
        if (dialogRect.Equals(_lastRect) && IsVisible)
            return;
        _lastRect = dialogRect;

        // 屏幕度量（WPF 逻辑像素；系统缩放 100% 时与 GetVisualRect 的物理像素 1:1）。
        double screenWidth = SystemParameters.VirtualScreenWidth;
        double screenHeight = SystemParameters.VirtualScreenHeight;
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        // 视觉贴合距离：窗口外框与对话框之间的间隔。
        // 卡片四周已无透明留白/阴影（Border.Margin=0、无 DropShadow），
        // 窗口框 = 卡片视觉边缘，因此这个 gap 就是用户看到的实际空隙。
        // 顶部要"再贴一些"就把 gap 往下压；留 1px 避免零间隙显得粘滞。
        const int gap = 1;

        // 自适应限高：候选很多（搜索模式最多 20 条）时列表区不能无限长。
        // 基础可视上限 240px（≈8 行 x 30px/行，用户指定"压缩到 8 条左右"），
        // 再按对话框上/下可用的屏幕空间收紧——
        // 取两侧可用空间较大的一侧，减去标题行等固定开销（约 44px），
        // 保证悬浮窗总高不超过它最终落脚那侧的可用区域（下方不够会翻到上方）。
        // 留 96px 下限，避免对话框几乎占满屏幕时悬浮窗被压到只剩一条。
        double availBelow = screenTop + screenHeight - (dialogRect.Bottom + gap);
        double availAbove = dialogRect.Top - screenTop - gap;
        // 页签行可见时多占 ~30px 高（仅候选模式；搜索模式页签隐藏），从列表高度预算里扣掉，
        // 避免整窗超出对话框侧可用空间被钳制回弹。
        double tabOverhead = TabRow.Visibility == Visibility.Visible ? 30 : 0;
        double listMax = Math.Max(96, Math.Min(240, Math.Max(availBelow, availAbove) - (44 + tabOverhead)));
        if (Math.Abs(ListScroller.MaxHeight - listMax) > 0.5)
            ListScroller.MaxHeight = listMax;

        // 第一次显示前，SizeToContent 窗口的 Width/Height 是 NaN（还没走过布局），
        // 用 NaN 算 Left 会导致窗口飞到屏幕外或者干脆不显示。
        // 所以先在屏幕外 Show 一次，强制走一遍布局拿到真实尺寸，再重新定位。
        if (!IsVisible)
        {
            Log.Info("[FolderJumpTool] FollowDialog: 窗口当前不可见 → 重新 Show()");
            Left = -5000;
            Top = -5000;
            Show();
            Log.Info($"[FolderJumpTool] FollowDialog: Show() 后 IsVisible={IsVisible}");
        }

        // 每次都强制走一遍布局再测量尺寸，而不是只在第一次显示时测——
        // 候选列表内容会变化（比如 RefreshCandidates 之后条目变多/变少），
        // 尺寸跟着变了但 ActualWidth/ActualHeight 不会自动刷新，
        // 用旧尺寸算位置会导致"贴下方空间够不够"判断错误，最终和对话框重叠。
        UpdateLayout();

        double actualWidth = ActualWidth > 0 ? ActualWidth : 200;   // 兜底默认宽高
        double actualHeight = ActualHeight > 0 ? ActualHeight : 120;

        // 水平对齐（由托盘菜单"悬浮窗位置"选择）：默认靠左与对话框可见左缘平齐，
        // 可选相对对话框居中，或靠右（右缘对齐对话框右缘）。
        // 边界情况：对话框被拖得很窄、宽度小于悬浮窗时，任何档位都无法让悬浮窗
        // 完整落在对话框横向范围内——居中/靠右会算出"悬空凸出"的坐标（甚至为负）。
        // 此时改走下拉面板语义：悬浮窗宽于宿主就从宿主左缘向右展开（最顺眼）；
        // 若右缘放不下（对话框贴屏幕右侧）则改从右缘向左展开，仍不放不下再交给
        // 下面的超屏回收兜底。三档在窄宿主下退化为同一种"贴边展开"，行为稳定。
        double dialogWidth = dialogRect.Right - dialogRect.Left;
        double left;
        if (actualWidth >= dialogWidth)
        {
            left = dialogRect.Left;
            if (left + actualWidth > screenLeft + screenWidth)
                left = dialogRect.Right - actualWidth;
        }
        else
        {
            left = _align switch
            {
                "Center" => dialogRect.Left + (dialogWidth - actualWidth) / 2,
                "Right" => dialogRect.Right - actualWidth,
                _ => dialogRect.Left,
            };
        }
        double top = dialogRect.Bottom + gap;

        // 水平超出屏幕就往回收（居中/靠右时对话框贴屏幕边可能算出界）
        if (left + actualWidth > screenLeft + screenWidth)
            left = screenLeft + screenWidth - actualWidth - gap;

        // 下方空间不够（比如对话框贴着屏幕底部）就改贴到上方
        if (top + actualHeight > screenTop + screenHeight)
            top = dialogRect.Top - actualHeight - gap;

        // 最后再兜底一次：不管上面算出什么，强制把窗口整体钳制在可见屏幕范围内。
        // 之前出现过候选列表太高、上下都放不下导致 Top 算出负数飞出屏幕的情况，
        // 靠这一步保证悬浮窗任何时候都是完整可见、可点击的。
        left = Math.Max(screenLeft, Math.Min(left, screenLeft + screenWidth - actualWidth));
        top = Math.Max(screenTop, Math.Min(top, screenTop + screenHeight - actualHeight));

        Left = left;
        Top = top;
    }

    public void HideOverlay()
    {
        Log.Info("[FolderJumpTool] OverlayWindow.HideOverlay() 调用");
        Hide();
    }

    // ---------- 标题行 × ：手动关闭出口（hover 卡片才淡入，防误判/防残留） ----------

    /// <summary>鼠标进入卡片：× 淡入并可点（80ms，与行内星标手感一致）。</summary>
    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DismissButton == null || DismissButton.IsHitTestVisible)
            return; // 已在显示状态，不重复动画
        DismissButton.IsHitTestVisible = true;
        DismissButton.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(80)));
    }

    /// <summary>鼠标离开卡片：× 淡出并禁点（离开窗口即收回，不常驻干扰）。</summary>
    private void Card_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DismissButton == null || !DismissButton.IsHitTestVisible)
            return;
        DismissButton.IsHitTestVisible = false;
        DismissButton.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(80)));
    }

    /// <summary>点 × ：向 App 请求停用当前对话框（该对话框不再弹悬浮窗，关闭后自动恢复）。
    /// 这是误判窗口 / 悬浮窗没正常消失时的手动兜底出口，与看门狗互补。</summary>
    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("[FolderJumpTool] 标题行 × 点击 → 请求停用当前对话框");
        DismissRequested?.Invoke();
    }

    // ---------- 行 hover：文字加粗 + 图标变色 + 星标出现（事件驱动；DataTemplate.Triggers 的动画在此环境不可靠） ----------

    /// <summary>行加载完成：按收藏状态初始化星标。已收藏 = 实心蓝星常显可点；未收藏 = 隐藏待 hover。
    /// 文件行（搜索结果里的文件）不能收藏——星标对文件夹才有意义，永久隐藏。</summary>
    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button row ||
            row.DataContext is not FavoriteFolder item)
            return;

        var star = FindElement<System.Windows.Controls.Button>(row, "StarButton");
        if (star == null)
            return;

        if (!item.IsDirectory)
        {
            // 文件行：星标隐藏且不可点，hover 也不再淡入（见 Row_MouseEnter 的 IsDirectory 守卫）
            star.BeginAnimation(OpacityProperty, null);
            star.Opacity = 0;
            star.IsHitTestVisible = false;
            return;
        }

        bool fav = item.IsFavorite;
        star.BeginAnimation(OpacityProperty, null);   // 清掉可能残留的进出场动画
        star.Opacity = fav ? 1 : 0;
        star.IsHitTestVisible = fav;
        SetGlyph(star, fav ? "\uE735" : "\uE734");    // E735 实心 / E734 空心

        if (fav)
        {
            star.Foreground = GetBrush("FolderIconHoverBrush") ?? star.Foreground;
            star.ToolTip = TryFindResource("Overlay.RemoveFav") as string ?? star.ToolTip;
        }
    }

    /// <summary>鼠标进入行：名称变加粗（图标为系统位图不可染色，只做字重反馈）；
    /// 未收藏的目录行星标淡入弹出（文件行不出星标）。弹入参数收敛：
    /// BackEase 幅度 0.25 + 从 0.9 起跳（早先 0.35/0.85 弹跳感太强，微调后更克制）。</summary>
    private void Row_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button row)
            return;

        SetRowFontWeight(row, isHover: true);

        // 未收藏目录条目：星标淡入 + 0.9→1 轻弹入
        if (row.DataContext is FavoriteFolder { IsFavorite: false, IsDirectory: true } &&
            FindElement<System.Windows.Controls.Button>(row, "StarButton") is { } star)
        {
            star.IsHitTestVisible = true;
            star.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(80)));
            AnimateScale(row, "StarScale", 1, 120, from: 0.9, amplitude: 0.25);
        }
    }

    /// <summary>鼠标离开行：字重还原；未收藏目录条目星标淡出收回（收藏常显不动）。
    /// 收回参数与进入对称：回到 0.9 起点，时长 80ms。</summary>
    private void Row_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button row)
            return;

        SetRowFontWeight(row, isHover: false);

        if (row.DataContext is FavoriteFolder { IsFavorite: false, IsDirectory: true } &&
            FindElement<System.Windows.Controls.Button>(row, "StarButton") is { } star)
        {
            star.IsHitTestVisible = false;
            star.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(60)));
            AnimateScale(row, "StarScale", 0.9, 80);
        }
    }

    /// <summary>切换行内文字字重：hover 时仅名称加粗（路径保持 Normal，
    /// 避免整行视觉过重），移出还原。名称列宽固定，加粗只是文字在列内变宽，
    /// 不会顶动其他列，也不会有布局跳动。</summary>
    private static void SetRowFontWeight(System.Windows.Controls.Button row, bool isHover)
    {
        if (FindElement<TextBlock>(row, "NameText") is { } name)
            name.FontWeight = isHover ? FontWeights.Bold : FontWeights.Normal;
    }

    /// <summary>对模板内命名 ScaleTransform（StarScale，Freezable）做动画，
    /// BeginAnimation 直接可调，不需要 Storyboard。</summary>
    private static void AnimateScale(FrameworkElement root, string transformName, double to,
        double ms, double? from = null, double amplitude = 0)
    {
        if (FindElement<ScaleTransform>(root, transformName) is not { } scale)
            return;

        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
        };
        if (from.HasValue)
            anim.From = from.Value;
        if (amplitude > 0)
            anim.EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static void SetGlyph(System.Windows.Controls.Button star, string glyph)
    {
        if (FindElement<TextBlock>(star, "StarGlyph") is { } g)
            g.Text = glyph;
    }

    /// <summary>先在模板 namescope 按名字找，找不到再沿可视树递归扫（双保险）。
    /// DataTemplate 的 x:Name 都注册在模板根（行 Button）上，理论上 FindName 即可命中。</summary>
    private static T? FindElement<T>(FrameworkElement root, string name) where T : class
    {
        if (root.FindName(name) is T hit)
            return hit;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child &&
                FindElement<T>(child, name) is { } found)
                return found;
        }
        return null;
    }

    private System.Windows.Media.Brush? GetBrush(string key) => TryFindResource(key) as System.Windows.Media.Brush;

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        Log.Info("[FolderJumpTool] FavoriteButton_Click 触发");

        if (sender is not System.Windows.Controls.Button { Tag: string path })
        {
            Log.Info("[FolderJumpTool]   -> 取不到按钮的 Tag/路径，中止");
            return;
        }

        Log.Info($"[FolderJumpTool]   -> 目标路径: {path}");
        ActivatePath(path);
    }

    /// <summary>
    /// 星标点击：把该路径加入/移出收藏夹。
    /// 内嵌在整行按钮里，必须先 e.Handled=true 阻断 Click 冒泡，否则会误触发行跳转。
    /// 普通（候选）模式：重取整表；搜索模式：只重打标记，不退出搜索。
    /// </summary>
    private void FavoriteStar_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button { Tag: string path } || string.IsNullOrWhiteSpace(path))
            return;

        if (FavoritesManager.IsFavorite(path))
            FavoritesManager.Remove(path);
        else
            FavoritesManager.Add("", path); // name 为空时自动取路径末段目录名

        if (SearchBox.Text.Trim().Length == 0)
        {
            RefreshCandidates();
        }
        else if (FavoritesList.ItemsSource is List<FavoriteFolder> current)
        {
            MarkFavorites(current);
            FavoritesList.ItemsSource = null; // 重新赋值强制重绘（星标字形/颜色变化）
            FavoritesList.ItemsSource = current;
        }
    }

    /// <summary>按对话框类型执行跳转：
    /// 文件对话框 → 把路径写进"文件名"框 + 点打开/保存（目录导航进入 / 文件直接选中）。
    /// 文件夹选择器 → 优先"写 0x480 文件夹输入框 + 点选择文件夹"直接完成选择
    /// （SHBrowseForFolder 新样式 / pick-folders 都带这个输入框）；找不到输入框的
    /// 老式纯树形控件才退回地址栏键盘导航进目录。
    /// 异步实现：退路方案需要分段等待（SendInput 时序），await 不阻塞 UI。</summary>
    private async void ActivatePath(string path)
    {
        if (_targetDialog == IntPtr.Zero || !NativeMethods.IsWindow(_targetDialog))
        {
            Log.Info($"[FolderJumpTool]   -> _targetDialog 无效 (hwnd={_targetDialog})，中止");
            return;
        }

        if (_dialogKind == DialogKind.FolderPicker)
        {
            // 有"文件夹:"输入框的变体：写路径 + 点"选择文件夹" = 直接完成选择
            var ok = DialogNavigator.NavigateFolderDialog(_targetDialog, path);
            Log.Info($"[FolderJumpTool]   -> FolderPicker NavigateFolderDialog 返回 {ok}");
            if (ok)
                return;
            // 老式纯树形没有输入框：退回地址栏导航进入目录
            var okNav = await DialogNavigator.NavigateFolderPickerAsync(_targetDialog, path);
            Log.Info($"[FolderJumpTool]   -> NavigateFolderPickerAsync 返回 {okNav}");
            return;
        }

        var okFile = DialogNavigator.NavigateTo(_targetDialog, path);
        Log.Info($"[FolderJumpTool]   -> NavigateTo 返回 {okFile}");
    }
}
