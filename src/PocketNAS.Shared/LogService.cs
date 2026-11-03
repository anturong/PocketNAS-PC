namespace PocketNAS.Shared;

public enum LogLevel
{
    Info,
    Success,
    Warn,
    Error
}

public sealed record LogEntry(DateTime Time, string Module, string Message, LogLevel Level);

/// <summary>
/// 全局共享日志。各模块只负责写入，由外壳统一渲染到右侧日志面板。
/// </summary>
public static class LogService
{
    public static event Action<LogEntry>? Written;

    public static void Info(string module, string message) => Emit(module, message, LogLevel.Info);
    public static void Success(string module, string message) => Emit(module, message, LogLevel.Success);
    public static void Warn(string module, string message) => Emit(module, message, LogLevel.Warn);
    public static void Error(string module, string message) => Emit(module, message, LogLevel.Error);

    public static void Write(string module, string message, LogLevel level)
        => Emit(module, message, level);

    private static void Emit(string module, string message, LogLevel level)
        => Written?.Invoke(new LogEntry(DateTime.Now, module, message, level));
}
