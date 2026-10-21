namespace WindowsClockOverlay;

internal static class BatteryDeviceLabelFormatter
{
    public static string ResolveShortName(string friendlyName, string? configuredAlias)
    {
        if (!string.IsNullOrWhiteSpace(configuredAlias))
        {
            return configuredAlias.Trim();
        }

        var normalized = new string((friendlyName ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        if (normalized.Contains("zone300", StringComparison.Ordinal))
        {
            return "Zone 300";
        }

        if (normalized.Contains("mxmaster3s", StringComparison.Ordinal))
        {
            return "MX 3S";
        }

        if (normalized.Contains("lift", StringComparison.Ordinal))
        {
            return "Lift";
        }

        if (normalized.Contains("k68", StringComparison.Ordinal))
        {
            return "K68";
        }

        return string.IsNullOrWhiteSpace(friendlyName) ? "Bluetooth device" : friendlyName.Trim();
    }

    public static string FormatPercentage(BluetoothBatteryDevice device)
    {
        return device.State == BatteryReadingState.Available && device.BatteryPercent is >= 0 and <= 100
            ? device.BatteryPercent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(3) + "%"
            : "—";
    }

    public static string FormatTooltip(BluetoothBatteryDevice device, string shortName)
    {
        var state = device.State switch
        {
            BatteryReadingState.Disconnected => "Disconnected",
            BatteryReadingState.Unavailable => "Battery reading unavailable",
            _ => "Connected"
        };
        var readTime = device.LastReadAt.HasValue
            ? $"Last Windows reading: {device.LastReadAt.Value.ToLocalTime():HH:mm:ss}"
            : "No current Windows battery reading";
        return $"{shortName} — {device.FriendlyName}\n{state}: {FormatPercentage(device).Trim()}\n{readTime}";
    }
}

internal static class TaskbarFullscreenClassifier
{
    public static bool IsGenuineFullscreen(
        Rectangle foregroundBounds,
        Rectangle displayBounds,
        bool isMaximized,
        bool hasCaption,
        bool isShellWindow,
        bool isTaskbarWindow)
    {
        if (isMaximized || hasCaption || isShellWindow || isTaskbarWindow ||
            foregroundBounds.Width <= 0 || foregroundBounds.Height <= 0 ||
            displayBounds.Width <= 0 || displayBounds.Height <= 0)
        {
            return false;
        }

        return foregroundBounds.Left <= displayBounds.Left + 1 &&
            foregroundBounds.Top <= displayBounds.Top + 1 &&
            foregroundBounds.Right >= displayBounds.Right - 1 &&
            foregroundBounds.Bottom >= displayBounds.Bottom - 1;
    }
}

internal static class TaskbarPositioningPolicy
{
    public static bool ShouldSetWindowPos(
        Rectangle currentBounds,
        Rectangle targetBounds,
        bool isVisible,
        bool isAboveTaskbar,
        bool isTopmost) =>
        currentBounds != targetBounds || !isVisible || !isAboveTaskbar || !isTopmost;
}

internal enum TaskbarDashboardVisibilityState
{
    Unavailable,
    Visible,
    FullscreenSuspended
}

internal sealed class DashboardVisibilityTransitionTracker
{
    public TaskbarDashboardVisibilityState? Current { get; private set; }

    public bool MoveTo(TaskbarDashboardVisibilityState next)
    {
        if (Current == next)
        {
            return false;
        }

        Current = next;
        return true;
    }
}
