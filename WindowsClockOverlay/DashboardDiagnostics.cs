using System.Diagnostics;
using System.IO;
using System.Text;

namespace WindowsClockOverlay;

internal static class DashboardDiagnostics
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsClockOverlay",
        "diagnostics.log");

    public static string FilePath => LogPath;

    public static void Write(string category, string message)
    {
        var entry = $"{DateTimeOffset.Now:O} [{category}] {message}{Environment.NewLine}";
        Trace.Write(entry);

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, entry, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Could not append dashboard diagnostics to {LogPath}: {exception.Message}");
        }
    }
}
