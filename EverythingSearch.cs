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
    // .NET Core 默认只带 UTF-8/UTF-16 等少数编码；es.exe 输出含中文路径时按系统
    // ANSI 代码页（简体中文 = GBK/936）编码，不注册 CodePages 提供程序的话
    // Encoding.GetEncoding(936) 会直接抛异常 → 整个查询静默返回空（表现为"搜不到"）。
    // 注册一次全进程生效。
    static EverythingSearch()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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
    ///
    /// 两条路径：
    /// 1) 原生 Everything 语法（词里含 : \ * ? " 空格 等）→ 原样透传给 es，
    ///    让用户自己写 folder:xxx / D:\xxx / name: / ext: / 引号精确匹配 等高级查询；
    ///    文件夹选择器场景也不干预（用户明确写 folder: 就是明确意图）。
    /// 2) 普通纯词（如 Code）→ 智能处理：
    ///    a. 文件夹对话框自动加 folder: 前缀（只出目录）；
    ///    b. 前缀通配 Code* —— Everything 默认是"名字任意位置包含"，打 Code
    ///       会把海量 .codebuddy、名字中间带 code 的一起算上（实测 11951 条），
    ///       D:\Code 被淹没在 80 条之外；前缀匹配噪音消失、目标进前 20；
    ///    c. 前缀查不到退回"任意位置包含"一次（照顾 olderJump→FolderJumpTool）；
    ///    d. 客户端重排：目录优先、名字精确命中优先、路径越浅越靠前，
    ///       把 D:\Code 这类"顶层项目根"顶到第 1 条。
    /// </summary>
    public static List<FavoriteFolder> Search(string esPath, string query, int maxResults = 40, bool foldersOnly = false)
    {
        var keyword = query.Trim();
        if (keyword.Length == 0)
            return new List<FavoriteFolder>();

        // 含 Everything 运算符/路径分隔符/空格 = 用户写的是原生语法，不加工、不补前缀
        var hasNativeSyntax = HasEverythingSyntax(keyword);

        string primary;
        if (!hasNativeSyntax && IsPlainWord(keyword))
        {
            // 智能路径：纯词
            primary = foldersOnly ? $"folder:{keyword}*" : $"{keyword}*";
        }
        else if (!hasNativeSyntax && foldersOnly)
        {
            // 带空格/中文等非纯词 + 文件夹对话框：仍补 folder:（保持"只出文件夹"），不加通配
            primary = $"folder:{keyword}";
        }
        else
        {
            primary = keyword; // 原生语法完全透传（不再拼 folder: / *）
        }

        var rawQuery = foldersOnly && !keyword.StartsWith("folder:", StringComparison.OrdinalIgnoreCase)
            ? $"folder:{keyword}"
            : keyword;

        // 取回量要远大于显示上限：es 按路径字母序返回，若只取几十条，
        // 用户目录下"名字含关键词前缀"的项（如 .codebuddy 系列）会把窗口占满，
        // 顶层目标根本排不进样本，后面的客户端重排"无米下锅"。
        // 取 600 条再重排截断（es 查 600 条毫秒级，无感）。
        int fetch = Math.Max(600, maxResults);
        var result = Run(esPath, primary, fetch);
        if (result.Count == 0 && primary != rawQuery)
            result = Run(esPath, rawQuery, fetch);

        // 只有智能路径（纯词）才客户端重排；原生语法查询保持 Everything 原样结果。
        if (!hasNativeSyntax)
            RankResults(result, keyword);

        // 重排完成后才截断到显示上限，避免几百条灌进列表
        if (result.Count > maxResults)
            result = result.GetRange(0, maxResults);

        return result;
    }

    /// <summary>是否含 Everything 原生语法特征（冒号前缀、路径分隔符、通配符、
    /// 引号、空格分词、大小写/函数前缀等）——是则按用户原样查询，不做智能加工。</summary>
    private static bool HasEverythingSyntax(string s)
    {
        foreach (var c in s)
        {
            if (c is ':' or '\\' or '/' or '*' or '?' or '"' or '!' or ' ' or '\t' or '<' or '>')
                return true;
        }
        return false;
    }

    /// <summary>是否"纯词"（无空格/无 Everything 运算符），可以安全追加 * 做前缀匹配。</summary>
    private static bool IsPlainWord(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.'))
                return false;
        }
        return true;
    }

    /// <summary>真正跑一次 es.exe，解析输出为条目。</summary>
    private static List<FavoriteFolder> Run(string esPath, string fullQuery, int maxResults)
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
            if (!string.IsNullOrWhiteSpace(fullQuery))
                psi.ArgumentList.Add(fullQuery);

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

                // 标注目录/文件，驱动行图标（文件夹图标 vs 文件类型图标）与星标显隐。
                // es 输出不带类型信息：带尾斜杠必为目录；否则按磁盘真实类型判定
                // （Everything 索引的都是真实存在路径，Exists 判断基本都能命中）。
                bool isDir;
                if (path.EndsWith('\\'))
                    isDir = true;
                else if (Directory.Exists(folderPath))
                    isDir = true;
                else
                    isDir = false;

                result.Add(new FavoriteFolder
                {
                    Name = string.IsNullOrEmpty(name) ? path : name,
                    Path = folderPath,
                    IsDirectory = isDir,
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
    /// 客户端排序：目录 > 文件；同级里名字精确命中关键词的排前面；
    /// 目录之间路径越浅越靠前（D:\Code 深 1 层，会排在 VS 模板里
    /// C:\Program Files\...\Code 那种深 7 层的同名目录前面）。
    /// </summary>
    private static void RankResults(List<FavoriteFolder> items, string keyword)
    {
        if (items.Count < 2)
            return;

        items.Sort((a, b) =>
        {
            int d = RankScore(b, keyword).CompareTo(RankScore(a, keyword));
            if (d != 0)
                return d;
            int n = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return n != 0 ? n : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static int RankScore(FavoriteFolder item, string keyword)
    {
        int score = 0;
        if (item.IsDirectory)
        {
            score += 1000; // 目录优先：跳转工具的主诉求是进目录
            score -= PathDepth(item.Path); // 路径浅的目录（根级项目）更靠前
        }
        if (string.Equals(item.Name, keyword, StringComparison.OrdinalIgnoreCase))
            score += 500; // 名字与关键词完全一致
        return score;
    }

    private static int PathDepth(string path)
    {
        int depth = 0;
        foreach (var c in path)
        {
            if (c is '\\' or '/')
                depth++;
        }
        return depth;
    }

    /// <summary>
    /// es.exe 的输出编码不固定（受控制台代码页影响），这里做兼容：
    /// 带 UTF-16 BOM → 按 UTF-16 解；带 UTF-8 BOM → 按 UTF-8 解（剥 BOM）；
    /// 能按 UTF-8 严格解出 → UTF-8；否则退回系统 ANSI 代码页（简体中文系统即 GBK，
    /// 静态构造里已注册 CodePagesEncodingProvider，此处不再抛异常）。
    /// 最后一道兜底用 Encoding.Default 按替换字符解码，保证任何字节都不会让查询失败。
    /// </summary>
    private static string DecodeText(byte[] raw)
    {
        if (raw.Length == 0)
            return "";

        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);

        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);

        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            // 非 UTF-8（典型：中文路径按系统 ANSI/GBK 输出）
        }

        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage)
                .GetString(raw);
        }
        catch
        {
            return Encoding.Default.GetString(raw); // 兜底：替换字符，绝不抛异常中断查询
        }
    }
}
