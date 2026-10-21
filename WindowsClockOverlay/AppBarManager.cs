using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsClockOverlay;

internal sealed class AppBarManager : IDisposable
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbmGetTaskbarPos = 0x00000005;
    private const uint AbmActivate = 0x00000006;
    private const uint AbmWindowPosChanged = 0x00000009;

    private const uint AbnPosChanged = 0x00000001;
    private const uint AbeBottom = 3;
    private const int DefaultHeightDip = 36;
    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmDpiChanged = 0x02E0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private static readonly uint CallbackMessage = RegisterWindowMessage("WindowsClockOverlay.AppBarCallback");
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Form _host;
    private readonly System.Windows.Forms.Timer _taskbarRestartRetryTimer;
    private bool _isRegistered;
    private bool _shouldBeRegistered;
    private bool _isPositioning;
    private bool _repositionQueued;
    private bool _isDisposed;
    private Rectangle? _lastPosition;
    private int _taskbarRestartRetriesRemaining;

    public AppBarManager(Form host)
    {
        _host = host;
        _taskbarRestartRetryTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _taskbarRestartRetryTimer.Tick += OnTaskbarRestartRetryTick;
    }

    public event Action<bool, string?>? AvailabilityChanged;

    public bool IsRegistered => _isRegistered;

    public string? LastFailureReason { get; private set; }

    public bool Start()
    {
        _shouldBeRegistered = true;
        DashboardDiagnostics.Write("AppBar", "AppBar registration requested.");
        _taskbarRestartRetriesRemaining = 0;
        if (!_isDisposed)
        {
            _taskbarRestartRetryTimer.Stop();
        }

        LastFailureReason = null;
        return TryRegister();
    }

    public void Stop()
    {
        if (_isRegistered || _shouldBeRegistered)
        {
            DashboardDiagnostics.Write("AppBar", $"Stopping AppBar; registered={_isRegistered}. The reserved desktop area will be released.");
        }

        _shouldBeRegistered = false;
        _taskbarRestartRetriesRemaining = 0;
        if (!_isDisposed)
        {
            _taskbarRestartRetryTimer.Stop();
        }

        UnregisterFromShell();
    }

    public bool ProcessWindowMessage(ref Message message)
    {
        if (_shouldBeRegistered && TaskbarCreatedMessage != 0 && (uint)message.Msg == TaskbarCreatedMessage)
        {
            OnTaskbarCreated();
            return true;
        }

        if (CallbackMessage != 0 && (uint)message.Msg == CallbackMessage)
        {
            if (unchecked((uint)message.WParam.ToInt64()) == AbnPosChanged)
            {
                QueueReposition();
            }

            return true;
        }

        return false;
    }

    public void OnWindowMessageProcessed(int messageId)
    {
        if (!_isRegistered)
        {
            return;
        }

        switch (messageId)
        {
            case WmActivate:
                SendAppBarMessage(AbmActivate);
                break;
            case WmWindowPosChanged:
                SendAppBarMessage(AbmWindowPosChanged);
                break;
            case WmDpiChanged:
                QueueReposition();
                break;
        }
    }

    public void NotifyDisplayChanged()
    {
        QueueReposition();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Stop();
        _isDisposed = true;
        _taskbarRestartRetryTimer.Stop();
        _taskbarRestartRetryTimer.Tick -= OnTaskbarRestartRetryTick;
        _taskbarRestartRetryTimer.Dispose();
        AvailabilityChanged = null;
    }

    private bool TryRegister()
    {
        if (_isDisposed || !_shouldBeRegistered || !_host.IsHandleCreated)
        {
            return false;
        }

        if (_isRegistered)
        {
            return true;
        }

        if (CallbackMessage == 0 || TaskbarCreatedMessage == 0)
        {
            return Fail("Windows could not register the AppBar notification messages.");
        }

        try
        {
            if (!TryGetSupportedMonitor(out var monitorBounds, out var failureReason))
            {
                return Fail(failureReason);
            }

            var appBarData = CreateAppBarData();
            appBarData.CallbackMessage = CallbackMessage;

            if (SendAppBarMessage(AbmNew, ref appBarData) == 0)
            {
                return Fail("Windows Shell rejected AppBar registration.");
            }

            _isRegistered = true;
            _lastPosition = null;

            if (!Position(monitorBounds))
            {
                UnregisterFromShell();
                return Fail("Windows Shell could not position the AppBar.");
            }

            LastFailureReason = null;
            _taskbarRestartRetriesRemaining = 0;
            _taskbarRestartRetryTimer.Stop();
            DashboardDiagnostics.Write("AppBar", $"AppBar registered and positioned for monitor bounds ({monitorBounds.Left},{monitorBounds.Top},{monitorBounds.Right},{monitorBounds.Bottom}).");
            return true;
        }
        catch (Exception exception) when (IsRecoverableNativeFailure(exception))
        {
            Trace.TraceWarning("AppBar registration failed: {0}", exception);
            UnregisterFromShell();
            return Fail("AppBar registration failed. The clock overlay is being used instead.");
        }
    }

    private bool TryGetSupportedMonitor(out Rectangle monitorBounds, out string failureReason)
    {
        monitorBounds = Rectangle.Empty;

        var primaryScreen = Screen.PrimaryScreen;
        if (primaryScreen is null || primaryScreen.Bounds.Width <= 0 || primaryScreen.Bounds.Height <= 0)
        {
            failureReason = "The primary display is unavailable.";
            return false;
        }

        var taskbar = CreateAppBarData();
        if (SendAppBarMessage(AbmGetTaskbarPos, ref taskbar) == 0)
        {
            failureReason = "Windows Shell could not report the taskbar position.";
            return false;
        }

        monitorBounds = primaryScreen.Bounds;
        var taskbarBounds = taskbar.Bounds.ToRectangle();
        var isBottomEdge = Math.Abs(taskbarBounds.Bottom - monitorBounds.Bottom) <= 2;
        var spansPrimaryWidth = taskbarBounds.Left <= monitorBounds.Left + 2 &&
            taskbarBounds.Right >= monitorBounds.Right - 2;
        if (taskbarBounds.Width <= 0 || taskbarBounds.Height <= 0 ||
            !isBottomEdge || !spansPrimaryWidth || taskbarBounds.Top < monitorBounds.Top ||
            taskbarBounds.Top >= monitorBounds.Bottom)
        {
            failureReason = "AppBar mode currently supports a bottom taskbar on the primary display only.";
            monitorBounds = Rectangle.Empty;
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private bool Position(Rectangle monitorBounds)
    {
        if (!_isRegistered || _isPositioning || monitorBounds.Width <= 0 || monitorBounds.Height <= 0)
        {
            return false;
        }

        _isPositioning = true;
        try
        {
            if (!TryGetWindowBounds(_host.Handle, out var windowBounds) ||
                !monitorBounds.Contains(windowBounds.Left + windowBounds.Width / 2, windowBounds.Top + windowBounds.Height / 2))
            {
                if (!SetWindowPos(
                        _host.Handle,
                        IntPtr.Zero,
                        monitorBounds.Left,
                        monitorBounds.Top,
                        0,
                        0,
                        SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder))
                {
                    return false;
                }
            }

            var appBarData = CreateAppBarData();
            appBarData.Edge = AbeBottom;
            appBarData.Bounds = NativeRect.FromRectangle(monitorBounds);

            // The shell returns the usable edge after accounting for the taskbar and
            // other appbars. Keep the full monitor width, then request 36 DIPs at
            // that approved edge.
            SendAppBarMessage(AbmQueryPos, ref appBarData);

            var dpi = GetDpiForWindow(_host.Handle);
            if (dpi == 0)
            {
                dpi = 96;
            }

            var height = Math.Max(1, (DefaultHeightDip * (int)dpi + 48) / 96);
            height = Math.Min(height, monitorBounds.Height);

            var availableBottom = appBarData.Bounds.Bottom;
            var proposedBounds = new Rectangle(
                monitorBounds.Left,
                availableBottom - height,
                monitorBounds.Width,
                height);

            if (_lastPosition == proposedBounds && TryGetWindowBounds(_host.Handle, out var currentBounds) && currentBounds == proposedBounds)
            {
                if (_host is ClockOverlayForm unchangedClockForm)
                {
                    unchangedClockForm.OnAppBarPositioned();
                }

                return true;
            }

            appBarData.Bounds = NativeRect.FromRectangle(proposedBounds);

            SendAppBarMessage(AbmSetPos, ref appBarData);
            var approvedBounds = appBarData.Bounds.ToRectangle();
            if (approvedBounds.Width <= 0 || approvedBounds.Height <= 0)
            {
                return false;
            }

            if (!SetWindowPos(
                    _host.Handle,
                    HwndTopmost,
                    approvedBounds.Left,
                    approvedBounds.Top,
                    approvedBounds.Width,
                    approvedBounds.Height,
                    SwpNoActivate | SwpNoOwnerZOrder))
            {
                return false;
            }

            _lastPosition = approvedBounds;
            if (_host is ClockOverlayForm clockForm)
            {
                clockForm.OnAppBarPositioned();
            }

            return true;
        }
        finally
        {
            _isPositioning = false;
        }
    }

    private void OnTaskbarCreated()
    {
        if (_isDisposed || !_shouldBeRegistered)
        {
            return;
        }

        // TaskbarCreated is also sent for primary-display DPI changes. Remove
        // any surviving registration first so either case can register cleanly.
        UnregisterFromShell();
        _taskbarRestartRetryTimer.Stop();
        _taskbarRestartRetriesRemaining = 3;
        var registered = TryRegister();
        AvailabilityChanged?.Invoke(registered, registered ? null : LastFailureReason);
        if (!registered && _shouldBeRegistered)
        {
            _taskbarRestartRetryTimer.Start();
        }
    }

    private void OnTaskbarRestartRetryTick(object? sender, EventArgs e)
    {
        _taskbarRestartRetryTimer.Stop();
        if (_isDisposed || !_shouldBeRegistered || _isRegistered || _taskbarRestartRetriesRemaining <= 0)
        {
            return;
        }

        _taskbarRestartRetriesRemaining--;
        var registered = TryRegister();
        AvailabilityChanged?.Invoke(registered, registered ? null : LastFailureReason);
        if (!registered && _taskbarRestartRetriesRemaining > 0 && _shouldBeRegistered)
        {
            _taskbarRestartRetryTimer.Start();
        }
    }

    private void QueueReposition()
    {
        if (_isDisposed || !_shouldBeRegistered || !_host.IsHandleCreated || _repositionQueued)
        {
            return;
        }

        _repositionQueued = true;
        try
        {
            _host.BeginInvoke((MethodInvoker)(() =>
            {
                _repositionQueued = false;
                if (_isDisposed || !_shouldBeRegistered || _host.IsDisposed)
                {
                    return;
                }

                if (!_isRegistered)
                {
                    var registered = TryRegister();
                    AvailabilityChanged?.Invoke(registered, registered ? null : LastFailureReason);
                    return;
                }

                try
                {
                    if (!TryGetSupportedMonitor(out var monitorBounds, out var failureReason))
                    {
                        UnregisterFromShell();
                        Fail(failureReason);
                        AvailabilityChanged?.Invoke(false, LastFailureReason);
                        return;
                    }

                    if (!Position(monitorBounds))
                    {
                        UnregisterFromShell();
                        Fail("Windows Shell could not reposition the AppBar.");
                        AvailabilityChanged?.Invoke(false, LastFailureReason);
                    }
                }
                catch (Exception exception) when (IsRecoverableNativeFailure(exception))
                {
                    Trace.TraceWarning("AppBar repositioning failed: {0}", exception);
                    UnregisterFromShell();
                    Fail("AppBar repositioning failed. The clock overlay is being used instead.");
                    AvailabilityChanged?.Invoke(false, LastFailureReason);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            _repositionQueued = false;
        }
    }

    private bool Fail(string reason)
    {
        LastFailureReason = reason;
        DashboardDiagnostics.Write("AppBar", $"Registration/positioning failed; reason={reason}");
        return false;
    }

    private void UnregisterFromShell()
    {
        if (!_isRegistered)
        {
            _lastPosition = null;
            return;
        }

        try
        {
            var appBarData = CreateAppBarData();
            SendAppBarMessage(AbmRemove, ref appBarData);
            DashboardDiagnostics.Write("AppBar", "Sent ABM_REMOVE to Windows Shell; reserved desktop area released.");
        }
        catch (Exception exception) when (IsRecoverableNativeFailure(exception))
        {
            Trace.TraceWarning("AppBar cleanup failed: {0}", exception);
            DashboardDiagnostics.Write("AppBar", $"ABM_REMOVE cleanup failed: {exception.GetType().Name}: {exception.Message}");
        }

        _isRegistered = false;
        _lastPosition = null;
    }

    private void SendAppBarMessage(uint message)
    {
        var appBarData = CreateAppBarData();
        SendAppBarMessage(message, ref appBarData);
    }

    private AppBarData CreateAppBarData()
    {
        return new AppBarData
        {
            Size = (uint)Marshal.SizeOf<AppBarData>(),
            WindowHandle = _host.Handle
        };
    }

    private static UIntPtr SendAppBarMessage(uint message, ref AppBarData appBarData)
    {
        return SHAppBarMessage(message, ref appBarData);
    }

    private static bool TryGetWindowBounds(IntPtr windowHandle, out Rectangle bounds)
    {
        if (GetWindowRect(windowHandle, out var nativeBounds))
        {
            bounds = nativeBounds.ToRectangle();
            return true;
        }

        bounds = Rectangle.Empty;
        return false;
    }

    private static bool IsRecoverableNativeFailure(Exception exception)
    {
        return exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or
            InvalidOperationException or ExternalException;
    }

    [DllImport("shell32.dll", EntryPoint = "SHAppBarMessage", SetLastError = true)]
    private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData appBarData);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Bounds;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public static NativeRect FromRectangle(Rectangle rectangle)
        {
            return new NativeRect
            {
                Left = rectangle.Left,
                Top = rectangle.Top,
                Right = rectangle.Right,
                Bottom = rectangle.Bottom
            };
        }

        public readonly Rectangle ToRectangle()
        {
            return Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }
    }
}
