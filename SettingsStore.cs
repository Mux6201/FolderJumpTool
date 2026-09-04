using System.IO;
using System.Text.Json;

namespace FolderJumpTool;

/// <summary>
/// 应用设置的读写（%AppData%\FolderJumpTool\settings.json）。
/// 当前键：themeMode（System/Light/Dark）、esPath（Everything 的 es.exe 完整路径）。
/// 所有键合并读写：Set 只更新一个键，其余键原样保留，互不覆盖。
/// </summary>
internal static class SettingsStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderJumpTool");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static string Get(string key, string defaultValue = "")
    {
        try
        {
            if (!File.Exists(FilePath))
                return defaultValue;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? defaultValue;
        }
        catch
        {
            // 读坏/读不到都返回默认值
        }
        return defaultValue;
    }

    public static void Set(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(Dir);

            // 先把现有键全部读出来，只改目标键，避免互相抹掉
            var dict = new Dictionary<string, string>();
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        dict[prop.Name] = prop.Value.GetString() ?? "";
                }
            }

            dict[key] = value;
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 持久化失败不影响本次运行
        }
    }
}
