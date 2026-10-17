using System.Globalization;
using System.Text;

namespace BlindsDown.Infrastructure;

/// <summary>
/// A tiny local diagnostic log (%LocalAppData%\BlindsDown\blindsdown.log, max ~1 MB, one old copy kept).
/// It records lock/unlock steps and errors to help with bug reports. It never records keystrokes,
/// window titles or anything typed, and it never leaves this PC.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Initialize(string path)
    {
        lock (Gate)
        {
            _path = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_path is null)
            {
                return;
            }

            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".old", overwrite: true);
                }

                string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} [{Environment.CurrentManagedThreadId}] {message}\n");
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
