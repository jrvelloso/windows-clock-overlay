namespace WindowsClockOverlay;

internal static class TaskbarPlacementGeometry
{
    public const int TaskbarControlSafetyMarginDip = 6;

    public static bool TryCalculateSafeBounds(
        TaskbarPlacementRegion region,
        Rectangle taskbarBounds,
        Rectangle taskListBounds,
        Rectangle notificationBounds,
        Rectangle? widgetsBounds,
        int dpi,
        int safeAreaMarginDip,
        int farLeftFallbackInsetDip,
        out Rectangle safeBounds,
        out bool usedFarLeftFallback,
        out string? failureReason)
    {
        safeBounds = Rectangle.Empty;
        usedFarLeftFallback = false;
        failureReason = null;
        dpi = dpi > 0 ? dpi : 96;
        safeAreaMarginDip = Math.Clamp(safeAreaMarginDip, 0, 64);
        farLeftFallbackInsetDip = Math.Clamp(farLeftFallbackInsetDip, 0, 1000);

        if (taskbarBounds.Width <= 0 || taskbarBounds.Height <= 0 || taskListBounds.Width <= 0)
        {
            failureReason = "Taskbar and centered task-list bounds are required to calculate a placement region.";
            return false;
        }

        var configuredMarginPx = DipToPixels(safeAreaMarginDip, dpi);
        var safetyMarginPx = DipToPixels(TaskbarControlSafetyMarginDip, dpi);
        int left;
        int right;

        switch (region)
        {
            case TaskbarPlacementRegion.FarLeft:
                if (widgetsBounds is { Width: > 0 } widgets && widgets.IntersectsWith(taskbarBounds))
                {
                    left = widgets.Right + configuredMarginPx;
                }
                else
                {
                    usedFarLeftFallback = true;
                    left = taskbarBounds.Left + DipToPixels(farLeftFallbackInsetDip, dpi) + configuredMarginPx;
                }

                right = taskListBounds.Left - safetyMarginPx;
                break;

            case TaskbarPlacementRegion.CenterGap:
                if (notificationBounds.Width <= 0)
                {
                    failureReason = "The notification area is unavailable, so CenterGap cannot be calculated.";
                    return false;
                }

                left = taskListBounds.Right + safetyMarginPx + configuredMarginPx;
                right = notificationBounds.Left - safetyMarginPx - configuredMarginPx;
                break;

            case TaskbarPlacementRegion.RightGap:
                if (notificationBounds.Width <= 0)
                {
                    failureReason = "The notification area is unavailable, so RightGap cannot be calculated.";
                    return false;
                }

                var centralGapLeft = taskListBounds.Right + safetyMarginPx;
                var centralGapRight = notificationBounds.Left - safetyMarginPx;
                if (centralGapRight <= centralGapLeft)
                {
                    failureReason = "The space between the task list and notification area is too small to define RightGap.";
                    return false;
                }

                left = centralGapLeft + (centralGapRight - centralGapLeft) / 2 + configuredMarginPx;
                right = centralGapRight - configuredMarginPx;
                break;

            default:
                failureReason = $"The placement region '{region}' is not recognized.";
                return false;
        }

        if (left < taskbarBounds.Left || right > taskbarBounds.Right || right <= left)
        {
            var fallbackDescription = usedFarLeftFallback
                ? $" using the configured {farLeftFallbackInsetDip} DIP left inset"
                : string.Empty;
            failureReason = $"The {region} region has no safe horizontal space{fallbackDescription} (calculated x interval [{left},{right})).";
            return false;
        }

        safeBounds = Rectangle.FromLTRB(left, taskbarBounds.Top, right, taskbarBounds.Bottom);
        return true;
    }

    public static bool TryValidateDashboardBounds(
        Rectangle dashboardBounds,
        Rectangle taskbarBounds,
        Rectangle safeBounds,
        IEnumerable<Rectangle> knownControlBounds,
        out string? failureReason)
    {
        failureReason = null;
        if (!Contains(taskbarBounds, dashboardBounds))
        {
            failureReason = $"Dashboard bounds {FormatBounds(dashboardBounds)} extend outside taskbar bounds {FormatBounds(taskbarBounds)}.";
            return false;
        }

        if (!Contains(safeBounds, dashboardBounds))
        {
            failureReason = $"Dashboard bounds {FormatBounds(dashboardBounds)} extend outside the selected safe area {FormatBounds(safeBounds)}.";
            return false;
        }

        foreach (var controlBounds in knownControlBounds.Where(bounds => bounds.Width > 0 && bounds.Height > 0))
        {
            if (!dashboardBounds.IntersectsWith(controlBounds))
            {
                continue;
            }

            failureReason = $"Dashboard bounds {FormatBounds(dashboardBounds)} overlap known taskbar control bounds {FormatBounds(controlBounds)}.";
            return false;
        }

        return true;
    }

    public static int DipToPixels(int dips, int dpi)
    {
        dpi = dpi > 0 ? dpi : 96;
        return Math.Max(0, (int)(((long)Math.Max(0, dips) * dpi + 48) / 96));
    }

    private static bool Contains(Rectangle outer, Rectangle inner) =>
        outer.Width > 0 && outer.Height > 0 &&
        inner.Width > 0 && inner.Height > 0 &&
        inner.Left >= outer.Left && inner.Top >= outer.Top &&
        inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static string FormatBounds(Rectangle bounds) =>
        $"({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}) {bounds.Width}x{bounds.Height}";
}
