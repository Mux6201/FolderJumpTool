using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace FolderJumpTool;

/// <summary>
/// Everything 的 es.exe 封装。
/// es.exe 是 Everything 官方命令行工具（需单独从 voidtools 下载），
/// 依赖 Everything 进程在后台运行提供索引——es 只是个查询客户端。
/// 输出是每行一个完整路径（文件与文件夹混合）。
/// </summary>
internal static class EverythingSearch
{
    /// <summary>
    /// 找出可用的 es.exe。优先级：
    /// 1. settings.json 里手动配置的路径；
    /// 2. PATH 里的 es.exe（where es.exe）；
    /// 3. Everything 安装目录（App Paths 注册表）；
    /// 4. 几个常见安装位置兜底。
    /// 全部找不到返回 null（悬浮窗不显示搜索框）。
    /// </summary>
    public static string? FindEsExe(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // 2. PATH
        try
        {
            using var where = Process.Start(new ProcessStartInfo("where.exe", "es.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (where != null)
            {
                var line = where.StandardOutput.ReadLine();
                where.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                    return line.Trim();
            }
        }
        catch
        {
            // 忽略，继续下一候选
        }

        // 3. App Paths 注册表：Everything.exe 的安装目录，es.exe 通常与之同目录
        try
        {
            using var appPaths = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe");
            var everythingExe = appPaths?.GetValue("") as string;
            if (string.IsNullOrEmpty(everythingExe))
            {
                using var lm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe");
                everythingExe = lm?.GetValue("") as string;
            }
            if (!string.IsNullOrEmpty(everythingExe))
            {
                var candidate = Path.Combine(Path.GetDirectoryName(everythingExe) ?? "", "es.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // 忽略
        }

        // 4. 常见安装位置
        string[] probeDirs =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everything"),
        };
        foreach (var dir in probeDirs)
        {
            var candidate = Path.Combine(dir, "es.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// 用 es.exe 搜索，返回最多 maxResults 条路径（文件/文件夹混合）。
    /// 查询在后台线程调用，避免卡 UI。
    /// </summary>
    public static List<FavoriteFolder> Search(string esPath, string query, int maxResults = 20)
    {
        var result = new List<FavoriteFolder>();
        try
        {
            var psi = new ProcessStartInfo(esPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(maxResults.ToString());
            if (!string.IsNullOrWhiteSpace(query))
                psi.ArgumentList.Add(query);

            using var p = Process.Start(psi);
            if (p == null)
                return result;

            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); } catch { }
                return result;
            }

            var text = DecodeText(ms.ToArray());
            foreach (var raw in text.Split('\n'))
            {
                if (result.Count >= maxResults)
                    break;

                var path = raw.Trim().Trim('"', '\r');
                if (path.Length == 0)
                    continue;

                var folderPath = path.TrimEnd('\\');
                var name = Path.GetFileName(folderPath);
                result.Add(new FavoriteFolder
                {
                    Name = string.IsNullOrEmpty(name) ? path : name,
                    Path = folderPath,
                });
            }

            if (result.Count == 0 && !string.IsNullOrWhiteSpace(err))
                Log.Info($"[EverythingSearch] es.exe 报错: {err.Trim()}");
        }
        catch (Exception ex)
        {
            Log.Info($"[EverythingSearch] 查询失败: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// es.exe 的输出编码不固定（受控制台代码页影响），这里做兼容：
    /// 带 UTF-16 BOM → 按 UTF-16 解；能按 UTF-8 严格解出 → UTF-8；
    /// 否则退回系统 ANSI 代码页（简体中文系统即 GBK）。
    /// </summary>
    private static string DecodeText(byte[] raw)
    {
        if (raw.Length == 0)
            return "";

        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);

        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(raw);
        }
    }
}
