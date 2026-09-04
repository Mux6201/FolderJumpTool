namespace FolderJumpTool;

/// <summary>
/// 简单的日志输出：同时写 Console 和 Debug。
/// 用 `dotnet run` 直接在终端跑的时候，Console.WriteLine 会直接显示在当前终端窗口里，
/// 不需要额外挂调试器或用 DebugView 之类的工具。
/// </summary>
internal static class Log
{
    public static void Info(string message)
    {
        Console.WriteLine(message);
        System.Diagnostics.Debug.WriteLine(message);
    }
}
