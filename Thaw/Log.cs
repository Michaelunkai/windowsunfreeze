using System.Text;

namespace Thaw;

/// <summary>Rotating file logger (hidden from the user, no console involved).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _debug;
    private const long MaxBytes = 1_000_000;

    internal static string LogPath => _path ?? DefaultPath();

    internal static string DefaultPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thaw");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "log.txt");
    }

    internal static void Init(bool debug = false)
    {
        lock (Gate)
        {
            _path = DefaultPath();
            _debug = debug;
            RotateIfNeeded();
            Info("Thaw started. Version " + typeof(Log).Assembly.GetName().Version);
        }
    }

    internal static void Debug(string msg)
    {
        if (_debug) Write("DBG", msg);
    }

    internal static void Info(string msg) => Write("INF", msg);

    internal static void Warn(string msg) => Write("WRN", msg);

    internal static void Error(string msg, Exception? ex = null)
    {
        Write("ERR", ex is null ? msg : msg + " :: " + ex.GetType().Name + ": " + ex.Message);
    }

    private static void Write(string level, string msg)
    {
        lock (Gate)
        {
            try
            {
                if (_path is null) _path = DefaultPath();
                RotateIfNeeded();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}";
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }

    private static void RotateIfNeeded()
    {
        if (_path is null) return;
        try
        {
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                string old = _path + ".old.txt";
                File.Delete(old);
                File.Move(_path, old);
            }
        }
        catch
        {
            // Ignore rotation failures.
        }
    }
}
