using System.IO;

namespace FolderJumpTool;

/// <summary>
/// 简单的日志输出：同时写 Console、Debug 与磁盘文件。
/// 磁盘日志在 %LocalAppData%\FolderJumpTool\app.log——后台运行时没有控制台，
/// 排查"事件没触发/悬浮窗没显示"这类时序问题时直接读文件即可。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message)
    {
        Console.WriteLine(message);
        System.Diagnostics.Debug.WriteLine(message);

        try
        {
            lock (Gate)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderJumpTool");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "app.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }
}
