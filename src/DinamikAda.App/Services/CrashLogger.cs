using System.Globalization;
using System.IO;
using System.Text;

namespace DinamikAda.App.Services;

/// <summary>
/// Minimal, dependency-free crash/diagnostic log writer. Appends to
/// %LOCALAPPDATA%\DinamikAda\logs\dinamikada.log and rolls the file over once it grows past 1 MB.
/// Never throws.
/// </summary>
public static class CrashLogger
{
    private const long MaxLogBytes = 1024 * 1024;
    private static readonly object Sync = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DinamikAda",
        "logs");

    public static string LogPath => Path.Combine(LogDirectory, "dinamikada.log");

    public static void Log(string category, Exception? ex, string? message = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(" [").Append(category).Append("] ");
            if (!string.IsNullOrEmpty(message)) sb.Append(message).Append(' ');
            if (ex is not null) sb.AppendLine().Append(ex);
            sb.AppendLine();

            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    string old = Path.Combine(LogDirectory, "dinamikada.old.log");
                    File.Copy(LogPath, old, overwrite: true);
                    File.Delete(LogPath);
                }
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the process down.
        }
    }
}
