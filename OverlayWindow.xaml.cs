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

    /// <summary>悬浮窗相对文件对话框的水平对齐：Left（靠左，默认）/ Center（居中）/ Right（靠右）。</summary>
    private string _align = "Left";

    /// <summary>输入去抖：停止打字 220ms 后才真正查询，避免每个字符都拉起 es.exe。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce;

    /// <summary>查询序号，用于丢弃过期结果（旧查询晚到不能覆盖新关键词的结果）。</summary>
    private int _searchSeq;

    /// <summary>全局热键 Ctrl+G 的注册 ID；0 = 未注册（不可见时不注册，避免吞掉其他程序的 Ctrl+G）。</summary>
    private int _hotkeyId;

    /// <summary>自定义热键 ID（只要进程内唯一、非 0 即可）。</summary>
    private const int HotkeyId = 0x4777;

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

        SourceInitialized += (_, _) => ApplyNoActivateStyle();

        // Ctrl+G 全局热键只在悬浮窗可见时生效：
        // 可见即注册（首选路径直达），隐藏即注销，不干扰其他程序使用 Ctrl+G。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                RegisterGlobalHotKey();
            else
                UnregisterGlobalHotKey();
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

    /// <summary>
    /// 重新拉取候选路径列表（已打开的资源管理器窗口 + 最近使用 + 固定收藏夹）。
    /// 每次新对话框弹出/对话框重回前台时调用一次，保证列表是"新鲜"的；
    /// 顺便给每条打上"是否已在收藏夹"标记，驱动行尾星标的空心/实心。
    /// </summary>
    public void RefreshCandidates()
    {
        var items = RecentFoldersProvider.GetCandidates();
        MarkFavorites(items);
        FavoritesList.ItemsSource = items;
        UpdateLocationHint();
        UpdateEmptyHint(searchMode: false);
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

    /// <summary>搜索框文字变化：清空恢复收藏/最近；有字则去抖后查询 Everything。</summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            _searchDebounce.Stop();
            RefreshCandidates();
            return;
        }

        LocationHint.Visibility = Visibility.Collapsed; // 搜索模式：右上角让位给搜索框
        _searchDebounce.Stop();
        _searchDebounce.Start();
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
        _ = Task.Run(() => EverythingSearch.Search(es, keyword, 20))
            .ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_searchSeq != seq)
                    return; // 已有更新的查询，这次的结果过期，丢弃

                var items = t.IsCompletedSuccessfully
                    ? t.Result
                    : new List<FavoriteFolder>();
                MarkFavorites(items);
                FavoritesList.ItemsSource = items;
                UpdateEmptyHint(searchMode: true);
            })));
    }

    /// <summary>回车：无输入时是"收藏列表"模式不拦截；搜索模式下跳第一条结果。
    /// Esc：清空关键词，立即恢复收藏/最近列表（配合 TextChanged 事件）。</summary>
    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter)
            return;
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
            return;

        JumpToFirst(); // 与 Ctrl+G 热键共用：直接跳当前列表第一条
        e.Handled = true;
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

    /// <summary>悬浮窗是 WS_EX_NOACTIVATE 窗口，点击不会自动把键盘焦点给到输入框；
    /// 这里手动拿键盘焦点（不改变窗口激活状态，避免对话框失焦）。</summary>
    private void SearchBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SearchBox.IsKeyboardFocusWithin)
            Keyboard.Focus(SearchBox);
    }

    /// <summary>
    /// 关键一步：给悬浮窗加上 WS_EX_NOACTIVATE，
    /// 这样点击悬浮窗上的按钮时不会把焦点从文件对话框上抢走，
    /// 体验上不会出现"点一下悬浮窗，对话框好像失焦跳了一下"的问题。
    /// </summary>
    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        // 关键：显式接管 WM_MOUSEACTIVATE 并返回 MA_NOACTIVATE。
        // WS_EX_NOACTIVATE 本身不总是够——WPF 不处理这个消息时，
        // 第一次点击经常会被系统当成"先激活窗口"处理掉，导致点击好像没反应，
        // 或者需要点好几次才会真正触发 Click。显式接管之后点击可以立刻穿透生效。
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProcHook);
    }

    /// <summary>窗口消息钩子：拦截 WM_MOUSEACTIVATE 让点击不抢对话框焦点；
    /// 处理 WM_HOTKEY（Ctrl+G）触发首选路径直达。</summary>
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return (IntPtr)NativeMethods.MA_NOACTIVATE;
        }

        if (msg == NativeMethods.WM_HOTKEY && _hotkeyId != 0 && wParam == (IntPtr)_hotkeyId)
        {
            JumpToFirst();
            handled = true;
        }

        return IntPtr.Zero;
    }

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
        // 基础可视上限 340px，再按对话框上/下可用的屏幕空间收紧——
        // 取两侧可用空间较大的一侧，减去标题行+卡片边距等固定开销（约 44px），
        // 保证悬浮窗总高不超过它最终落脚那侧的可用区域（下方不够会翻到上方）。
        // 留 96px 下限，避免对话框几乎占满屏幕时悬浮窗被压到只剩一条。
        double availBelow = screenTop + screenHeight - (dialogRect.Bottom + gap);
        double availAbove = dialogRect.Top - screenTop - gap;
        double listMax = Math.Max(96, Math.Min(340, Math.Max(availBelow, availAbove) - 44));
        if (Math.Abs(ListScroller.MaxHeight - listMax) > 0.5)
            ListScroller.MaxHeight = listMax;

        // 第一次显示前，SizeToContent 窗口的 Width/Height 是 NaN（还没走过布局），
        // 用 NaN 算 Left 会导致窗口飞到屏幕外或者干脆不显示。
        // 所以先在屏幕外 Show 一次，强制走一遍布局拿到真实尺寸，再重新定位。
        if (!IsVisible)
        {
            Left = -5000;
            Top = -5000;
            Show();
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

        Log.Info(
            $"[FolderJumpTool] FollowDialog: align={_align} dialogRect=({dialogRect.Left},{dialogRect.Top},{dialogRect.Right},{dialogRect.Bottom}) -> overlay Left={Left}, Top={Top}, ActualWidth={ActualWidth}, ActualHeight={ActualHeight}");
    }

    public void HideOverlay() => Hide();

    // ---------- 行 hover：条目放大 + 星标出现（事件驱动；DataTemplate.Triggers 的动画在此环境不可靠） ----------

    /// <summary>行加载完成：按收藏状态初始化星标。已收藏 = 实心蓝星常显可点；未收藏 = 隐藏待 hover。</summary>
    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button row ||
            row.DataContext is not FavoriteFolder item)
            return;

        var star = FindElement<System.Windows.Controls.Button>(row, "StarButton");
        if (star == null)
            return;

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

    /// <summary>鼠标进入行：整行内容（图标+名称+路径）经 RenderTransform 弹性放大——
    /// GPU 合成层变换不触发重新布局，动画丝滑无顿挫（此前 FontSize 字号动画每帧重排文本，
    /// 且字号增量被光栅器按像素取整，看起来一卡一卡）。ClearType 在非整数缩放下会发糊，
    /// 故放大期间把行内文字临时切到灰度抗锯齿（灰度在任意缩放下都是均匀柔边）。
    /// 未收藏条目星标淡入弹出。</summary>
    private void Row_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button row)
            return;

        // 行内容放大（BackEase 轻微过冲再回位 = "啵"一下，丝滑）
        AnimateScale(row, "RowScale", 1.05, 150, amplitude: 0.25);

        // 放大期间切灰度抗锯齿，避免 ClearType 子像素渲染在非整数缩放下发糊
        SetTextAa(row, System.Windows.Media.TextRenderingMode.Grayscale);

        // 图标 hover 变主题蓝
        if (FindElement<System.Windows.Shapes.Path>(row, "FolderIcon") is { } icon)
            icon.Fill = GetBrush("FolderIconHoverBrush");

        // 未收藏条目：星标淡入 + 0.85→1 弹入
        if (row.DataContext is FavoriteFolder { IsFavorite: false } &&
            FindElement<System.Windows.Controls.Button>(row, "StarButton") is { } star)
        {
            star.IsHitTestVisible = true;
            star.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));
            AnimateScale(row, "StarScale", 1, 160, from: 0.85, amplitude: 0.35);
        }
    }

    /// <summary>鼠标离开行：放大还原、抗锯齿恢复默认、图标回灰；未收藏条目星标淡出收回。</summary>
    private void Row_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button row)
            return;

        AnimateScale(row, "RowScale", 1, 120);
        SetTextAa(row, System.Windows.Media.TextRenderingMode.Auto);

        if (FindElement<System.Windows.Shapes.Path>(row, "FolderIcon") is { } icon)
            icon.Fill = GetBrush("FolderIconBrush");

        if (row.DataContext is FavoriteFolder { IsFavorite: false } &&
            FindElement<System.Windows.Controls.Button>(row, "StarButton") is { } star)
        {
            star.IsHitTestVisible = false;
            star.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(70)));
            AnimateScale(row, "StarScale", 0.85, 100);
        }
    }

    /// <summary>整行名称/路径文字临时切换抗锯齿模式（Grayscale 放大期 / Auto 恢复系统默认）。</summary>
    private static void SetTextAa(FrameworkElement row, System.Windows.Media.TextRenderingMode mode)
    {
        foreach (var name in new[] { "NameText", "PathText" })
        {
            if (FindElement<TextBlock>(row, name) is { } tb)
                TextOptions.SetTextRenderingMode(tb, mode);
        }
    }

    /// <summary>对模板内命名 ScaleTransform（RowScale/StarScale，都是 Freezable）做动画，
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

    /// <summary>把路径写进文件对话框并触发跳转。目录 → 对话框导航进入；
    /// 文件完整路径 → 打开/保存对话框直接选中该文件。</summary>
    private void ActivatePath(string path)
    {
        if (_targetDialog == IntPtr.Zero || !NativeMethods.IsWindow(_targetDialog))
        {
            Log.Info($"[FolderJumpTool]   -> _targetDialog 无效 (hwnd={_targetDialog})，中止");
            return;
        }

        var ok = DialogNavigator.NavigateTo(_targetDialog, path);
        Log.Info($"[FolderJumpTool]   -> NavigateTo 返回 {ok}");
    }
}
