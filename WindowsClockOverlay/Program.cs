using System.IO;

namespace WindowsClockOverlay;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\WindowsClockOverlay", out var isFirstInstance);
        if (!isFirstInstance)
        {
            DashboardDiagnostics.Write("Startup", "A second instance was requested and exited because the single-instance mutex is already owned.");
            return;
        }

        var executablePath = Environment.ProcessPath ?? string.Empty;
        var executableModified = File.Exists(executablePath) ? File.GetLastWriteTime(executablePath).ToString("O") : "unknown";
        var moduleVersionId = typeof(Program).Assembly.ManifestModule.ModuleVersionId;
        DashboardDiagnostics.Write(
            "Startup",
            $"PID={Environment.ProcessId}; executable={executablePath}; executableLastWrite={executableModified}; assembly={typeof(Program).Assembly.Location}; moduleVersionId={moduleVersionId}; diagnostics={DashboardDiagnostics.FilePath}");

        ApplicationConfiguration.Initialize();
        Application.Run(new ClockOverlayForm());
    }
}
