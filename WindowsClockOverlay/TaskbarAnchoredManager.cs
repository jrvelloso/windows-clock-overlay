using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsClockOverlay;

internal sealed class TaskbarAnchoredManager : IDisposable
{
    private const int WmDpiChanged = 0x02E0;
    private const int GwlStyle = -16;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const long WsCaption = 0x00C00000;
    private const uint GwHwndNext = 2;
    private const int DefaultPollIntervalMs = 500;
    private const int TaskbarDisplayChangeMessage = 0x007E;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private readonly ClockOverlayForm _host;
    private readonly System.Windows.Forms.Timer _geometryTimer;
    private readonly TaskbarWidgetsBoundsProvider _widgetsBoundsProvider = new();
    private readonly DashboardVisibilityTransitionTracker _visibilityTransitions = new();
    private readonly List<TaskbarChild> _children = [];
    private readonly EnumChildProc _enumChildProc;
    private bool _shouldBeAnchored;
    private bool _isDisposed;
    private bool _isAvailable;
    private bool _isFullscreenSuspended;
    private bool _isTopmost;
    private Rectangle? _lastPosition;
    private string? _lastFailedLayoutKey;
    private string? _lastLoggedTaskbarGeometryKey;
    private string? _lastLoggedPlacementKey;
    private string? _lastLoggedZOrderState;
    private bool? _lastLoggedFullscreenClassification;
    private IntPtr _lastLoggedForegroundWindowHandle;
    private bool _lastLoggedForegroundCoversDisplay;
    private string? _lastFullscreenDiagnostic;
    private DateTime _lastStabilitySummaryAtUtc = DateTime.UtcNow;
    private long _geometryRefreshCount;
    private long _setWindowPosCount;
    private long _redundantPositionOperationsSkipped;
    private bool _placementStarting;
    private int _horizontalOffsetDip;
    private int _safeAreaMarginDip = 8;
    private int _farLeftFallbackInsetDip = 180;
    private TaskbarHorizontalAlignment _horizontalAlignment = TaskbarHorizontalAlignment.Left;
    private TaskbarPlacementRegion _placementRegion = TaskbarPlacementRegion.FarLeft;

    public TaskbarAnchoredManager(ClockOverlayForm host)
    {
        _host = host;
        _enumChildProc = OnTaskbarChildEnumerated;
        _geometryTimer = new System.Windows.Forms.Timer { Interval = DefaultPollIntervalMs };
        _geometryTimer.Tick += OnGeometryTimerTick;
    }

    public event Action<bool, string?>? AvailabilityChanged;
    public event Action? PlacementStarting;

    public string? LastFailureReason { get; private set; }

    public bool IsAvailable => _isAvailable;

    public bool IsFullscreenSuspended => _isFullscreenSuspended;

    public bool Start(
        int horizontalOffsetDip,
        int safeAreaMarginDip,
        TaskbarHorizontalAlignment horizontalAlignment,
        int farLeftFallbackInsetDip,
        TaskbarPlacementRegion placementRegion)
    {
        _shouldBeAnchored = true;
        _horizontalOffsetDip = Math.Clamp(horizontalOffsetDip, 0, 2000);
        _safeAreaMarginDip = Math.Clamp(safeAreaMarginDip, 0, 64);
        _horizontalAlignment = Enum.IsDefined(horizontalAlignment) ? horizontalAlignment : TaskbarHorizontalAlignment.Left;
        _farLeftFallbackInsetDip = Math.Clamp(farLeftFallbackInsetDip, 0, 1000);
        _placementRegion = Enum.IsDefined(placementRegion) ? placementRegion : TaskbarPlacementRegion.FarLeft;
        _lastFailedLayoutKey = null;
        _lastLoggedTaskbarGeometryKey = null;
        _lastLoggedPlacementKey = null;
        _lastPosition = null;
        LastFailureReason = null;

        DashboardDiagnostics.Write(
            "TaskbarAnchored",
            $"Requested placement; selected region={_placementRegion}; dashboard width=calculated from enabled widgets and safe space; horizontal alignment={_horizontalAlignment}; safe-area margin={_safeAreaMarginDip} DIP; far-left fallback inset={_farLeftFallbackInsetDip} DIP; additional left offset={_horizontalOffsetDip} DIP; host DPI={_host.DeviceDpi}.");

        if (_isDisposed || !_host.IsHandleCreated)
        {
            return false;
        }

        _geometryTimer.Start();
        return RefreshPlacement(raiseAvailabilityEvent: false);
    }

    public void Stop()
    {
        if (_shouldBeAnchored || _isAvailable)
        {
            DashboardDiagnostics.Write("TaskbarAnchored", "Stopped anchored placement polling.");
        }

        _shouldBeAnchored = false;
        _geometryTimer.Stop();
        _isAvailable = false;
        _isFullscreenSuspended = false;
        _lastPosition = null;
        LogVisibilityTransition(TaskbarDashboardVisibilityState.Unavailable, "Taskbar Anchored manager stopped", null);

        if (_isTopmost && _host.IsHandleCreated && !_host.IsDisposed)
        {
            SetTopmost(false);
        }
    }

    public void UpdateLayout(
        int horizontalOffsetDip,
        int safeAreaMarginDip,
        TaskbarHorizontalAlignment horizontalAlignment,
        int farLeftFallbackInsetDip,
        TaskbarPlacementRegion placementRegion)
    {
        _horizontalOffsetDip = Math.Clamp(horizontalOffsetDip, 0, 2000);
        _safeAreaMarginDip = Math.Clamp(safeAreaMarginDip, 0, 64);
        _horizontalAlignment = Enum.IsDefined(horizontalAlignment) ? horizontalAlignment : TaskbarHorizontalAlignment.Left;
        _farLeftFallbackInsetDip = Math.Clamp(farLeftFallbackInsetDip, 0, 1000);
        _placementRegion = Enum.IsDefined(placementRegion) ? placementRegion : TaskbarPlacementRegion.FarLeft;
        _lastFailedLayoutKey = null;
        NotifyGeometryChanged();
    }

    public void NotifyGeometryChanged()
    {
        if (_isDisposed || !_shouldBeAnchored || !_host.IsHandleCreated || _host.IsDisposed)
        {
            return;
        }

        try
        {
            _host.BeginInvoke((MethodInvoker)(() => RefreshPlacement(raiseAvailabilityEvent: true)));
        }
        catch (InvalidOperationException)
        {
            // The form can be closing while Windows broadcasts display changes.
        }
    }

    public bool ProcessWindowMessage(ref Message message)
    {
        if (_shouldBeAnchored && TaskbarCreatedMessage != 0 && (uint)message.Msg == TaskbarCreatedMessage)
        {
            DashboardDiagnostics.Write("Explorer", $"Received TaskbarCreated; prior taskbar HWND=0x{FindWindow("Shell_TrayWnd", null).ToInt64():X}; invalidating Widgets bounds and scheduling Taskbar Anchored recovery.");
            _widgetsBoundsProvider.Invalidate();
            _lastFailedLayoutKey = null;
            NotifyGeometryChanged();
            return true;
        }

        if (_shouldBeAnchored && message.Msg is WmDpiChanged or TaskbarDisplayChangeMessage)
        {
            DashboardDiagnostics.Write("TaskbarAnchored", $"Received {(message.Msg == WmDpiChanged ? "WM_DPICHANGED" : "WM_DISPLAYCHANGE")}; invalidating Widgets bounds and scheduling geometry refresh.");
            _widgetsBoundsProvider.Invalidate();
            NotifyGeometryChanged();
        }

        return false;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Stop();
        _isDisposed = true;
        _geometryTimer.Stop();
        _geometryTimer.Tick -= OnGeometryTimerTick;
        _geometryTimer.Dispose();
        AvailabilityChanged = null;
        PlacementStarting = null;
    }

    private void OnGeometryTimerTick(object? sender, EventArgs e)
    {
        RefreshPlacement(raiseAvailabilityEvent: true);
        WriteStabilitySummaryIfDue();
    }

    private bool RefreshPlacement(bool raiseAvailabilityEvent)
    {
        if (_isDisposed || !_shouldBeAnchored || !_host.IsHandleCreated || _host.IsDisposed)
        {
            return false;
        }

        _geometryRefreshCount++;

        try
        {
            var primaryScreen = Screen.PrimaryScreen;
            if (primaryScreen is not null && IsUnrelatedFullscreenWindowForeground(primaryScreen.Bounds))
            {
                _lastFailedLayoutKey = null;
                if (!_isAvailable && !_placementStarting)
                {
                    _placementStarting = true;
                    PlacementStarting?.Invoke();
                }

                SuspendForFullscreen();
                LastFailureReason = null;
                return true;
            }

            if (!TryGetTaskbarLayout(out var layout, out var failureReason))
            {
                return SetUnavailable(failureReason, raiseAvailabilityEvent);
            }

            var windowDpi = GetDpiForWindow(_host.Handle);
            var dpi = windowDpi > 0 ? (int)windowDpi : (_host.DeviceDpi > 0 ? _host.DeviceDpi : 96);
            if (!TaskbarPlacementGeometry.TryCalculateSafeBounds(
                    _placementRegion,
                    layout.TaskbarBounds,
                    layout.TaskListBounds,
                    layout.NotificationBounds,
                    layout.WidgetsBounds,
                    dpi,
                    _safeAreaMarginDip,
                    _farLeftFallbackInsetDip,
                    out var safeBounds,
                    out var usedFarLeftFallback,
                    out var safeBoundsFailure))
            {
                var reason = safeBoundsFailure ?? $"The selected {_placementRegion} placement region is unavailable.";
                _lastFailedLayoutKey = layout.Key + $"|{_placementRegion}|{dpi}|{reason}";
                DashboardDiagnostics.Write(
                    "TaskbarAnchored",
                    $"Placement rejected; selected region={_placementRegion}; taskbar bounds={FormatBounds(layout.TaskbarBounds)}; Widgets/weather bounds={FormatOptionalBounds(layout.WidgetsBounds)}; centered task-list bounds={FormatBounds(layout.TaskListBounds)}; notification-area bounds={FormatBounds(layout.NotificationBounds)}; fallback reason={reason}");
                return SetUnavailable(reason, raiseAvailabilityEvent);
            }

            var availablePx = safeBounds.Width;
            var availableDip = availablePx * 96d / dpi;
            var safeAreaInsetPx = TaskbarPlacementGeometry.DipToPixels(_safeAreaMarginDip, dpi);
            var requestedOffsetPx = TaskbarPlacementGeometry.DipToPixels(_horizontalOffsetDip, dpi);
            var effectiveOffsetPx = _horizontalAlignment == TaskbarHorizontalAlignment.Left ? requestedOffsetPx : 0;
            var measurementLeft = safeBounds.Left + effectiveOffsetPx;
            var usableAfterOffsetPx = safeBounds.Right - measurementLeft;
            var maximumBounds = new Rectangle(measurementLeft, layout.TaskbarBounds.Top, Math.Max(0, usableAfterOffsetPx), layout.TaskbarBounds.Height);
            if (safeBounds.Width <= 0 || usableAfterOffsetPx <= 0)
            {
                _lastFailedLayoutKey = layout.Key + $"|{_placementRegion}|{_horizontalOffsetDip}|{_safeAreaMarginDip}|{_horizontalAlignment}|{dpi}";
                var reason = $"The selected {_placementRegion} region has {Math.Max(0, availableDip):0.##} DIP of safe space, and the configured offset/alignment leaves no room for a dashboard.";
                DashboardDiagnostics.Write("TaskbarAnchored", $"Placement rejected; selected region={_placementRegion}; safe bounds={FormatBounds(safeBounds)}; reason={reason}");
                return SetUnavailable(reason, raiseAvailabilityEvent);
            }

            if (!_host.TryCalculateTaskbarAnchoredWidth(maximumBounds, dpi, out var widthPx, out var widthError))
            {
                _lastFailedLayoutKey = layout.Key + $"|{_placementRegion}|{_horizontalOffsetDip}|{_safeAreaMarginDip}|{_horizontalAlignment}|{dpi}|{widthError}";
                DashboardDiagnostics.Write("TaskbarAnchored", $"Widget layout rejected; selected region={_placementRegion}; safe bounds={FormatBounds(safeBounds)}; available safe space={usableAfterOffsetPx} px; reason={widthError}");
                return SetUnavailable(widthError ?? "No enabled dashboard widgets fit in the safe taskbar area.", raiseAvailabilityEvent);
            }

            var target = CalculateDashboardBounds(safeBounds, widthPx, _horizontalAlignment, effectiveOffsetPx);
            var attemptKey = layout.Key + $"|{_placementRegion}|{_horizontalOffsetDip}|{_safeAreaMarginDip}|{_farLeftFallbackInsetDip}|{_horizontalAlignment}|{dpi}|{widthPx}";
            if (_lastLoggedPlacementKey != attemptKey)
            {
                DashboardDiagnostics.Write(
                    "TaskbarAnchored",
                    $"Taskbar HWND=0x{layout.TaskbarHandle.ToInt64():X}; taskbar bounds={FormatBounds(layout.TaskbarBounds)}; Widgets/weather bounds={FormatOptionalBounds(layout.WidgetsBounds)}; Widgets lookup={layout.WidgetsLookupFailureReason ?? "available"}; centered task-list bounds={FormatBounds(layout.TaskListBounds)}; notification-area bounds={FormatBounds(layout.NotificationBounds)}; selected placement region={_placementRegion}; calculated safe bounds={FormatBounds(safeBounds)}; available horizontal space={availablePx} px / {availableDip:0.##} DIP; used configurable far-left fallback={usedFarLeftFallback}; width calculation starts at x={measurementLeft}; usable after configured offset={usableAfterOffsetPx} px; horizontal alignment={_horizontalAlignment}; configured safe-area margin={_safeAreaMarginDip} DIP ({safeAreaInsetPx} px); far-left fallback inset={_farLeftFallbackInsetDip} DIP; additional left offset={_horizontalOffsetDip} DIP ({requestedOffsetPx} px, applied={effectiveOffsetPx} px); calculated widget width={widthPx} px; calculated dashboard bounds={FormatBounds(target)}; layout status={_host.TaskbarLayoutStatus}; GetDpiForWindow={windowDpi}; DeviceDpi={_host.DeviceDpi}; used DPI={dpi}.");
                _lastLoggedPlacementKey = attemptKey;
            }

            if (!_isAvailable && _lastFailedLayoutKey == attemptKey)
            {
                return false;
            }

            string? geometryFailure = null;
            var geometryValid = widthPx > 0 && TaskbarPlacementGeometry.TryValidateDashboardBounds(
                target,
                layout.TaskbarBounds,
                safeBounds,
                [layout.WidgetsBounds ?? Rectangle.Empty, layout.TaskListBounds, layout.NotificationBounds],
                out geometryFailure);
            if (!geometryValid)
            {
                _lastFailedLayoutKey = attemptKey;
                var reason = widthPx <= 0
                    ? "The calculated dashboard width is not positive."
                    : geometryFailure ?? $"The calculated dashboard bounds {FormatBounds(target)} do not fit in selected safe bounds {FormatBounds(safeBounds)}.";
                DashboardDiagnostics.Write("TaskbarAnchored", $"Placement rejected; selected region={_placementRegion}; safe bounds={FormatBounds(safeBounds)}; dashboard bounds={FormatBounds(target)}; collision/containment reason={reason}");
                return SetUnavailable(reason, raiseAvailabilityEvent);
            }

            if (!_isAvailable && !_placementStarting)
            {
                _placementStarting = true;
                PlacementStarting?.Invoke();
            }

            var currentBoundsRead = TryGetWindowRect(_host.Handle, out var currentBounds);
            if (!currentBoundsRead)
            {
                currentBounds = Rectangle.Empty;
            }

            var positionChanged = currentBounds != target || _lastPosition != target;
            var wasAvailable = _isAvailable;
            if ((!wasAvailable || positionChanged) && !_host.PrepareTaskbarAnchoredLayout(target, dpi))
            {
                _lastFailedLayoutKey = attemptKey;
                return SetUnavailable("The enabled dashboard widgets could not be laid out within the available taskbar area.", raiseAvailabilityEvent);
            }

            var currentZOrder = InspectWindowZOrder(_host.Handle, layout.TaskbarHandle);
            var wasVisible = IsWindowVisible(_host.Handle);
            var actualTopmost = currentZOrder.DashboardTopmost;
            var shouldRaise = !actualTopmost || currentZOrder.IsAboveTaskbar != true;
            var shouldSetWindowPos = TaskbarPositioningPolicy.ShouldSetWindowPos(
                currentBounds,
                target,
                wasVisible,
                currentZOrder.IsAboveTaskbar == true,
                actualTopmost);

            if (shouldSetWindowPos)
            {
                SetTopmost(true);
                var flags = SwpNoActivate | SwpNoOwnerZOrder;
                var nativeBoundsChanged = !currentBoundsRead || currentBounds != target;
                if (!nativeBoundsChanged)
                {
                    flags |= SwpNoMove | SwpNoSize;
                }

                if (!wasVisible)
                {
                    flags |= SwpShowWindow;
                }

                if (!shouldRaise)
                {
                    flags |= SwpNoZOrder;
                }

                if (!NativeMethods.SetWindowPos(_host.Handle, HwndTopmost, target.Left, target.Top, target.Width, target.Height, flags))
                {
                    _lastFailedLayoutKey = attemptKey;
                    var reason = $"Windows could not position the dashboard over the taskbar (Win32 error {Marshal.GetLastWin32Error()}).";
                    DashboardDiagnostics.Write("TaskbarAnchored", $"Placement rejected; calculated dashboard bounds={FormatBounds(target)}; reason={reason}");
                    return SetUnavailable(reason, raiseAvailabilityEvent);
                }

                _setWindowPosCount++;
                DashboardDiagnostics.Write(
                    "Positioning",
                    $"SetWindowPos applied; reason={(nativeBoundsChanged ? "bounds changed" : !wasVisible ? "dashboard was hidden" : "Z-order correction")}; before={FormatBounds(currentBounds)}; target={FormatBounds(target)}; visible before={wasVisible}; was above taskbar={currentZOrder.IsAboveTaskbar}; topmost before={actualTopmost}; flags=0x{flags:X}.");
            }
            else
            {
                _redundantPositionOperationsSkipped++;
            }

            var actualBoundsRead = TryGetWindowRect(_host.Handle, out var actualBounds);
            var isVisible = IsWindowVisible(_host.Handle);
            var zOrder = shouldSetWindowPos
                ? InspectWindowZOrder(_host.Handle, layout.TaskbarHandle)
                : currentZOrder;
            var zOrderState = FormatWindowState(_host.Handle, layout.TaskbarHandle, actualBoundsRead, actualBounds, isVisible, zOrder);
            var zOrderStateKey = FormatWindowStateKey(actualBoundsRead, actualBounds, isVisible, zOrder, layout.TaskbarHandle);
            if (_lastLoggedZOrderState != zOrderStateKey)
            {
                DashboardDiagnostics.Write("WindowState", zOrderState);
                _lastLoggedZOrderState = zOrderStateKey;
            }

            if (!actualBoundsRead || actualBounds != target)
            {
                var actual = actualBoundsRead ? FormatBounds(actualBounds) : "unavailable";
                var reason = $"Windows did not place the dashboard at the requested taskbar bounds (requested {FormatBounds(target)}, actual {actual}).";
                _lastFailedLayoutKey = null;
                DashboardDiagnostics.Write("TaskbarAnchored", $"Placement verification failed; reason={reason}");
                return SetUnavailable(reason, raiseAvailabilityEvent);
            }

            if (!isVisible)
            {
                var reason = "Windows positioned the dashboard but did not make it visible.";
                _lastFailedLayoutKey = null;
                DashboardDiagnostics.Write("TaskbarAnchored", $"Placement verification failed; reason={reason}");
                return SetUnavailable(reason, raiseAvailabilityEvent);
            }

            if (zOrder.IsAboveTaskbar != true)
            {
                var reason = zOrder.IsAboveTaskbar == false
                    ? "Windows kept the dashboard behind the taskbar, so it cannot be shown reliably inside the taskbar."
                    : "The dashboard and taskbar Z-order could not be verified, so the dashboard was kept hidden.";
                _lastFailedLayoutKey = null;
                DashboardDiagnostics.Write("TaskbarAnchored", $"Placement verification failed; {zOrderState}; reason={reason}");
                return SetUnavailable(reason, raiseAvailabilityEvent);
            }

            _isAvailable = true;
            _isFullscreenSuspended = false;
            _lastPosition = target;
            _lastFailedLayoutKey = null;
            _placementStarting = false;
            LastFailureReason = null;
            LogVisibilityTransition(TaskbarDashboardVisibilityState.Visible, "taskbar placement verified", actualBounds);

            if (raiseAvailabilityEvent && !wasAvailable)
            {
                AvailabilityChanged?.Invoke(true, null);
            }

            if (!wasAvailable || positionChanged)
            {
                DashboardDiagnostics.Write("TaskbarAnchored", $"Placement succeeded; effective dashboard bounds={FormatBounds(actualBounds)}; AppBar registered={_host.AppBarIsRegistered}; visible={isVisible}; {zOrderState}");
            }

            return true;
        }
        catch (Exception exception) when (IsRecoverableNativeFailure(exception))
        {
            Trace.TraceWarning("Taskbar anchored positioning failed: {0}", exception);
            var reason = "Windows could not read the taskbar layout or position the dashboard.";
            DashboardDiagnostics.Write("TaskbarAnchored", $"Placement threw {exception.GetType().Name}: {exception.Message}; reason={reason}");
            return SetUnavailable(reason, raiseAvailabilityEvent);
        }
    }

    private bool TryGetTaskbarLayout(out TaskbarLayout layout, out string failureReason)
    {
        layout = default;
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        var taskbarBounds = Rectangle.Empty;
        var taskbarBoundsRead = taskbarHandle != IntPtr.Zero && TryGetWindowRect(taskbarHandle, out taskbarBounds);
        if (!taskbarBoundsRead)
        {
            var missingGeometryKey = $"missing|{taskbarHandle.ToInt64()}";
            if (_lastLoggedTaskbarGeometryKey != missingGeometryKey)
            {
                DashboardDiagnostics.Write("TaskbarGeometry", $"Shell_TrayWnd HWND=0x{taskbarHandle.ToInt64():X}; GetWindowRect succeeded={taskbarBoundsRead}; bounds unavailable.");
                _lastLoggedTaskbarGeometryKey = missingGeometryKey;
            }
            failureReason = "The primary Windows taskbar is unavailable.";
            return false;
        }

        var primaryScreen = Screen.PrimaryScreen;
        if (primaryScreen is null || primaryScreen.Bounds.Width <= 0 || primaryScreen.Bounds.Height <= 0)
        {
            DashboardDiagnostics.Write("TaskbarGeometry", $"Shell_TrayWnd HWND=0x{taskbarHandle.ToInt64():X}; bounds={FormatBounds(taskbarBounds)}; primary display bounds unavailable.");
            failureReason = "The primary display is unavailable.";
            return false;
        }

        var primaryBounds = primaryScreen.Bounds;
        var isBottomTaskbar = taskbarBounds.Height > 0 && taskbarBounds.Width > 0 &&
            Math.Abs(taskbarBounds.Bottom - primaryBounds.Bottom) <= 2 &&
            taskbarBounds.Left <= primaryBounds.Left + 2 && taskbarBounds.Right >= primaryBounds.Right - 2;
        if (!isBottomTaskbar)
        {
            failureReason = "Taskbar Anchored currently supports a visible bottom taskbar on the primary display only.";
            DashboardDiagnostics.Write("TaskbarGeometry", $"Layout rejected for Shell_TrayWnd HWND=0x{taskbarHandle.ToInt64():X}; taskbar bounds={FormatBounds(taskbarBounds)}; primary display bounds={FormatBounds(primaryBounds)}; reason={failureReason}");
            return false;
        }

        _children.Clear();
        EnumChildWindows(taskbarHandle, _enumChildProc, IntPtr.Zero);
        var childSummary = string.Join("; ", _children
            .OrderBy(child => child.ClassName, StringComparer.Ordinal)
            .ThenBy(child => child.Bounds.Left)
            .Select(child => $"{child.ClassName} HWND=0x{child.WindowHandle.ToInt64():X} bounds={FormatBounds(child.Bounds)}"));
        var geometryKey = $"{taskbarHandle.ToInt64()}|{FormatBounds(taskbarBounds)}|{FormatBounds(primaryBounds)}|{childSummary}";
        if (_lastLoggedTaskbarGeometryKey != geometryKey)
        {
            DashboardDiagnostics.Write(
                "TaskbarGeometry",
                $"Shell_TrayWnd HWND=0x{taskbarHandle.ToInt64():X}; bounds={FormatBounds(taskbarBounds)}; primary display bounds={FormatBounds(primaryBounds)}; child windows=[{childSummary}].");
            _lastLoggedTaskbarGeometryKey = geometryKey;
        }

        var taskLists = _children.Where(child => child.ClassName is "MSTaskListWClass" or "MSTaskSwWClass")
            .Select(child => child.Bounds)
            .Where(bounds => bounds.Width > 0 && bounds.Height > 0 && bounds.IntersectsWith(taskbarBounds))
            .ToArray();
        var notificationAreas = _children.Where(child => child.ClassName == "TrayNotifyWnd")
            .Select(child => child.Bounds)
            .Where(bounds => bounds.Width > 0 && bounds.Height > 0 && bounds.IntersectsWith(taskbarBounds))
            .ToArray();

        if (taskLists.Length == 0 || (_placementRegion != TaskbarPlacementRegion.FarLeft && notificationAreas.Length == 0))
        {
            failureReason = _placementRegion == TaskbarPlacementRegion.FarLeft
                ? "Windows did not expose a recognizable centered task-list area. The dashboard was not placed over taskbar controls."
                : $"Windows did not expose a recognizable centered task-list or notification area required for {_placementRegion}. The dashboard was not placed over taskbar controls.";
            DashboardDiagnostics.Write("TaskbarGeometry", $"Layout rejected for HWND=0x{taskbarHandle.ToInt64():X}; selected region={_placementRegion}; available horizontal space=unknown because a required control area was not recognized; task-list areas={taskLists.Length}; notification areas={notificationAreas.Length}; reason={failureReason}");
            return false;
        }

        var taskListBounds = Rectangle.FromLTRB(
            taskLists.Min(bounds => bounds.Left),
            taskLists.Min(bounds => bounds.Top),
            taskLists.Max(bounds => bounds.Right),
            taskLists.Max(bounds => bounds.Bottom));
        var notificationBounds = notificationAreas.Length == 0
            ? Rectangle.Empty
            : Rectangle.FromLTRB(
                notificationAreas.Min(bounds => bounds.Left),
                notificationAreas.Min(bounds => bounds.Top),
                notificationAreas.Max(bounds => bounds.Right),
                notificationAreas.Max(bounds => bounds.Bottom));

        if (_placementRegion != TaskbarPlacementRegion.FarLeft &&
            (taskListBounds.Right >= notificationBounds.Left ||
             Math.Min(taskListBounds.Bottom, notificationBounds.Bottom) <= Math.Max(taskListBounds.Top, notificationBounds.Top)))
        {
            failureReason = $"The centered task-list and notification areas leave no identifiable {_placementRegion} region for the dashboard.";
            var rawGapPx = notificationBounds.Left - taskListBounds.Right;
            var dpi = _host.DeviceDpi > 0 ? _host.DeviceDpi : 96;
            var rawGapDip = rawGapPx * 96d / dpi;
            DashboardDiagnostics.Write("TaskbarGeometry", $"Layout rejected for HWND=0x{taskbarHandle.ToInt64():X}; selected region={_placementRegion}; centered task-list bounds={FormatBounds(taskListBounds)}; notification-area bounds={FormatBounds(notificationBounds)}; available horizontal space before margins={rawGapPx} px / {rawGapDip:0.##} DIP; reason={failureReason}");
            return false;
        }

        Rectangle? widgetsBounds = null;
        string? widgetsLookupFailureReason = null;
        if (!_widgetsBoundsProvider.TryGetBounds(taskbarHandle, taskbarBounds, out var detectedWidgetsBounds, out widgetsLookupFailureReason))
        {
            DashboardDiagnostics.Write(
                "TaskbarGeometry",
                $"Widgets/weather discovery unavailable for Shell_TrayWnd HWND=0x{taskbarHandle.ToInt64():X}; selected region={_placementRegion}; reason={widgetsLookupFailureReason}; configured left fallback inset={_farLeftFallbackInsetDip} DIP.");
        }
        else
        {
            widgetsBounds = detectedWidgetsBounds;
        }

        var key = $"{taskbarHandle.ToInt64()}|{taskbarBounds.Left},{taskbarBounds.Top},{taskbarBounds.Width},{taskbarBounds.Height}|{taskListBounds.Left},{taskListBounds.Right}|{notificationBounds.Left},{notificationBounds.Right}|{FormatOptionalBounds(widgetsBounds)}|{widgetsLookupFailureReason}|{_placementRegion}";
        layout = new TaskbarLayout(taskbarHandle, taskbarBounds, taskListBounds, notificationBounds, widgetsBounds, widgetsLookupFailureReason, key);
        failureReason = string.Empty;
        return true;
    }

    private bool OnTaskbarChildEnumerated(IntPtr windowHandle, IntPtr lParam)
    {
        var className = new StringBuilder(128);
        var classLength = GetClassName(windowHandle, className, className.Capacity);
        if (classLength > 0 && TryGetWindowRect(windowHandle, out var bounds))
        {
            _children.Add(new TaskbarChild(windowHandle, className.ToString(), bounds));
        }

        return true;
    }

    private bool SetUnavailable(string failureReason, bool raiseAvailabilityEvent)
    {
        var wasAvailable = _isAvailable;
        var reasonChanged = LastFailureReason != failureReason;
        var shouldNotify = wasAvailable || (_placementStarting && reasonChanged);
        _isAvailable = false;
        _isFullscreenSuspended = false;
        _placementStarting = false;
        _lastPosition = null;
        LastFailureReason = failureReason;

        if ((wasAvailable || reasonChanged) && _host.IsHandleCreated && !_host.IsDisposed)
        {
            SetTopmost(false);
            _host.HideTaskbarAnchoredDashboard();
            var visible = IsWindowVisible(_host.Handle);
            var taskbarHandle = FindWindow("Shell_TrayWnd", null);
            var hasTaskbar = TryGetWindowRect(taskbarHandle, out _);
            var zOrder = InspectWindowZOrder(_host.Handle, taskbarHandle);
            var boundsRead = TryGetWindowRect(_host.Handle, out var bounds);
            var state = FormatWindowState(_host.Handle, taskbarHandle, boundsRead, bounds, visible, zOrder);
            DashboardDiagnostics.Write("TaskbarAnchored", $"Placement unavailable; {state}; taskbar bounds available={hasTaskbar}; reason={failureReason}");
            _lastLoggedZOrderState = FormatWindowStateKey(boundsRead, bounds, visible, zOrder, taskbarHandle);
            LogVisibilityTransition(TaskbarDashboardVisibilityState.Unavailable, failureReason, boundsRead ? bounds : null);
        }

        if (raiseAvailabilityEvent && shouldNotify)
        {
            AvailabilityChanged?.Invoke(false, failureReason);
        }

        return false;
    }

    private void SuspendForFullscreen()
    {
        if (_isFullscreenSuspended)
        {
            return;
        }

        if (_host.IsHandleCreated && !_host.IsDisposed)
        {
            SetTopmost(false);
            if (NativeMethods.SetWindowPos(_host.Handle, HwndNotTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoOwnerZOrder | SwpHideWindow | SwpNoZOrder))
            {
                _setWindowPosCount++;
            }
        }

        _isFullscreenSuspended = true;
        var visible = _host.IsHandleCreated && IsWindowVisible(_host.Handle);
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        var zOrder = _host.IsHandleCreated ? InspectWindowZOrder(_host.Handle, taskbarHandle) : default;
        var bounds = Rectangle.Empty;
        var boundsRead = _host.IsHandleCreated && TryGetWindowRect(_host.Handle, out bounds);
        var dashboardBounds = boundsRead ? bounds : Rectangle.Empty;
        var state = FormatWindowState(_host.Handle, taskbarHandle, boundsRead, dashboardBounds, visible, zOrder);
        DashboardDiagnostics.Write("TaskbarAnchored", $"Suspended dashboard for genuine fullscreen foreground; {state}");
        LogVisibilityTransition(
            TaskbarDashboardVisibilityState.FullscreenSuspended,
            _lastFullscreenDiagnostic ?? "fullscreen foreground covers the primary display",
            boundsRead ? dashboardBounds : null);
    }

    private void SetTopmost(bool topmost)
    {
        if (!_host.IsHandleCreated || _host.IsDisposed)
        {
            return;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(_host.Handle, GwlExStyle).ToInt64();
        var currentlyTopmost = (extendedStyle & WsExTopmost) != 0;
        if (_isTopmost == topmost && currentlyTopmost == topmost)
        {
            return;
        }

        if (topmost)
        {
            extendedStyle |= WsExTopmost;
        }
        else
        {
            extendedStyle &= ~WsExTopmost;
        }

        NativeMethods.SetWindowLongPtr(_host.Handle, GwlExStyle, new IntPtr(extendedStyle));
        if (!topmost)
        {
            if (NativeMethods.SetWindowPos(
                _host.Handle,
                HwndNotTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder))
            {
                _setWindowPosCount++;
                DashboardDiagnostics.Write("Positioning", "Removed dashboard from the topmost band without moving or resizing it.");
            }
        }

        _isTopmost = topmost;
    }

    private bool IsUnrelatedFullscreenWindowForeground(Rectangle primaryBounds)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            LogForegroundClassification(IntPtr.Zero, "unknown", "unknown", Rectangle.Empty, primaryBounds, false, false, false, false);
            return false;
        }

        var taskbarWindow = FindWindow("Shell_TrayWnd", null);
        var foregroundClass = new StringBuilder(128);
        var classNameRead = GetClassName(foregroundWindow, foregroundClass, foregroundClass.Capacity) > 0;
        var className = classNameRead ? foregroundClass.ToString() : "unknown";
        var isShellWindow = className is "Progman" or "WorkerW";
        var isTaskbarWindow = foregroundWindow == taskbarWindow;
        var boundsRead = TryGetWindowRect(foregroundWindow, out var foregroundBounds);
        var isMaximized = IsZoomed(foregroundWindow);
        var style = NativeMethods.GetWindowLongPtr(foregroundWindow, GwlStyle).ToInt64();
        var hasCaption = (style & WsCaption) == WsCaption;
        var coversDisplay = boundsRead && foregroundBounds.Left <= primaryBounds.Left + 1 &&
            foregroundBounds.Top <= primaryBounds.Top + 1 &&
            foregroundBounds.Right >= primaryBounds.Right - 1 &&
            foregroundBounds.Bottom >= primaryBounds.Bottom - 1;
        var isFullscreen = TaskbarFullscreenClassifier.IsGenuineFullscreen(
            boundsRead ? foregroundBounds : Rectangle.Empty,
            primaryBounds,
            isMaximized,
            hasCaption,
            isShellWindow,
            isTaskbarWindow);
        LogForegroundClassification(
            foregroundWindow,
            GetForegroundProcessName(foregroundWindow),
            className,
            boundsRead ? foregroundBounds : Rectangle.Empty,
            primaryBounds,
            isMaximized,
            hasCaption,
            coversDisplay,
            isFullscreen);
        return isFullscreen;
    }

    private void LogForegroundClassification(
        IntPtr foregroundWindow,
        string processName,
        string className,
        Rectangle foregroundBounds,
        Rectangle displayBounds,
        bool isMaximized,
        bool hasCaption,
        bool coversDisplay,
        bool isFullscreen)
    {
        var shouldLog = _lastLoggedFullscreenClassification is null ||
            _lastLoggedFullscreenClassification != isFullscreen ||
            _lastLoggedForegroundCoversDisplay != coversDisplay ||
            (coversDisplay && _lastLoggedForegroundWindowHandle != foregroundWindow);
        if (shouldLog)
        {
            _lastFullscreenDiagnostic =
                $"foreground HWND=0x{foregroundWindow.ToInt64():X}; process={processName}; class={className}; bounds={FormatBounds(foregroundBounds)}; display bounds={FormatBounds(displayBounds)}; maximized={isMaximized}; caption style={hasCaption}; covers display={coversDisplay}; classification={(isFullscreen ? "genuine fullscreen, hide dashboard" : "not fullscreen, keep dashboard visible")}";
            DashboardDiagnostics.Write("Fullscreen", _lastFullscreenDiagnostic);
            _lastLoggedForegroundWindowHandle = foregroundWindow;
            _lastLoggedForegroundCoversDisplay = coversDisplay;
            _lastLoggedFullscreenClassification = isFullscreen;
        }
    }

    private static string GetForegroundProcessName(IntPtr foregroundWindow)
    {
        GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0)
        {
            return "unknown";
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "exited";
        }
    }

    private void LogVisibilityTransition(TaskbarDashboardVisibilityState state, string reason, Rectangle? bounds)
    {
        if (!_visibilityTransitions.MoveTo(state))
        {
            return;
        }

        DashboardDiagnostics.Write(
            "Visibility",
            $"Dashboard visibility state changed to {state}; reason={reason}; bounds={(bounds.HasValue ? FormatBounds(bounds.Value) : "unknown")}; fullscreen classification={(_lastLoggedFullscreenClassification == true ? "genuine fullscreen" : "not fullscreen")}; TaskbarAnchored available={_isAvailable}; fullscreen suspended={_isFullscreenSuspended}.");
    }

    private void WriteStabilitySummaryIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _lastStabilitySummaryAtUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        DashboardDiagnostics.Write(
            "Stability",
            $"Last five minutes: geometry refreshes={_geometryRefreshCount}; SetWindowPos calls={_setWindowPosCount}; redundant position operations skipped={_redundantPositionOperationsSkipped}; visibility state={_visibilityTransitions.Current?.ToString() ?? "unknown"}; last verified bounds={(_lastPosition.HasValue ? FormatBounds(_lastPosition.Value) : "unknown")}.");
        _geometryRefreshCount = 0;
        _setWindowPosCount = 0;
        _redundantPositionOperationsSkipped = 0;
        _lastStabilitySummaryAtUtc = now;
    }

    private static bool TryGetWindowRect(IntPtr windowHandle, out Rectangle bounds)
    {
        if (windowHandle != IntPtr.Zero && GetWindowRect(windowHandle, out var nativeBounds))
        {
            bounds = Rectangle.FromLTRB(nativeBounds.Left, nativeBounds.Top, nativeBounds.Right, nativeBounds.Bottom);
            return true;
        }

        bounds = Rectangle.Empty;
        return false;
    }

    private static WindowZOrder InspectWindowZOrder(IntPtr dashboardHandle, IntPtr taskbarHandle)
    {
        int? dashboardIndex = null;
        int? taskbarIndex = null;
        var seen = new HashSet<IntPtr>();
        var current = GetTopWindow(IntPtr.Zero);
        var index = 0;
        while (current != IntPtr.Zero && seen.Add(current) && index < 20_000)
        {
            if (current == dashboardHandle)
            {
                dashboardIndex = index;
            }

            if (current == taskbarHandle)
            {
                taskbarIndex = index;
            }

            current = GetWindow(current, GwHwndNext);
            index++;
        }

        var dashboardExStyle = dashboardHandle == IntPtr.Zero ? 0 : NativeMethods.GetWindowLongPtr(dashboardHandle, GwlExStyle).ToInt64();
        var taskbarExStyle = taskbarHandle == IntPtr.Zero ? 0 : NativeMethods.GetWindowLongPtr(taskbarHandle, GwlExStyle).ToInt64();
        bool? isAboveTaskbar = dashboardIndex.HasValue && taskbarIndex.HasValue
            ? dashboardIndex.Value < taskbarIndex.Value
            : null;

        return new WindowZOrder(
            dashboardIndex,
            taskbarIndex,
            isAboveTaskbar,
            (dashboardExStyle & WsExTopmost) != 0,
            (taskbarExStyle & WsExTopmost) != 0);
    }

    private static string FormatWindowState(
        IntPtr dashboardHandle,
        IntPtr taskbarHandle,
        bool boundsRead,
        Rectangle bounds,
        bool isVisible,
        WindowZOrder zOrder)
    {
        var formattedBounds = boundsRead ? FormatBounds(bounds) : "unavailable";
        var relation = zOrder.IsAboveTaskbar switch
        {
            true => "dashboard is above taskbar",
            false => "dashboard is behind taskbar",
            _ => "dashboard/taskbar Z-order unknown"
        };

        return $"dashboard HWND=0x{dashboardHandle.ToInt64():X}; bounds={formattedBounds}; visible={isVisible}; topmost={zOrder.DashboardTopmost}; Z-order index={FormatIndex(zOrder.DashboardIndex)}; taskbar HWND=0x{taskbarHandle.ToInt64():X}; taskbar visible={(taskbarHandle != IntPtr.Zero && IsWindowVisible(taskbarHandle))}; taskbar topmost={zOrder.TaskbarTopmost}; taskbar Z-order index={FormatIndex(zOrder.TaskbarIndex)}; relation={relation}";
    }

    private static string FormatWindowStateKey(
        bool boundsRead,
        Rectangle bounds,
        bool isVisible,
        WindowZOrder zOrder,
        IntPtr taskbarHandle)
    {
        return $"taskbar=0x{taskbarHandle.ToInt64():X}|bounds={(boundsRead ? FormatBounds(bounds) : "unavailable")}|visible={isVisible}|aboveTaskbar={zOrder.IsAboveTaskbar}|dashboardTopmost={zOrder.DashboardTopmost}|taskbarTopmost={zOrder.TaskbarTopmost}";
    }

    private static string FormatIndex(int? index) => index?.ToString() ?? "unknown";

    internal static Rectangle CalculateDashboardBounds(
        Rectangle safeArea,
        int dashboardWidth,
        TaskbarHorizontalAlignment horizontalAlignment,
        int additionalLeftOffsetPixels = 0)
    {
        var left = horizontalAlignment switch
        {
            TaskbarHorizontalAlignment.Center => safeArea.Left + (safeArea.Width - dashboardWidth) / 2,
            TaskbarHorizontalAlignment.Right => safeArea.Right - dashboardWidth,
            _ => safeArea.Left + additionalLeftOffsetPixels
        };

        return new Rectangle(left, safeArea.Top, dashboardWidth, safeArea.Height);
    }

    private static string FormatBounds(Rectangle bounds)
    {
        return $"({bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}) {bounds.Width}x{bounds.Height}";
    }

    private static string FormatOptionalBounds(Rectangle? bounds) =>
        bounds.HasValue ? FormatBounds(bounds.Value) : "unavailable";

    private static bool IsRecoverableNativeFailure(Exception exception)
    {
        return exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
            InvalidOperationException or ExternalException;
    }

    private static uint RegisterWindowMessage(string message)
    {
        return RegisterWindowMessageNative(message);
    }

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "EnumChildWindows", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parentWindowHandle, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect bounds);

    [DllImport("user32.dll", EntryPoint = "GetWindow", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

    [DllImport("user32.dll", EntryPoint = "GetTopWindow", SetLastError = true)]
    private static extern IntPtr GetTopWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "IsZoomed")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint RegisterWindowMessageNative(string message);

    private delegate bool EnumChildProc(IntPtr windowHandle, IntPtr lParam);

    private readonly record struct TaskbarChild(IntPtr WindowHandle, string ClassName, Rectangle Bounds);

    private readonly record struct TaskbarLayout(
        IntPtr TaskbarHandle,
        Rectangle TaskbarBounds,
        Rectangle TaskListBounds,
        Rectangle NotificationBounds,
        Rectangle? WidgetsBounds,
        string? WidgetsLookupFailureReason,
        string Key);

    private readonly record struct WindowZOrder(
        int? DashboardIndex,
        int? TaskbarIndex,
        bool? IsAboveTaskbar,
        bool DashboardTopmost,
        bool TaskbarTopmost);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
