using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderJumpTool;

internal sealed class FavoriteFolder
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>仅用于悬浮窗星标显示（该路径当前是否已在收藏夹），不写入 json。</summary>
    [JsonIgnore]
    public bool IsFavorite { get; set; }
}

/// <summary>
/// 收藏夹的读写。存到 %AppData%\FolderJumpTool\favorites.json
/// Load() 只返回用户持久化的收藏；文件不存在/为空/损坏都视为"还没有收藏"（返回空列表）。
/// LoadWithDefaults() 是候选列表专用的：没有任何收藏时用桌面/下载/文档三条兜底，
/// 但这三条默认项只是展示用，不会写进文件——用户首次点星标收藏时只存自己加的。
/// </summary>
internal static class FavoritesManager
{
    private static readonly string StoreDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderJumpTool");

    private static readonly string StoreFile = System.IO.Path.Combine(StoreDir, "favorites.json");

    /// <summary>用户持久化的收藏列表；文件不存在/空/损坏时返回空列表（不是默认项）。</summary>
    public static List<FavoriteFolder> Load()
    {
        try
        {
            if (!File.Exists(StoreFile))
                return new List<FavoriteFolder>();

            var json = File.ReadAllText(StoreFile);
            var list = JsonSerializer.Deserialize<List<FavoriteFolder>>(json);
            return list ?? new List<FavoriteFolder>();
        }
        catch
        {
            return new List<FavoriteFolder>();
        }
    }

    /// <summary>候选列表用的收藏来源：有持久化收藏就用，否则返回桌面/下载/文档兜底（不落盘）。</summary>
    public static List<FavoriteFolder> LoadWithDefaults() =>
        Load() is { Count: > 0 } saved ? saved : DefaultFavorites();

    public static void Save(List<FavoriteFolder> favorites)
    {
        Directory.CreateDirectory(StoreDir);
        var json = JsonSerializer.Serialize(favorites, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StoreFile, json);
    }

    public static bool IsFavorite(string path)
    {
        var n = Normalize(path);
        return Load().Any(f => Normalize(f.Path) == n);
    }

    /// <summary>添加收藏。返回是否真的加了（已存在则不重复添加）。name 为空时自动取路径末段目录名。</summary>
    public static bool Add(string name, string path)
    {
        var list = Load();
        var n = Normalize(path);
        if (list.Any(f => Normalize(f.Path) == n))
            return false;

        if (string.IsNullOrWhiteSpace(name))
        {
            var dirName = Path.GetFileName(n.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            name = string.IsNullOrEmpty(dirName) ? n : dirName;
        }

        list.Add(new FavoriteFolder { Name = name, Path = n });
        Save(list);
        return true;
    }

    /// <summary>按路径移除收藏。返回是否真的删了。</summary>
    public static bool Remove(string path)
    {
        var list = Load();
        var n = Normalize(path);
        int removed = list.RemoveAll(f => Normalize(f.Path) == n);
        if (removed == 0)
            return false;
        Save(list);
        return true;
    }

    private static string Normalize(string path) => (path ?? "").Trim().TrimEnd('\\');

    private static List<FavoriteFolder> DefaultFavorites()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var downloads = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return new List<FavoriteFolder>
        {
            new() { Name = "桌面", Path = desktop },
            new() { Name = "下载", Path = downloads },
            new() { Name = "文档", Path = documents },
        };
    }
}
