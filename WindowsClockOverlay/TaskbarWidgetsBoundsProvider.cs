using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WindowsClockOverlay;

internal sealed class TaskbarWidgetsBoundsProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

    private IntPtr _cachedTaskbarHandle;
    private DateTimeOffset _cacheExpiresAt;
    private bool _cachedSuccess;
    private Rectangle _cachedBounds;
    private string? _cachedFailureReason;

    public bool TryGetBounds(IntPtr taskbarHandle, Rectangle taskbarBounds, out Rectangle bounds, out string? failureReason)
    {
        if (taskbarHandle == _cachedTaskbarHandle && DateTimeOffset.UtcNow < _cacheExpiresAt)
        {
            bounds = _cachedBounds;
            failureReason = _cachedFailureReason;
            return _cachedSuccess;
        }

        var success = TryReadBounds(taskbarHandle, taskbarBounds, out bounds, out failureReason);
        _cachedTaskbarHandle = taskbarHandle;
        _cacheExpiresAt = DateTimeOffset.UtcNow + CacheDuration;
        _cachedSuccess = success;
        _cachedBounds = bounds;
        _cachedFailureReason = failureReason;
        return success;
    }

    public void Invalidate()
    {
        _cachedTaskbarHandle = IntPtr.Zero;
        _cacheExpiresAt = DateTimeOffset.MinValue;
        _cachedSuccess = false;
        _cachedBounds = Rectangle.Empty;
        _cachedFailureReason = null;
    }

    private static bool TryReadBounds(IntPtr taskbarHandle, Rectangle taskbarBounds, out Rectangle bounds, out string? failureReason)
    {
        bounds = Rectangle.Empty;
        failureReason = null;
        try
        {
            var taskbar = AutomationElement.FromHandle(taskbarHandle);
            if (taskbar is null)
            {
                failureReason = "UI Automation could not open Shell_TrayWnd.";
                return false;
            }

            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton");
            var matches = taskbar.FindAll(TreeScope.Descendants, condition);
            var candidates = new List<Rectangle>();
            for (var index = 0; index < matches.Count; index++)
            {
                try
                {
                    var current = matches[index].Current;
                    if (current.IsOffscreen)
                    {
                        continue;
                    }

                    var boundsValue = current.BoundingRectangle;
                    if (!double.IsFinite(boundsValue.Left) || !double.IsFinite(boundsValue.Top) ||
                        !double.IsFinite(boundsValue.Right) || !double.IsFinite(boundsValue.Bottom))
                    {
                        continue;
                    }

                    var candidate = Rectangle.FromLTRB(
                        (int)Math.Floor(boundsValue.Left),
                        (int)Math.Floor(boundsValue.Top),
                        (int)Math.Ceiling(boundsValue.Right),
                        (int)Math.Ceiling(boundsValue.Bottom));
                    if (candidate.Width > 0 && candidate.Height > 0 && candidate.IntersectsWith(taskbarBounds))
                    {
                        candidates.Add(candidate);
                    }
                }
                catch (ElementNotAvailableException)
                {
                    // Explorer can replace UI Automation elements while the taskbar is updating.
                }
            }

            if (candidates.Count == 0)
            {
                failureReason = "UI Automation did not expose a visible WidgetsButton inside the primary taskbar.";
                return false;
            }

            bounds = Rectangle.FromLTRB(
                candidates.Min(candidate => candidate.Left),
                candidates.Min(candidate => candidate.Top),
                candidates.Max(candidate => candidate.Right),
                candidates.Max(candidate => candidate.Bottom));
            if (bounds.Left > taskbarBounds.Left + taskbarBounds.Width / 3 || bounds.Right > taskbarBounds.Left + taskbarBounds.Width / 2)
            {
                failureReason = $"WidgetsButton bounds {FormatBounds(bounds)} are not in the left-side taskbar region.";
                bounds = Rectangle.Empty;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is COMException or ElementNotAvailableException or InvalidOperationException or InvalidCastException)
        {
            failureReason = $"UI Automation could not read WidgetsButton bounds ({exception.GetType().Name}: {exception.Message}).";
            return false;
        }
    }

    private static string FormatBounds(Rectangle bounds) =>
        $"({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}) {bounds.Width}x{bounds.Height}";
}
