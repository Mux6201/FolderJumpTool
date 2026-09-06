using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
// csproj 启用了 UseWindowsForms（托盘用），System.Windows.Forms 进了全局命名空间，
// 与 WPF 同名类型（TextBox/Button/Point/MouseEventArgs 等）冲突，这里显式别名到 WPF。
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace FolderJumpTool;

/// <summary>
/// 收藏夹管理窗口（托盘菜单"收藏夹管理…"打开）。
/// 功能：添加 / 删除 / 行内重命名（铅笔按钮或双击名称，Enter 提交、Esc 取消）/
/// 拖拽排序（按住行拖动，蓝线指示插入位置，可拖出列表边缘触发自动滚动）。
/// 列表改动直接操作 FavoritesManager 的 favorites.json；
/// 任何改动后触发 FavoritesChanged，让 App 刷新悬浮窗候选（星标状态/名称/顺序同步）。
/// 全部文案/颜色走 DynamicResource，主题与语言热切换即时生效。
/// 事件驱动为主（DataTemplate.Triggers 的样式/动画切换在本环境不可靠，历史教训）。
/// </summary>
public partial class FavoritesWindow : Window
{
    /// <summary>收藏列表变化（添加/删除/重命名/排序）后触发；App 订阅后刷新悬浮窗候选。</summary>
    public event Action? FavoritesChanged;

    private List<FavoriteFolder> _items = new();
    private readonly DispatcherTimer _autoScroll;

    // 拖拽排序状态
    private Grid? _dragRow;
    private FavoriteFolder? _dragItem;
    private bool _dragArmed;
    private bool _dragActive;
    private Point _downPos;
    private int _dropIndex = -1;

    // 行内重命名状态（防 LostFocus 与 Enter/Esc 处理互相重入）
    private bool _editBusy;

    public FavoritesWindow()
    {
        InitializeComponent();
        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _autoScroll.Tick += AutoScroll_Tick;
        Reload();
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoScroll.Stop();
        base.OnClosed(e);
    }

    private void Reload()
    {
        _items = FavoritesManager.Load();
        ApplyList();
    }

    /// <summary>把当前工作列表刷到界面（重置 ItemsSource 触发重绘，尽量保持滚动位置）。</summary>
    private void ApplyList()
    {
        double offset = FavScroller.VerticalOffset;
        FavList.ItemsSource = null;
        FavList.ItemsSource = _items;
        if (offset > 0)
            FavScroller.ScrollToVerticalOffset(offset);

        bool empty = _items.Count == 0;
        FavScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = "(" + _items.Count + ")";
    }

    // ---------- 添加 / 删除 ----------

    private void AddPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Multiselect = false,
            Title = S("Fav.PickFolder"),
        };

        if (dlg.ShowDialog(this) != true)
            return;

        string folder = dlg.FolderName;
        if (string.IsNullOrWhiteSpace(folder))
            return;

        bool added = FavoritesManager.Add("", folder); // name 自动取目录末段
        if (!added)
            return;

        Reload();
        FavoritesChanged?.Invoke();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;

        if (!FavoritesManager.Remove(path))
            return;

        Reload();
        FavoritesChanged?.Invoke();
    }

    // ---------- 行内重命名 ----------

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var row = FindAncestor<Grid>(sender as DependencyObject);
        StartEdit(row);
    }

    /// <summary>双击名称直接进入编辑（名称 TextBlock 无选中概念，双击即重命名）。</summary>
    private void NameText_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not TextBlock nameText)
            return;
        e.Handled = true; // 阻断冒泡，避免被当成行拖拽起点
        StartEdit(FindAncestor<Grid>(nameText));
    }

    private void StartEdit(Grid? row)
    {
        if (row == null || _editBusy)
            return;
        if (FindDescendant<TextBox>(row, "RenameBox") is not { } box ||
            FindDescendant<TextBlock>(row, "NameText") is not { } name)
            return;

        box.Text = name.Text;
        name.Visibility = Visibility.Collapsed;
        box.Visibility = Visibility.Visible;
        box.Focus();
        box.SelectAll();
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitRename(box);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelRename(box);
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 框已被隐藏（Enter/Esc 已处理过）或正在处理中 → 忽略这次失焦，防重入
        if (sender is not TextBox { Visibility: Visibility.Visible } box || _editBusy)
            return;
        CommitRename(box);
    }

    private void CommitRename(TextBox? box)
    {
        if (box == null || _editBusy)
            return;

        _editBusy = true;
        try
        {
            string newName = box.Text.Trim();
            bool save = false;
            string? path = null;
            if (box.DataContext is FavoriteFolder item)
            {
                save = newName.Length > 0 && newName != item.Name;
                path = item.Path;
            }
            EndEdit(box);
            if (save && path != null && FavoritesManager.Rename(path, newName))
            {
                Reload();
                FavoritesChanged?.Invoke();
            }
        }
        finally
        {
            _editBusy = false;
        }
    }

    private void CancelRename(TextBox? box)
    {
        if (box == null || _editBusy)
            return;

        _editBusy = true;
        try
        {
            EndEdit(box);
        }
        finally
        {
            _editBusy = false;
        }
    }

    /// <summary>退出编辑态：隐藏输入框、恢复名称显示（不改数据）。</summary>
    private static void EndEdit(TextBox box)
    {
        box.Visibility = Visibility.Collapsed;
        var row = FindAncestor<Grid>(box);
        if (row != null && FindDescendant<TextBlock>(row, "NameText") is { } name)
            name.Visibility = Visibility.Visible;
    }

    // ---------- 拖拽排序 ----------

    private void Row_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid row || _editBusy)
            return;
        if (e.ChangedButton != MouseButton.Left || e.ClickCount > 1)
            return;
        // 起点落在按钮/输入框上时不进入拖拽（那是点按或编辑）
        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<Button>(src) != null || FindAncestor<TextBox>(src) != null)
            return;
        if (row.DataContext is not FavoriteFolder item)
            return;

        _dragRow = row;
        _dragItem = item;
        _downPos = e.GetPosition(row);
        _dragArmed = true;
        _dragActive = false;
        row.CaptureMouse();
    }

    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || sender is not Grid row || _dragItem == null)
            return;

        var pos = e.GetPosition(row);
        if (!_dragActive)
        {
            if (Math.Abs(pos.X - _downPos.X) < 4 && Math.Abs(pos.Y - _downPos.Y) < 4)
                return; // 未超过拖拽阈值，仍按点击处理
            _dragActive = true;
            _dragRow = row;
            row.Opacity = 0.55;
            PlaceDropLine(e);
        }

        UpdateDrag(e);
    }

    private void Row_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragArmed)
            return;

        _dragArmed = false;
        _autoScroll.Stop();
        if (sender is Grid row)
        {
            if (row.IsMouseCaptured)
                row.ReleaseMouseCapture();
            row.Opacity = 1;
        }

        if (_dragActive)
        {
            _dragActive = false;
            DropLine.Visibility = Visibility.Collapsed;
            CommitReorder();
        }

        _dragItem = null;
        _dragRow = null;
        _dropIndex = -1;
    }

    /// <summary>拖拽中：实时放插入指示线 + 边缘自动滚动启停。</summary>
    private void UpdateDrag(MouseEventArgs e)
    {
        if (_dragItem == null)
            return;
        PlaceDropLine(e);

        double vy = e.GetPosition(FavScroller).Y;
        double vh = FavScroller.ViewportHeight;
        if (vy < 18 || vy > vh - 18)
        {
            if (!_autoScroll.IsEnabled)
                _autoScroll.Start();
        }
        else
        {
            _autoScroll.Stop();
        }
    }

    private void AutoScroll_Tick(object? sender, EventArgs e)
    {
        if (!_dragActive || _dragItem == null)
        {
            _autoScroll.Stop();
            return;
        }
        double vy = Mouse.GetPosition(FavScroller).Y;
        double vh = FavScroller.ViewportHeight;
        if (vy < 18)
            FavScroller.ScrollToVerticalOffset(FavScroller.VerticalOffset - 12);
        else if (vy > vh - 18)
            FavScroller.ScrollToVerticalOffset(FavScroller.VerticalOffset + 12);
        else
            _autoScroll.Stop();
    }

    /// <summary>按鼠标位置（ScrollHost 坐标）算出插入下标并移动蓝线。</summary>
    private void PlaceDropLine(MouseEventArgs e)
    {
        var hostPoint = e.GetPosition(ScrollHost);
        int n = _items.Count;
        int idx = n;
        double lineY = 0;
        double contentBottom = 0;
        var gen = FavList.ItemContainerGenerator;

        for (int i = 0; i < n; i++)
        {
            if (gen.ContainerFromIndex(i) is not FrameworkElement c)
                break;
            double top = c.TransformToAncestor(ScrollHost).Transform(new Point(0, 0)).Y;
            contentBottom = top + c.ActualHeight;
            if (hostPoint.Y < top + c.ActualHeight / 2)
            {
                idx = i;
                lineY = top;
                break;
            }
        }

        if (idx == n)
            lineY = contentBottom;

        _dropIndex = idx;
        DropLine.Margin = new Thickness(4, Math.Max(0, lineY - 1), 4, 0);
        DropLine.Visibility = Visibility.Visible;
    }

    private void CommitReorder()
    {
        if (_dragItem == null || _dropIndex < 0)
            return;
        int from = _items.IndexOf(_dragItem);
        if (from < 0)
            return;

        if (!FavoritesManager.Move(from, _dropIndex))
            return; // 没实际变化（原位/紧邻）不动

        Reload();
        FavoritesChanged?.Invoke();
    }

    // ---------- 工具 ----------

    /// <summary>从当前资源取本地化词；取不到时原样返回 key 便于发现漏词。</summary>
    private string S(string key) => TryFindResource(key) as string ?? key;

    /// <summary>沿可视树向上找第一个指定类型元素。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
            node = VisualTreeHelper.GetParent(node);
        return node as T;
    }

    /// <summary>在指定根下按名字找子元素（行模板里 x:Name 不在窗口命名空间，需走可视树）。</summary>
    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T t && t.Name == name)
            return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var hit = FindDescendant<T>(VisualTreeHelper.GetChild(root, i), name);
            if (hit != null)
                return hit;
        }
        return null;
    }
}
