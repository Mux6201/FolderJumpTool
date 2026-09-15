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
    public static SearchOutcome Search(string esPath, string query, int maxResults = 40, bool foldersOnly = false)
    {
        var keyword = query.Trim();
        if (keyword.Length == 0)
            return new SearchOutcome(new List<FavoriteFolder>(), TimedOut: false);

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
        var (result, timedOut) = Run(esPath, primary, fetch);
        // 已经超时就不再补第二次查询——那只会让"搜索中"再多转三秒。
        if (result.Count == 0 && !timedOut && primary != rawQuery)
            (result, timedOut) = Run(esPath, rawQuery, fetch);

        // 只有智能路径（纯词）才客户端重排；原生语法查询保持 Everything 原样结果。
        if (!hasNativeSyntax)
            RankResults(result, keyword);

        // 重排完成后才截断到显示上限，避免几百条灌进列表
        if (result.Count > maxResults)
            result = result.GetRange(0, maxResults);

        return new SearchOutcome(result, timedOut);
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

    /// <summary>es.exe 单次查询的超时上限（毫秒）。超时即终止进程并返回空结果——
    /// 宁可这一次搜不到，也不能让调用方永久挂在等 es 返回上。</summary>
    private const int QueryTimeoutMs = 3000;

    /// <summary>慢查询留痕阈值（毫秒）：只在这条线以上才写日志，正常毫秒级查询不刷屏。</summary>
    private const int SlowQueryLogMs = 400;

    /// <summary>
    /// 预热：跑一次最小查询，把 es.exe 进程冷启动 + 与 Everything 建立 IPC 握手的开销
    /// 提前到程序启动阶段（后台线程，用户无感），消除"第一次搜索要卡一下"。
    /// 失败静默——真正搜索时还会再试，预热只是把冷启动成本挪走。
    /// </summary>
    public static void WarmUp(string esPath)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            Run(esPath, "a", 1, logSlow: false); // 查询内容无所谓，目的是让 es.exe 跑起来并连上 Everything
            sw.Stop();

            // 冷启动成本实测留痕：这条数字就是"第一次搜索会卡多久"的答案，超过阈值才写。
            if (sw.ElapsedMilliseconds >= 300)
                Log.Info($"[EverythingSearch] 预热 es.exe 耗时 {sw.ElapsedMilliseconds}ms"
                    + "（该冷启动成本已从\"第一次搜索\"挪到了启动阶段）");
        }
        catch
        {
            // 预热失败不影响功能
        }
    }

    /// <summary>
    /// 真正跑一次 es.exe，解析输出为条目。第二项标记"是否因超时提前结束"——
    /// UI 靠它把"真的没匹配到"和"没搜完"区分开（前者提示无结果，后者提示超时）。
    /// </summary>
    private static (List<FavoriteFolder> Items, bool TimedOut) Run(
        string esPath, string fullQuery, int maxResults, bool logSlow = true)
    {
        var result = new List<FavoriteFolder>();
        var sw = Stopwatch.StartNew();
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
                return (result, false);

            // stdout/stderr 必须【并发】读。原实现是「CopyTo(stdout) → ReadToEnd(stderr)
            // → WaitForExit(3000)」，而 CopyTo 会一直阻塞到 es 关闭输出为止：一旦 es 因
            // Everything 索引重建 / IPC 首次握手未就绪而挂起不出数据，CopyTo 永不返回，
            // 后面的 WaitForExit 根本执行不到 —— 超时形同虚设，后台线程永久卡住、结果
            // 永远不回来（用户看到的就是"第一次搜索卡住，后面又正常"）。
            var ms = new MemoryStream();
            var err = "";
            var readOut = Task.Run(() => { try { p.StandardOutput.BaseStream.CopyTo(ms); } catch { } });
            var readErr = Task.Run(() => { try { err = p.StandardError.ReadToEnd(); } catch { } });

            if (!p.WaitForExit(QueryTimeoutMs))
            {
                try { p.Kill(); } catch { }
                Log.Info($"[EverythingSearch] es.exe 超时 {QueryTimeoutMs}ms 未返回"
                    + $"（Everything 未运行 / 索引重建中？查询='{fullQuery}'），本次放弃并标记超时");
                return (result, true);
            }

            readOut.Wait(500); // 进程已退出 → 输出流随即关闭，这里只是等读取任务收尾
            readErr.Wait(500);
            var esMs = sw.ElapsedMilliseconds;

            var text = DecodeText(ms.ToArray());
            foreach (var raw in text.Split('\n'))
            {
                if (result.Count >= maxResults)
                    break;

                // 解析阶段同样受总时间预算约束：下面判断目录/文件要做文件系统探测
                // （本地是微秒级；万一碰上脱机网络盘会卡死在 SMB 超时上）。一旦超预算
                // 就停止补全剩余条目 —— 宁可少列几条，也要保证"搜索中"的转圈一定会停。
                if (sw.ElapsedMilliseconds > QueryTimeoutMs)
                {
                    Log.Info($"[EverythingSearch] 结果解析超出时间预算 {sw.ElapsedMilliseconds}ms"
                        + $"（已解析 {result.Count} 条），停止补全并标记超时");
                    return (result, true);
                }

                var path = raw.Trim().Trim('"', '\r');
                if (path.Length == 0)
                    continue;

                var folderPath = path.TrimEnd('\\');
                var name = Path.GetFileName(folderPath);

                // 标注目录/文件，驱动行图标（文件夹图标 vs 文件类型图标）与星标显隐。
                // es 输出不带类型信息（默认 txt 格式连尾斜杠都不稳定，json/csv 才带），
                // 所以三级判断，从"零成本"到"可能阻塞"：
                // 1) 带尾斜杠 → 目录（免费）；
                // 2) 网络路径（UNC 或映射到网络的盘符）→ 不做 Exists：脱机/休眠的 NAS 上
                //    一次 Directory.Exists 会一直等到 SMB 超时（数十秒），是"搜索卡住"
                //    最隐蔽的来源；宁可图标退化为文件图标，也不能让界面卡死；
                // 3) 本地路径 → Directory.Exists（本地元数据查询，微秒级）。
                bool isDir;
                if (path.EndsWith('\\'))
                    isDir = true;
                else if (IsNetworkPath(folderPath))
                    isDir = false;
                else
                    isDir = Directory.Exists(folderPath);

                result.Add(new FavoriteFolder
                {
                    Name = string.IsNullOrEmpty(name) ? path : name,
                    Path = folderPath,
                    IsDirectory = isDir,
                });
            }

            if (result.Count == 0 && !string.IsNullOrWhiteSpace(err))
                Log.Info($"[EverythingSearch] es.exe 报错: {err.Trim()}");

            // 慢查询留痕（只在超阈值时写）：把 "es 本身慢" 与 "解析/Exists 判断慢" 分开，
            // 下次再有人说卡，日志里直接能看出瓶颈在哪一段。
            var totalMs = sw.ElapsedMilliseconds;
            if (logSlow && totalMs >= SlowQueryLogMs)
                Log.Info($"[EverythingSearch] 慢查询 {totalMs}ms（es {esMs}ms + 解析 {totalMs - esMs}ms）"
                    + $" 查询='{fullQuery}' 返回{result.Count}条");
        }
        catch (Exception ex)
        {
            Log.Info($"[EverythingSearch] 查询失败: {ex.Message}");
            return (result, false);
        }
        return (result, false);
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
    /// 是否为网络路径：UNC（\\server\share）或映射到网络位置的盘符。
    /// 用途是避免对网络路径做 Directory.Exists —— 脱机/休眠的 NAS 上单次探测会一直
    /// 等到 SMB 超时（数十秒），这是"搜索卡住"最隐蔽的来源。
    /// DriveType 读的是已挂载卷的元数据，成本可忽略；探测失败也按网络路径处理，
    /// 宁可图标退化为文件图标，也不冒阻塞界面/后台线程的风险。
    /// </summary>
    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true; // UNC：\\server\share\...

        if (path.Length < 2 || path[1] != ':')
            return false;

        try
        {
            return new DriveInfo(path.Substring(0, 2)).DriveType == DriveType.Network;
        }
        catch
        {
            return true; // 盘符不存在/无权限：同样不去探它
        }
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

/// <summary>
/// 一次搜索的结果：条目 + 是否因超时提前结束。
/// 之所以要这个标记，是因为"没搜到"和"没搜完"必须给用户不同反馈——
/// 前者是正常结果，后者要提示超时/重试，否则用户会误以为真的没有匹配项。
/// </summary>
internal readonly record struct SearchOutcome(List<FavoriteFolder> Items, bool TimedOut);
