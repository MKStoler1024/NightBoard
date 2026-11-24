using System;
using System.IO;
using System.Threading.Tasks;

namespace NightBoard.Services;

/// <summary>
/// 崩溃兜底：未捕获异常写进 %LOCALAPPDATA%\NightBoard\crash.log，
/// 下次启动时如果看到上次的崩溃记录，会在日志里标出来。
/// </summary>
public static class CrashLog
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightBoard");

    public static string FilePath => Path.Combine(Dir, "crash.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write(e.ExceptionObject as Exception, "AppDomain");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "UnobservedTask");
            e.SetObserved();
        };
    }

    /// <summary>上次运行是否留下了崩溃记录（启动时调用一次）。</summary>
    public static bool HasPreviousCrash()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            var info = new FileInfo(FilePath);
            return info.Length > 0 && info.LastWriteTime > DateTime.Now.AddDays(-3);
        }
        catch
        {
            return false;
        }
    }

    public static void Write(Exception? ex, string source)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n";
            File.AppendAllText(FilePath, text);
        }
        catch
        {
            // 兜底再失败就没办法了
        }
    }
}
