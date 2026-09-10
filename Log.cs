using System.Collections.Concurrent;
using System.IO;

namespace FolderJumpTool;

/// <summary>
/// 异步日志：调用方只入内存队列（无 IO、无锁竞争），专用后台线程落盘。
/// 之前每条日志同步 Directory.CreateDirectory + File.AppendAllText（开-写-关），
/// 日志爆发时（实测峰值 3.6 万行/小时）UI 线程被磁盘 IO 卡住，表现为"卡卡的"。
/// 文件超过 2MB 自动轮转成 app.log.old（单份备份，避免无限增长）。
/// 磁盘日志在 %LocalAppData%\FolderJumpTool\app.log——排查"事件没触发/悬浮窗没显示"
/// 这类时序问题时直接读文件即可。极端情况（队列满/写盘失败）丢日志不丢功能。
/// </summary>
internal static class Log
{
    private static readonly BlockingCollection<string> Queue = new(4096);
    private static readonly object Gate = new();
    private static int _linesSinceRotate;

    static Log()
    {
        var writer = new Thread(Drain)
        {
            IsBackground = true,
            Name = "fjt-log-writer",
        };
        writer.Start();
    }

    public static void Info(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        try
        {
            Queue.TryAdd($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
        catch
        {
            // 队列满（极端爆发）直接丢这条，绝不反压调用方
        }
    }

    private static void Drain()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderJumpTool");
        var path = Path.Combine(dir, "app.log");

        // 启动时先检查一次：把上次遗留的超限日志立即归档，
        // 否则要等写满 2000 行才触发检查，大文件会一直挂在那里。
        TryRotate(dir, path);

        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(dir);

                    // 每 2000 行做一次大小检查（元数据读取，开销可忽略）：
                    // 超 2MB 轮转 app.log → app.log.old（覆盖旧备份）
                    if (++_linesSinceRotate >= 2000)
                    {
                        _linesSinceRotate = 0;
                        TryRotate(dir, path);
                    }

                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败绝不影响主流程
            }
        }
    }

    /// <summary>日志超过 2MB 就轮转成 app.log.old（单份备份，覆盖旧的）。</summary>
    private static void TryRotate(string dir, string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= 2 * 1024 * 1024)
                return;

            var old = Path.Combine(dir, "app.log.old");
            File.Delete(old);
            File.Move(path, old);
        }
        catch
        {
            // 轮转失败不影响写日志
        }
    }
}
