using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace FolderJumpTool;

/// <summary>
/// 收藏夹管理窗口（托盘菜单"收藏夹管理…"打开）。
/// 列表增删直接操作 FavoritesManager 的 favorites.json；
/// 任何改动后触发 FavoritesChanged，让 App 刷新悬浮窗候选（星标状态同步）。
/// 全部文案/颜色走 DynamicResource，主题与语言热切换即时生效。
/// </summary>
public partial class FavoritesWindow : Window
{
    /// <summary>收藏列表变化（添加/删除）后触发；App 订阅后刷新悬浮窗候选。</summary>
    public event Action? FavoritesChanged;

    public FavoritesWindow()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        var list = FavoritesManager.Load();
        FavList.ItemsSource = list;

        bool empty = list.Count == 0;
        FavScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = "(" + list.Count + ")";
    }

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
        if (sender is not System.Windows.Controls.Button { Tag: string path })
            return;

        if (!FavoritesManager.Remove(path))
            return;

        Reload();
        FavoritesChanged?.Invoke();
    }

    /// <summary>从当前资源取本地化词；取不到时原样返回 key 便于发现漏词。</summary>
    private string S(string key) => TryFindResource(key) as string ?? key;
}
