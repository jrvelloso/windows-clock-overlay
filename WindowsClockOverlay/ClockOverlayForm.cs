using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;

namespace WindowsClockOverlay;

public sealed class ClockOverlayForm : Form
{
    private readonly Label _clockLabel;
    private readonly ClockWidget _clockWidget;
    private readonly BluetoothBatteryWidget _batteryWidget;
    private readonly Panel _dashboardPanel;
    private readonly DashboardSeparator _dashboardSeparator;
    private readonly DashboardStatusIndicator _layoutStatusIndicator;
    private readonly System.Windows.Forms.Timer _clockTimer;
    private readonly NotifyIcon _trayIcon;
    private readonly OverlaySettingsStore _settingsStore;
    private readonly AppBarManager _appBarManager;
    private readonly TaskbarAnchoredManager _taskbarAnchoredManager;
    private readonly BluetoothBatteryService _batteryService;
    private readonly Dictionary<int, ToolStripMenuItem> _colorMenuItems = [];
    private readonly Dictionary<ClockDisplayMode, ToolStripMenuItem> _modeMenuItems = [];
    private ToolStripMenuItem _requestedModeStatusItem = null!;
    private ToolStripMenuItem _effectiveModeStatusItem = null!;
    private ToolStripMenuItem _positioningErrorStatusItem = null!;
    private ToolStripMenuItem _dashboardSettingsStatusItem = null!;
    private ToolStripMenuItem _widgetSettingsMenu = null!;
    private ToolStripMenuItem _resetPositionMenuItem = null!;
    private readonly Icon _trayAppIcon;

    private ClockDisplayMode _preferredDisplayMode = ClockDisplayMode.TaskbarAnchored;
    private ClockDisplayMode _activeDisplayMode = ClockDisplayMode.TaskbarAnchored;
    private bool _useDefaultPosition = true;
    private bool _isDragging;
    private bool _didInitialPlacement;
    private bool _hasShownAppBarFallbackNotice;
    private bool _hasShownTaskbarUnavailableNotice;
    private string? _lastLoggedModeState;
    private Point _savedLocation;
    private Point _dragStartCursor;
    private Point _dragStartWindow;
    private float _fontSize = DefaultFontSize;
    private int _taskbarDashboardWidthDip = 180;
    private int _taskbarHorizontalOffsetDip;
    private int _taskbarSafeAreaMarginDip = 8;
    private int _taskbarFarLeftFallbackInsetDip = 180;
    private TaskbarHorizontalAlignment _taskbarHorizontalAlignment = TaskbarHorizontalAlignment.Left;
    private TaskbarPlacementRegion _taskbarPlacementRegion = TaskbarPlacementRegion.FarLeft;
    private OverlaySettings _settings = new();
    private DashboardLayoutResult _taskbarLayoutResult = new(Array.Empty<string>(), 0, 0, false);
    private string? _lastLoggedTaskbarLayoutKey;

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpNoownerzorder = 0x0200;
    private const uint SwpFramechanged = 0x0020;
    private const int RightMargin = 150;
    private const int TopMargin = 0;
    private const int WmMouseWheel = 0x020A;
    private const int WmDpiChanged = 0x02E0;
    private const int MkControl = 0x0008;
    private const float DefaultFontSize = 22f;
    private const float MinimumFontSize = 10f;
    private const float MaximumFontSize = 96f;
    private const float FontSizeStep = 1f;
    private const string EmbeddedIconResourceName = "WindowsClockOverlay.clock.ico";
    private const float TrayIconScale = 1.35f;

    private static readonly (string Name, string Hex, Color Color)[] Palette =
    [
        ("Soft White", "#F2F2F2", Color.FromArgb(0xF2, 0xF2, 0xF2)),
        ("Ice Blue", "#DDF4FF", Color.FromArgb(0xDD, 0xF4, 0xFF)),
        ("Mint", "#D9FFE8", Color.FromArgb(0xD9, 0xFF, 0xE8)),
        ("Lemon", "#FFF6B8", Color.FromArgb(0xFF, 0xF6, 0xB8)),
        ("Peach", "#FFDAB8", Color.FromArgb(0xFF, 0xDA, 0xB8)),
        ("Sky", "#87CEFF", Color.FromArgb(0x87, 0xCE, 0xFF)),
        ("Lime", "#7CFC00", Color.FromArgb(0x7C, 0xFC, 0x00)),
        ("Amber", "#FFC107", Color.FromArgb(0xFF, 0xC1, 0x07)),
        ("Orange", "#FF8C00", Color.FromArgb(0xFF, 0x8C, 0x00)),
        ("Red", "#FF3B30", Color.FromArgb(0xFF, 0x3B, 0x30)),
        ("Magenta", "#FF00AA", Color.FromArgb(0xFF, 0x00, 0xAA)),
        ("Neon Green", "#00FF00", Color.FromArgb(0x00, 0xFF, 0x00))
    ];

    public ClockOverlayForm()
    {
        _settingsStore = new OverlaySettingsStore();
        _appBarManager = new AppBarManager(this);
        _appBarManager.AvailabilityChanged += OnAppBarAvailabilityChanged;
        _taskbarAnchoredManager = new TaskbarAnchoredManager(this);
        _taskbarAnchoredManager.AvailabilityChanged += OnTaskbarAnchoredAvailabilityChanged;
        _taskbarAnchoredManager.PlacementStarting += OnTaskbarAnchoredPlacementStarting;
        _trayAppIcon = LoadTrayIcon();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = "Clock Overlay";

        _clockLabel = new DashboardClockLabel
        {
            AutoSize = true,
            Font = new Font("Consolas", DefaultFontSize, FontStyle.Bold),
            ForeColor = Color.Lime,
            BackColor = Color.Transparent,
            Text = DateTime.Now.ToString("HH:mm:ss")
        };
        _clockWidget = new ClockWidget(_clockLabel);
        _clockWidget.SetPreferredFontSize(_fontSize);
        _batteryWidget = new BluetoothBatteryWidget();
        _dashboardSeparator = new DashboardSeparator();
        _dashboardPanel = new Panel
        {
            BackColor = Color.Magenta,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Visible = false
        };
        _layoutStatusIndicator = new DashboardStatusIndicator
        {
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point),
            Visible = false,
            TabStop = false
        };

        Controls.Add(_clockLabel);
        Controls.Add(_dashboardPanel);
        WireMoveHandlers(this);
        WireMoveHandlers(_clockLabel);

        _batteryService = new BluetoothBatteryService(
            new WindowsBluetoothDeviceSource(),
            () => _settings.BatteryRefreshIntervalSeconds);
        _batteryService.SnapshotChanged += OnBluetoothSnapshotChanged;

        _clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _clockTimer.Tick += (_, _) => UpdateClockAndLayout();
        _clockTimer.Start();

        var trayMenu = BuildTrayMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = _trayAppIcon,
            Text = "Windows Clock Dashboard",
            Visible = true,
            ContextMenuStrip = trayMenu
        };

        LoadSettings();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        UpdateClockAndLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyExtendedWindowStyles();
        _batteryService.Start();

        if (_preferredDisplayMode == ClockDisplayMode.AppBar)
        {
            if (!_appBarManager.Start())
            {
                ActivateOverlayFallback(_appBarManager.LastFailureReason, showNotice: true);
            }
        }
        else if (_preferredDisplayMode == ClockDisplayMode.TaskbarAnchored)
        {
            if (!_taskbarAnchoredManager.Start(
                    _taskbarHorizontalOffsetDip,
                    _taskbarSafeAreaMarginDip,
                    _taskbarHorizontalAlignment,
                    _taskbarFarLeftFallbackInsetDip,
                    _taskbarPlacementRegion))
            {
                HandleTaskbarAnchoredUnavailable(_taskbarAnchoredManager.LastFailureReason, showNotice: true);
            }
        }
        else
        {
            EnsureAlwaysOnTop();
        }

        UpdateClockAndLayout();
        UpdateModeMenu();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && _activeDisplayMode == ClockDisplayMode.TaskbarAnchored &&
            !_taskbarAnchoredManager.IsAvailable && !IsHandleCreated && !IsDisposed)
        {
            // The anchored manager starts from OnHandleCreated, so create the handle before
            // deciding whether the window may be shown. Returning early here would prevent
            // OnHandleCreated and permanently bypass the positioning attempt.
            CreateHandle();
        }

        if (value && _activeDisplayMode == ClockDisplayMode.TaskbarAnchored && !_taskbarAnchoredManager.IsAvailable)
        {
            base.SetVisibleCore(false);
            return;
        }

        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        DashboardDiagnostics.Write("Shutdown", $"Closing; requested display mode={_preferredDisplayMode}; effective display mode={GetEffectiveDisplayModeText()}; AppBar registered={_appBarManager.IsRegistered}.");
        SaveSettings();
        _appBarManager.AvailabilityChanged -= OnAppBarAvailabilityChanged;
        _taskbarAnchoredManager.AvailabilityChanged -= OnTaskbarAnchoredAvailabilityChanged;
        _taskbarAnchoredManager.PlacementStarting -= OnTaskbarAnchoredPlacementStarting;
        _appBarManager.Dispose();
        _taskbarAnchoredManager.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _clockTimer.Stop();
        _clockTimer.Dispose();
        _batteryService.SnapshotChanged -= OnBluetoothSnapshotChanged;
        _batteryService.Stop();
        _batteryService.Dispose();
        _batteryWidget.Dispose();
        _dashboardSeparator.Dispose();
        _layoutStatusIndicator.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _trayAppIcon.Dispose();
        base.OnFormClosing(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _appBarManager.Stop();
        _taskbarAnchoredManager.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (_activeDisplayMode == ClockDisplayMode.Overlay && m.Msg == WmMouseWheel && IsControlWheel(m.WParam))
        {
            Zoom(Math.Sign(GetWheelDelta(m.WParam)));
            return;
        }

        var appBarMessageHandled = _appBarManager.ProcessWindowMessage(ref m);
        var taskbarMessageHandled = _taskbarAnchoredManager.ProcessWindowMessage(ref m);
        if (appBarMessageHandled || taskbarMessageHandled)
        {
            return;
        }

        var messageId = m.Msg;
        base.WndProc(ref m);
        if (messageId == WmDpiChanged)
        {
            RefreshClockFontForCurrentDpi();
        }

        _appBarManager.OnWindowMessageProcessed(messageId);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            createParams.ExStyle |= WsExToolWindow | WsExNoActivate;
            return createParams;
        }
    }

    protected override bool ShowWithoutActivation => true;

    private ContextMenuStrip BuildTrayMenu()
    {
        var trayMenu = new ContextMenuStrip();
        trayMenu.Opening += (_, _) =>
        {
            UpdateModeMenu();
            UpdateWidgetMenu();
        };

        var modeMenu = new ToolStripMenuItem("Display Mode");
        _modeMenuItems[ClockDisplayMode.AppBar] = new ToolStripMenuItem("Taskbar AppBar", null, (_, _) => SetDisplayMode(ClockDisplayMode.AppBar));
        _modeMenuItems[ClockDisplayMode.Overlay] = new ToolStripMenuItem("Legacy Clock Overlay", null, (_, _) => SetDisplayMode(ClockDisplayMode.Overlay));
        _modeMenuItems[ClockDisplayMode.TaskbarAnchored] = new ToolStripMenuItem("Taskbar Anchored", null, (_, _) => SetDisplayMode(ClockDisplayMode.TaskbarAnchored));
        modeMenu.DropDownItems.Add(_modeMenuItems[ClockDisplayMode.AppBar]);
        modeMenu.DropDownItems.Add(_modeMenuItems[ClockDisplayMode.TaskbarAnchored]);
        modeMenu.DropDownItems.Add(_modeMenuItems[ClockDisplayMode.Overlay]);
        trayMenu.Items.Add(modeMenu);
        _requestedModeStatusItem = new ToolStripMenuItem { Enabled = false };
        _effectiveModeStatusItem = new ToolStripMenuItem { Enabled = false };
        trayMenu.Items.Add(_requestedModeStatusItem);
        trayMenu.Items.Add(_effectiveModeStatusItem);
        _positioningErrorStatusItem = new ToolStripMenuItem { Enabled = false };
        trayMenu.Items.Add(_positioningErrorStatusItem);
        trayMenu.Items.Add(new ToolStripSeparator());

        trayMenu.Items.Add("Taskbar Anchored Layout...", null, (_, _) => ConfigureTaskbarAnchoredLayout());
        _dashboardSettingsStatusItem = new ToolStripMenuItem("Dashboard and Bluetooth Devices...", null, (_, _) => ConfigureDashboardSettings());
        trayMenu.Items.Add(_dashboardSettingsStatusItem);
        _widgetSettingsMenu = new ToolStripMenuItem("Widgets");
        _widgetSettingsMenu.DropDownOpening += (_, _) => UpdateWidgetMenu();
        trayMenu.Items.Add(_widgetSettingsMenu);
        trayMenu.Items.Add(new ToolStripSeparator());

        _resetPositionMenuItem = new ToolStripMenuItem("Reset Position", null, (_, _) => ResetToDefaultPosition());
        trayMenu.Items.Add(_resetPositionMenuItem);
        trayMenu.Items.Add(new ToolStripSeparator());

        var colorMenu = new ToolStripMenuItem("Clock Color");
        foreach (var colorOption in Palette)
        {
            var item = new ToolStripMenuItem($"{colorOption.Name} ({colorOption.Hex})")
            {
                Tag = colorOption.Color
            };
            item.Click += (_, _) => ApplyPaletteColor((Color)item.Tag!);
            colorMenu.DropDownItems.Add(item);
            _colorMenuItems[colorOption.Color.ToArgb()] = item;
        }

        trayMenu.Items.Add(colorMenu);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => Close());
        return trayMenu;
    }

    private void LoadSettings()
    {
        var settings = _settingsStore.Load();
        _settings = settings;
        _preferredDisplayMode = settings.DisplayMode;
        _activeDisplayMode = _preferredDisplayMode;
        _taskbarDashboardWidthDip = Math.Clamp(settings.TaskbarDashboardWidthDip, 160, 500);
        _taskbarHorizontalOffsetDip = Math.Clamp(settings.TaskbarHorizontalOffsetDip, 0, 2000);
        _taskbarSafeAreaMarginDip = Math.Clamp(settings.TaskbarSafeAreaMarginDip, 0, 64);
        _taskbarHorizontalAlignment = Enum.IsDefined(settings.HorizontalAlignment)
            ? settings.HorizontalAlignment
            : TaskbarHorizontalAlignment.Left;
        _taskbarFarLeftFallbackInsetDip = Math.Clamp(settings.TaskbarFarLeftFallbackInsetDip, 0, 1000);
        _taskbarPlacementRegion = Enum.IsDefined(settings.PlacementRegion)
            ? settings.PlacementRegion
            : TaskbarPlacementRegion.FarLeft;
        ApplyModeAppearance();
        ApplyPaletteColor(Color.FromArgb(settings.ForegroundColorArgb), shouldSave: false);

        if (settings.PositionX.HasValue && settings.PositionY.HasValue)
        {
            _savedLocation = new Point(settings.PositionX.Value, settings.PositionY.Value);
            _useDefaultPosition = false;
        }
        else
        {
            _useDefaultPosition = true;
        }

        SetFontSize(settings.FontSize, shouldSave: false);
        _batteryWidget.UpdateDevices(_batteryService.Snapshot, _settings);
        UpdateModeMenu();

        if (settings.ShouldMigrateDisplayMode || settings.ShouldMigrateHorizontalLayout)
        {
            DashboardDiagnostics.Write(
                "Settings",
                $"Migrating settings; display mode migration={settings.ShouldMigrateDisplayMode}; horizontal layout migration={settings.ShouldMigrateHorizontalLayout}; placement region={_taskbarPlacementRegion}; alignment={_taskbarHorizontalAlignment}; safe-area margin={_taskbarSafeAreaMarginDip} DIP; far-left fallback inset={_taskbarFarLeftFallbackInsetDip} DIP.");
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        if (_activeDisplayMode != ClockDisplayMode.TaskbarAnchored)
        {
            _settings.ForegroundColorArgb = _clockLabel.ForeColor.ToArgb();
        }

        _settings.FontSize = _fontSize;
        _settings.DisplayMode = _preferredDisplayMode;
        _settings.TaskbarDashboardWidthDip = _taskbarDashboardWidthDip;
        _settings.TaskbarHorizontalOffsetDip = _taskbarHorizontalOffsetDip;
        _settings.TaskbarSafeAreaMarginDip = _taskbarSafeAreaMarginDip;
        _settings.HorizontalAlignment = _taskbarHorizontalAlignment;
        _settings.TaskbarFarLeftFallbackInsetDip = _taskbarFarLeftFallbackInsetDip;
        _settings.PlacementRegion = _taskbarPlacementRegion;
        _settings.PositionX = _useDefaultPosition ? null : _savedLocation.X;
        _settings.PositionY = _useDefaultPosition ? null : _savedLocation.Y;
        _settingsStore.Save(_settings);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                if (_preferredDisplayMode == ClockDisplayMode.AppBar)
                {
                    _appBarManager.NotifyDisplayChanged();
                }
                else if (_preferredDisplayMode == ClockDisplayMode.TaskbarAnchored)
                {
                    _taskbarAnchoredManager.NotifyGeometryChanged();
                }
                else if (_useDefaultPosition)
                {
                    ApplyDefaultPosition();
                }
                else
                {
                    ClampToPrimaryScreen();
                    SaveSettings();
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The form can be closing while Windows broadcasts display changes.
        }
    }

    private void UpdateClockAndLayout()
    {
        _clockLabel.Text = DateTime.Now.ToString("HH:mm:ss");

        if (_activeDisplayMode == ClockDisplayMode.AppBar)
        {
            LayoutAppBarClock();
            return;
        }

        if (_activeDisplayMode == ClockDisplayMode.TaskbarAnchored)
        {
            _clockLabel.Invalidate();
            return;
        }

        _clockLabel.Location = Point.Empty;
        ClientSize = _clockLabel.PreferredSize;

        if (!_didInitialPlacement)
        {
            if (_useDefaultPosition)
            {
                ApplyDefaultPosition();
            }
            else
            {
                Location = _savedLocation;
                ClampToPrimaryScreen();
            }

            _didInitialPlacement = true;
            return;
        }

        if (_useDefaultPosition)
        {
            ApplyDefaultPosition();
        }

        EnsureAlwaysOnTop();
    }

    private void LayoutAppBarClock()
    {
        var leftMargin = Math.Max(1, (int)Math.Round(12d * DeviceDpi / 96d));
        var top = Math.Max(0, (ClientSize.Height - _clockLabel.Height) / 2);
        _clockLabel.Location = new Point(leftMargin, top);
    }

    private void RefreshClockFontForCurrentDpi()
    {
        var previousFont = _clockLabel.Font;
        _clockLabel.Font = new Font(previousFont.FontFamily, _fontSize, FontStyle.Bold);
        previousFont.Dispose();
        _clockWidget.SetPreferredFontSize(_fontSize);
        UpdateClockAndLayout();

        if (_activeDisplayMode == ClockDisplayMode.Overlay && !_useDefaultPosition)
        {
            ClampToPrimaryScreen();
            SaveSettings();
        }

        if (_activeDisplayMode == ClockDisplayMode.TaskbarAnchored)
        {
            _taskbarAnchoredManager.NotifyGeometryChanged();
        }
    }

    internal void OnAppBarPositioned()
    {
        if (_activeDisplayMode == ClockDisplayMode.AppBar)
        {
            LayoutAppBarClock();
        }
    }

    internal bool AppBarIsRegistered => _appBarManager.IsRegistered;

    internal string TaskbarLayoutStatus => _taskbarLayoutResult.HiddenWidgetCount > 0
        ? $"{_taskbarLayoutResult.HiddenWidgetCount} widget(s) hidden for available taskbar space"
        : "all enabled widgets fit";

    internal bool TryCalculateTaskbarAnchoredWidth(Rectangle maximumBounds, int dpi, out int widthPixels, out string? error)
    {
        widthPixels = 0;
        error = null;
        if (_activeDisplayMode != ClockDisplayMode.TaskbarAnchored)
        {
            _activeDisplayMode = ClockDisplayMode.TaskbarAnchored;
            ApplyModeAppearance();
            ApplyExtendedWindowStyles();
        }

        if (maximumBounds.Width <= 0 || maximumBounds.Height <= 0)
        {
            error = "The safe taskbar area has no usable width or height.";
            return false;
        }

        _clockWidget.Prepare(maximumBounds.Height, dpi, maximumBounds.Width);
        var widgets = new IDashboardWidget[] { _clockWidget, _batteryWidget };
        var measures = widgets
            .Select(widget => new DashboardWidgetMeasure(
                widget.Id,
                widget.Priority,
                widget.GetPreferredWidthPixels(dpi, _settings.CompactLayout),
                IsWidgetEnabled(widget.Id)))
            .ToArray();
        var spacing = ClockWidget.DipToPixels(_settings.CompactLayout ? 4 : 8, dpi);
        var statusWidth = ClockWidget.DipToPixels(18, dpi);
        var separatorWidth = DashboardSeparator.GetWidthPixels(dpi);
        _taskbarLayoutResult = DashboardLayoutCalculator.Calculate(
            maximumBounds.Width,
            measures,
            _settings.WidgetOrder,
            spacing,
            statusWidth,
            separatorWidth);

        if (_taskbarLayoutResult.Width <= 0 ||
            (_taskbarLayoutResult.VisibleWidgetIds.Count == 0 && !_taskbarLayoutResult.ShowsHiddenStatus))
        {
            error = "No enabled dashboard widget fits in the safe taskbar area. Enable a widget or increase the available taskbar gap.";
            var noFitKey = $"no-fit:{maximumBounds.Width}x{maximumBounds.Height}@{dpi}";
            if (!string.Equals(_lastLoggedTaskbarLayoutKey, noFitKey, StringComparison.Ordinal))
            {
                _lastLoggedTaskbarLayoutKey = noFitKey;
                DashboardDiagnostics.Write("TaskbarAnchored", $"Widget layout has no visible widgets; available width={maximumBounds.Width}px; enabled={string.Join(",", _settings.EnabledWidgets)}.");
            }

            return false;
        }

        var layoutKey = $"available={maximumBounds.Width}x{maximumBounds.Height};dpi={dpi};width={_taskbarLayoutResult.Width};visible={string.Join(",", _taskbarLayoutResult.VisibleWidgetIds)};hidden={string.Join(",", _taskbarLayoutResult.HiddenWidgetIds)};status={_taskbarLayoutResult.ShowsHiddenStatus}";
        if (!string.Equals(_lastLoggedTaskbarLayoutKey, layoutKey, StringComparison.Ordinal))
        {
            _lastLoggedTaskbarLayoutKey = layoutKey;
            DashboardDiagnostics.Write("DashboardLayout", $"Layout recalculated; {layoutKey}.");
        }

        widthPixels = _taskbarLayoutResult.Width;
        return true;
    }

    internal bool PrepareTaskbarAnchoredLayout(Rectangle bounds, int dpi)
    {
        if (_activeDisplayMode != ClockDisplayMode.TaskbarAnchored)
        {
            _activeDisplayMode = ClockDisplayMode.TaskbarAnchored;
            ApplyModeAppearance();
            ApplyExtendedWindowStyles();
        }

        if (_taskbarLayoutResult.Width <= 0 || bounds.Width != _taskbarLayoutResult.Width)
        {
            if (!TryCalculateTaskbarAnchoredWidth(bounds, dpi, out _, out _))
            {
                return false;
            }
        }

        var spacing = ClockWidget.DipToPixels(_settings.CompactLayout ? 4 : 8, dpi);
        var separatorWidth = DashboardSeparator.GetWidthPixels(dpi);
        _dashboardPanel.Visible = false;
        _dashboardPanel.Controls.Clear();
        _dashboardPanel.Bounds = new Rectangle(Point.Empty, bounds.Size);
        ClientSize = bounds.Size;
        var left = 0;
        string? previousWidgetId = null;
        var widgets = new IDashboardWidget[] { _clockWidget, _batteryWidget };
        foreach (var id in _taskbarLayoutResult.VisibleWidgetIds)
        {
            if (previousWidgetId is not null && IsClockBatteryPair(previousWidgetId, id))
            {
                _dashboardSeparator.Bounds = new Rectangle(left, 0, separatorWidth, bounds.Height);
                _dashboardPanel.Controls.Add(_dashboardSeparator);
                left += separatorWidth + spacing;
            }

            var widget = widgets.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
            if (widget is null)
            {
                continue;
            }

            var width = widget.GetPreferredWidthPixels(dpi, _settings.CompactLayout);
            widget.ApplyBounds(new Rectangle(left, 0, width, bounds.Height), dpi, _settings.CompactLayout);
            _dashboardPanel.Controls.Add(widget.View);
            left += width + spacing;
            previousWidgetId = id;
        }

        if (_taskbarLayoutResult.ShowsHiddenStatus)
        {
            var statusWidth = Math.Min(ClockWidget.DipToPixels(18, dpi), Math.Max(1, bounds.Width - left));
            if (statusWidth > 0)
            {
                _layoutStatusIndicator.SetHiddenWidgets(_taskbarLayoutResult.HiddenWidgetIds);
                _layoutStatusIndicator.Bounds = new Rectangle(left, 0, statusWidth, bounds.Height);
                _dashboardPanel.Controls.Add(_layoutStatusIndicator);
            }
        }

        if (_clockLabel.Parent != _dashboardPanel && _taskbarLayoutResult.VisibleWidgetIds.Contains(DashboardWidgetIds.Clock, StringComparer.OrdinalIgnoreCase))
        {
            _dashboardPanel.Controls.Add(_clockLabel);
        }

        _dashboardPanel.Visible = true;
        _dashboardPanel.BringToFront();
        return true;
    }

    internal void HideTaskbarAnchoredDashboard()
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        if (Visible)
        {
            Hide();
        }
    }

    private void SetDisplayMode(ClockDisplayMode mode)
    {
        DashboardDiagnostics.Write("DisplayMode", $"User requested display mode change from {_preferredDisplayMode} to {mode}.");
        _preferredDisplayMode = mode;

        if (mode == ClockDisplayMode.Overlay)
        {
            _taskbarAnchoredManager.Stop();
            _appBarManager.Stop();
            if (_activeDisplayMode == ClockDisplayMode.AppBar)
            {
                _didInitialPlacement = false;
            }

            _activeDisplayMode = ClockDisplayMode.Overlay;
            ApplyModeAppearance();
            ApplyExtendedWindowStyles();
            UpdateClockAndLayout();
            EnsureAlwaysOnTop();
            EnsureVisibleWithoutActivation();
        }
        else if (mode == ClockDisplayMode.AppBar)
        {
            _taskbarAnchoredManager.Stop();
            _activeDisplayMode = ClockDisplayMode.AppBar;
            ApplyModeAppearance();
            ApplyExtendedWindowStyles();

            if (IsHandleCreated && !_appBarManager.Start())
            {
                ActivateOverlayFallback(_appBarManager.LastFailureReason, showNotice: true);
            }
            else
            {
                UpdateClockAndLayout();
                EnsureVisibleWithoutActivation();
            }
        }
        else
        {
            _appBarManager.Stop();
            _activeDisplayMode = ClockDisplayMode.TaskbarAnchored;
            ApplyModeAppearance();
            ApplyExtendedWindowStyles();

            if (IsHandleCreated && !_taskbarAnchoredManager.Start(
                    _taskbarHorizontalOffsetDip,
                    _taskbarSafeAreaMarginDip,
                    _taskbarHorizontalAlignment,
                    _taskbarFarLeftFallbackInsetDip,
                    _taskbarPlacementRegion))
            {
                HandleTaskbarAnchoredUnavailable(_taskbarAnchoredManager.LastFailureReason, showNotice: true);
            }
            else
            {
                UpdateClockAndLayout();
                EnsureVisibleWithoutActivation();
            }
        }

        UpdateModeMenu();
        SaveSettings();
    }

    private void OnAppBarAvailabilityChanged(bool isAvailable, string? failureReason)
    {
        if (IsDisposed || _preferredDisplayMode != ClockDisplayMode.AppBar)
        {
            return;
        }

        if (!isAvailable)
        {
            ActivateOverlayFallback(failureReason, showNotice: true);
            return;
        }

        _activeDisplayMode = ClockDisplayMode.AppBar;
        ApplyModeAppearance();
        ApplyExtendedWindowStyles();
        UpdateClockAndLayout();
        EnsureVisibleWithoutActivation();
        UpdateModeMenu();
    }

    private void OnTaskbarAnchoredPlacementStarting()
    {
        if (_preferredDisplayMode != ClockDisplayMode.TaskbarAnchored || IsDisposed)
        {
            return;
        }

        _appBarManager.Stop();
        if (_activeDisplayMode != ClockDisplayMode.TaskbarAnchored)
        {
            _activeDisplayMode = ClockDisplayMode.TaskbarAnchored;
            ApplyModeAppearance();
            ApplyExtendedWindowStyles();
            UpdateModeMenu();
        }
    }

    private void OnTaskbarAnchoredAvailabilityChanged(bool isAvailable, string? failureReason)
    {
        if (_preferredDisplayMode != ClockDisplayMode.TaskbarAnchored || IsDisposed)
        {
            return;
        }

        if (!isAvailable)
        {
            HandleTaskbarAnchoredUnavailable(failureReason, showNotice: true);
            return;
        }

        _appBarManager.Stop();
        _activeDisplayMode = ClockDisplayMode.TaskbarAnchored;
        ApplyModeAppearance();
        ApplyExtendedWindowStyles();
        UpdateClockAndLayout();
        EnsureVisibleWithoutActivation();
        _hasShownTaskbarUnavailableNotice = false;
        UpdateModeMenu();
    }

    private void HandleTaskbarAnchoredUnavailable(string? failureReason, bool showNotice)
    {
        _appBarManager.Stop();
        _activeDisplayMode = ClockDisplayMode.TaskbarAnchored;
        ApplyModeAppearance();
        ApplyExtendedWindowStyles();
        HideTaskbarAnchoredDashboard();
        UpdateModeMenu();

        var reason = string.IsNullOrWhiteSpace(failureReason) ? "The dashboard could not be placed safely in the taskbar." : failureReason;
        DashboardDiagnostics.Write(
            "DisplayMode",
            $"Requested display mode={_preferredDisplayMode}; effective display mode=Taskbar Anchored (unavailable, hidden); AppBar registered={_appBarManager.IsRegistered}; reason={reason}");

        if (showNotice && !_hasShownTaskbarUnavailableNotice && _trayIcon.Visible)
        {
            _hasShownTaskbarUnavailableNotice = true;
            _trayIcon.ShowBalloonTip(
                5000,
                "Taskbar Anchored unavailable",
                $"{reason} The dashboard is hidden. Choose Taskbar AppBar or Legacy Clock Overlay in Display Mode to switch explicitly. Details: {DashboardDiagnostics.FilePath}",
                ToolTipIcon.Warning);
        }
    }

    private void ActivateOverlayFallback(string? failureReason, bool showNotice)
    {
        if (_activeDisplayMode == ClockDisplayMode.AppBar)
        {
            _didInitialPlacement = false;
        }

        _activeDisplayMode = ClockDisplayMode.Overlay;
        ApplyModeAppearance();
        ApplyExtendedWindowStyles();
        UpdateClockAndLayout();
        EnsureAlwaysOnTop();
        EnsureVisibleWithoutActivation();
        UpdateModeMenu();

        if (!string.IsNullOrWhiteSpace(failureReason))
        {
            Trace.TraceWarning("AppBar mode is unavailable: {0}", failureReason);
            DashboardDiagnostics.Write("DisplayMode", $"Requested display mode={_preferredDisplayMode}; effective display mode=Legacy Clock Overlay (AppBar fallback); reason={failureReason}");

            if (showNotice && !_hasShownAppBarFallbackNotice && _trayIcon.Visible)
            {
                _hasShownAppBarFallbackNotice = true;
                _trayIcon.ShowBalloonTip(
                    3500,
                    "Windows Clock Overlay",
                    $"{failureReason} The clock is using its legacy overlay mode.",
                    ToolTipIcon.Warning);
            }
        }
    }

    private void ApplyModeAppearance()
    {
        if (_activeDisplayMode != ClockDisplayMode.TaskbarAnchored)
        {
            if (ReferenceEquals(_clockLabel.Parent, _dashboardPanel))
            {
                _dashboardPanel.Controls.Remove(_clockLabel);
                Controls.Add(_clockLabel);
            }

            if (ReferenceEquals(_batteryWidget.View.Parent, _dashboardPanel))
            {
                _dashboardPanel.Controls.Remove(_batteryWidget.View);
            }

            if (ReferenceEquals(_layoutStatusIndicator.Parent, _dashboardPanel))
            {
                _dashboardPanel.Controls.Remove(_layoutStatusIndicator);
            }

            _dashboardPanel.Visible = false;
        }

        if (_activeDisplayMode != ClockDisplayMode.TaskbarAnchored && _clockLabel.Font.Size != _fontSize)
        {
            var previousFont = _clockLabel.Font;
            _clockLabel.Font = new Font(previousFont.FontFamily, _fontSize, FontStyle.Bold);
            previousFont.Dispose();
        }

        if (_activeDisplayMode == ClockDisplayMode.AppBar)
        {
            BackColor = Color.FromArgb(32, 32, 32);
            TransparencyKey = Color.Empty;
            Text = "Windows Clock Dashboard";
            _clockLabel.AutoSize = true;
            _clockLabel.Dock = DockStyle.None;
            _clockLabel.Padding = Padding.Empty;
            _clockLabel.TextAlign = ContentAlignment.MiddleLeft;
        }
        else if (_activeDisplayMode == ClockDisplayMode.TaskbarAnchored)
        {
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            Text = "Windows Clock Dashboard (Taskbar Anchored)";
            _clockLabel.AutoSize = false;
            _clockLabel.Dock = DockStyle.None;
            _clockLabel.Padding = Padding.Empty;
            _clockLabel.TextAlign = ContentAlignment.MiddleCenter;
        }
        else
        {
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            Text = "Windows Clock Overlay";
            _clockLabel.AutoSize = true;
            _clockLabel.Dock = DockStyle.None;
            _clockLabel.Padding = Padding.Empty;
            _clockLabel.TextAlign = ContentAlignment.TopLeft;
        }

        if (_clockLabel is DashboardClockLabel dashboardClockLabel)
        {
            dashboardClockLabel.TaskbarStyled = _activeDisplayMode == ClockDisplayMode.TaskbarAnchored;
        }

        // Keep the taskbar dashboard's foreground independent from the saved
        // accent used by the legacy overlay and AppBar modes.
        _clockLabel.ForeColor = _activeDisplayMode == ClockDisplayMode.TaskbarAnchored
            ? Color.White
            : Color.FromArgb(_settings.ForegroundColorArgb);

        if (_activeDisplayMode != ClockDisplayMode.TaskbarAnchored)
        {
            TopMost = true;
        }
    }

    private void ApplyExtendedWindowStyles()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var styles = NativeMethods.GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        styles |= WsExToolWindow | WsExNoActivate;
        styles &= ~WsExTransparent;
        if (_activeDisplayMode == ClockDisplayMode.AppBar)
        {
            styles &= ~WsExLayered;
        }
        else if (_activeDisplayMode == ClockDisplayMode.TaskbarAnchored)
        {
            styles |= WsExTransparent;
            styles |= WsExLayered;
        }
        else
        {
            styles |= WsExLayered;
        }

        NativeMethods.SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(styles));
        NativeMethods.SetWindowPos(
            Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpNoownerzorder | SwpFramechanged);
    }

    private void UpdateModeMenu()
    {
        foreach (var (mode, item) in _modeMenuItems)
        {
            item.Checked = mode == _preferredDisplayMode;
        }

        var effectiveMode = GetEffectiveDisplayModeText();
        _requestedModeStatusItem.Text = $"Requested: {GetDisplayModeLabel(_preferredDisplayMode)}";
        _effectiveModeStatusItem.Text = $"Effective: {effectiveMode}";
        var positioningError = _preferredDisplayMode switch
        {
            ClockDisplayMode.TaskbarAnchored => _taskbarAnchoredManager.LastFailureReason,
            ClockDisplayMode.AppBar => _appBarManager.LastFailureReason,
            _ => null
        };
        _positioningErrorStatusItem.Text = string.IsNullOrWhiteSpace(positioningError)
            ? "Positioning error: none"
            : $"Positioning error: {positioningError}";

        _resetPositionMenuItem.Enabled = _activeDisplayMode == ClockDisplayMode.Overlay;

        var modeState = $"Requested display mode={_preferredDisplayMode}; effective display mode={effectiveMode}; AppBar registered={_appBarManager.IsRegistered}; Taskbar Anchored available={_taskbarAnchoredManager.IsAvailable}; positioning error={positioningError ?? "none"}.";

        if (_lastLoggedModeState != modeState)
        {
            DashboardDiagnostics.Write("DisplayMode", modeState);
            _lastLoggedModeState = modeState;
        }
    }

    private string GetEffectiveDisplayModeText()
    {
        if (_activeDisplayMode == ClockDisplayMode.TaskbarAnchored)
        {
            if (_taskbarAnchoredManager.IsFullscreenSuspended)
            {
                return "Taskbar Anchored (hidden for full-screen window)";
            }

            if (!_taskbarAnchoredManager.IsAvailable)
            {
                return "Taskbar Anchored unavailable (hidden)";
            }

            if (_taskbarLayoutResult.HiddenWidgetCount > 0)
            {
                return $"Taskbar Anchored ({_taskbarLayoutResult.HiddenWidgetCount} widget(s) hidden for space)";
            }

            return "Taskbar Anchored";
        }

        if (_activeDisplayMode == ClockDisplayMode.AppBar)
        {
            return _appBarManager.IsRegistered ? "Taskbar AppBar" : "Taskbar AppBar (not registered)";
        }

        return _preferredDisplayMode == ClockDisplayMode.Overlay
            ? "Legacy Clock Overlay"
            : "Legacy Clock Overlay (fallback)";
    }

    private static string GetDisplayModeLabel(ClockDisplayMode mode)
    {
        return mode switch
        {
            ClockDisplayMode.AppBar => "Taskbar AppBar",
            ClockDisplayMode.TaskbarAnchored => "Taskbar Anchored",
            ClockDisplayMode.Overlay => "Legacy Clock Overlay",
            _ => mode.ToString()
        };
    }

    private void ConfigureTaskbarAnchoredLayout()
    {
        using var dialog = new TaskbarAnchorSettingsForm(
            _taskbarHorizontalOffsetDip,
            _taskbarSafeAreaMarginDip,
            _taskbarHorizontalAlignment,
            _taskbarFarLeftFallbackInsetDip,
            _taskbarPlacementRegion);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _taskbarHorizontalOffsetDip = dialog.HorizontalOffsetDip;
        _taskbarSafeAreaMarginDip = dialog.SafeAreaMarginDip;
        _taskbarHorizontalAlignment = dialog.HorizontalAlignment;
        _taskbarFarLeftFallbackInsetDip = dialog.FarLeftFallbackInsetDip;
        _taskbarPlacementRegion = dialog.PlacementRegion;
        _settings.TaskbarHorizontalOffsetDip = _taskbarHorizontalOffsetDip;
        _settings.TaskbarSafeAreaMarginDip = _taskbarSafeAreaMarginDip;
        _settings.HorizontalAlignment = _taskbarHorizontalAlignment;
        _settings.TaskbarFarLeftFallbackInsetDip = _taskbarFarLeftFallbackInsetDip;
        _settings.PlacementRegion = _taskbarPlacementRegion;
        _taskbarAnchoredManager.UpdateLayout(
            _taskbarHorizontalOffsetDip,
            _taskbarSafeAreaMarginDip,
            _taskbarHorizontalAlignment,
            _taskbarFarLeftFallbackInsetDip,
            _taskbarPlacementRegion);
        SaveSettings();
    }

    private void ConfigureDashboardSettings()
    {
        using var dialog = new DashboardSettingsForm(_settings, _batteryService.Snapshot);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings = dialog.EditedSettings;
        _taskbarHorizontalOffsetDip = Math.Clamp(_settings.TaskbarHorizontalOffsetDip, 0, 2000);
        _taskbarSafeAreaMarginDip = Math.Clamp(_settings.TaskbarSafeAreaMarginDip, 0, 64);
        _taskbarHorizontalAlignment = Enum.IsDefined(_settings.HorizontalAlignment)
            ? _settings.HorizontalAlignment
            : TaskbarHorizontalAlignment.Left;
        _taskbarFarLeftFallbackInsetDip = Math.Clamp(_settings.TaskbarFarLeftFallbackInsetDip, 0, 1000);
        _taskbarPlacementRegion = Enum.IsDefined(_settings.PlacementRegion)
            ? _settings.PlacementRegion
            : TaskbarPlacementRegion.FarLeft;
        _batteryWidget.UpdateDevices(_batteryService.Snapshot, _settings);
        _taskbarAnchoredManager.UpdateLayout(
            _taskbarHorizontalOffsetDip,
            _taskbarSafeAreaMarginDip,
            _taskbarHorizontalAlignment,
            _taskbarFarLeftFallbackInsetDip,
            _taskbarPlacementRegion);
        UpdateWidgetMenu();
        UpdateModeMenu();
        SaveSettings();
    }

    private void UpdateWidgetMenu()
    {
        if (_widgetSettingsMenu is null)
        {
            return;
        }

        _widgetSettingsMenu.DropDownItems.Clear();
        AddWidgetMenuItem(DashboardWidgetIds.Clock, "Clock");
        AddWidgetMenuItem(DashboardWidgetIds.BluetoothBattery, "Bluetooth batteries");
        _widgetSettingsMenu.DropDownItems.Add(new ToolStripSeparator());
        _widgetSettingsMenu.DropDownItems.Add("Configure widgets and devices...", null, (_, _) => ConfigureDashboardSettings());

        void AddWidgetMenuItem(string id, string title)
        {
            var item = new ToolStripMenuItem(title)
            {
                Checked = IsWidgetEnabled(id),
                CheckOnClick = true
            };
            item.Click += (_, _) =>
            {
                var enabled = IsWidgetEnabled(id);
                _settings.EnabledWidgets.RemoveAll(widgetId => string.Equals(widgetId, id, StringComparison.OrdinalIgnoreCase));
                if (!enabled)
                {
                    _settings.EnabledWidgets.Add(id);
                }

                _taskbarAnchoredManager.NotifyGeometryChanged();
                SaveSettings();
                UpdateModeMenu();
            };
            _widgetSettingsMenu.DropDownItems.Add(item);
        }
    }

    private static bool IsClockBatteryPair(string first, string second) =>
        (string.Equals(first, DashboardWidgetIds.Clock, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(second, DashboardWidgetIds.BluetoothBattery, StringComparison.OrdinalIgnoreCase)) ||
        (string.Equals(first, DashboardWidgetIds.BluetoothBattery, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(second, DashboardWidgetIds.Clock, StringComparison.OrdinalIgnoreCase));

    private bool IsWidgetEnabled(string id) => _settings.EnabledWidgets.Contains(id, StringComparer.OrdinalIgnoreCase);

    private void OnBluetoothSnapshotChanged(IReadOnlyList<BluetoothBatteryDevice> devices)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                if (IsDisposed)
                {
                    return;
                }

                var batteryLayoutChanged = _batteryWidget.UpdateDevices(devices, _settings);
                if (batteryLayoutChanged && _activeDisplayMode == ClockDisplayMode.TaskbarAnchored)
                {
                    DashboardDiagnostics.Write("DashboardLayout", "Bluetooth device membership, labels, or display mode changed; recalculating dashboard width.");
                    _taskbarAnchoredManager.NotifyGeometryChanged();
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // Shutdown can race with the background device scan.
        }
    }

    private void EnsureVisibleWithoutActivation()
    {
        if (!Visible && !IsDisposed)
        {
            Show();
        }
    }

    private void Zoom(int direction)
    {
        if (direction == 0)
        {
            return;
        }

        SetFontSize(_fontSize + direction * FontSizeStep);
    }

    private void SetFontSize(float fontSize, bool shouldSave = true)
    {
        _fontSize = Math.Clamp(fontSize, MinimumFontSize, MaximumFontSize);
        var previousFont = _clockLabel.Font;
        _clockLabel.Font = new Font(previousFont.FontFamily, _fontSize, FontStyle.Bold);
        previousFont.Dispose();
        _clockWidget.SetPreferredFontSize(_fontSize);
        UpdateClockAndLayout();

        if (_activeDisplayMode == ClockDisplayMode.Overlay && !_useDefaultPosition)
        {
            ClampToPrimaryScreen();
        }

        if (shouldSave)
        {
            SaveSettings();
        }
    }

    private static bool IsControlWheel(IntPtr wParam)
    {
        return ((long)wParam & MkControl) != 0;
    }

    private static short GetWheelDelta(IntPtr wParam)
    {
        return unchecked((short)(((long)wParam >> 16) & 0xffff));
    }

    private void WireMoveHandlers(Control control)
    {
        control.MouseDown += OnMoveMouseDown;
        control.MouseMove += OnMoveMouseMove;
        control.MouseUp += OnMoveMouseUp;
    }

    private void OnMoveMouseDown(object? sender, MouseEventArgs e)
    {
        if (_activeDisplayMode != ClockDisplayMode.Overlay || e.Button != MouseButtons.Left)
        {
            return;
        }

        _isDragging = true;
        _useDefaultPosition = false;
        _dragStartCursor = Cursor.Position;
        _dragStartWindow = Location;
    }

    private void OnMoveMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var cursor = Cursor.Position;
        var deltaX = cursor.X - _dragStartCursor.X;
        var deltaY = cursor.Y - _dragStartCursor.Y;
        Location = new Point(_dragStartWindow.X + deltaX, _dragStartWindow.Y + deltaY);
    }

    private void OnMoveMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_isDragging)
        {
            return;
        }

        _isDragging = false;
        ClampToPrimaryScreen();
        SaveSettings();
    }

    private void ResetToDefaultPosition()
    {
        if (_activeDisplayMode != ClockDisplayMode.Overlay)
        {
            return;
        }

        _useDefaultPosition = true;
        ApplyDefaultPosition();
        SaveSettings();
    }

    private void ApplyDefaultPosition()
    {
        var bounds = GetActiveScreenBounds();
        Location = new Point(
            bounds.Right - Width - RightMargin,
            bounds.Top + TopMargin);
    }

    private void ClampToPrimaryScreen()
    {
        var bounds = GetActiveScreenBounds();
        var maxX = bounds.Right - Width;
        var maxY = bounds.Bottom - Height;

        Location = new Point(
            Math.Clamp(Location.X, bounds.Left, maxX),
            Math.Clamp(Location.Y, bounds.Top, maxY));
        _savedLocation = Location;
    }

    private Rectangle GetActiveScreenBounds()
    {
        if (_didInitialPlacement)
        {
            return Screen.FromPoint(Location).Bounds;
        }

        return Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    }

    private void ApplyPaletteColor(Color color, bool shouldSave = true)
    {
        if (shouldSave)
        {
            _settings.ForegroundColorArgb = color.ToArgb();
        }

        _clockLabel.ForeColor = _activeDisplayMode == ClockDisplayMode.TaskbarAnchored
            ? Color.White
            : color;
        SetSelectedColorMenuItem(color);
        if (shouldSave)
        {
            SaveSettings();
        }
    }

    private void SetSelectedColorMenuItem(Color color)
    {
        foreach (var item in _colorMenuItems.Values)
        {
            item.Checked = false;
        }

        if (_colorMenuItems.TryGetValue(color.ToArgb(), out var match))
        {
            match.Checked = true;
            return;
        }

        if (_colorMenuItems.TryGetValue(Palette[0].Color.ToArgb(), out var fallback))
        {
            fallback.Checked = true;
        }
    }

    private void EnsureAlwaysOnTop()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        TopMost = true;
        NativeMethods.SetWindowPos(
            Handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpNoownerzorder);
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var embeddedStream = assembly.GetManifestResourceStream(EmbeddedIconResourceName);
            if (embeddedStream is not null)
            {
                return LoadIconFromStream(embeddedStream);
            }

            var filePath = Path.Combine(AppContext.BaseDirectory, "clock.ico");
            if (File.Exists(filePath))
            {
                using var fileStream = File.OpenRead(filePath);
                return LoadIconFromStream(fileStream);
            }
        }
        catch
        {
            // Fall through to default icon.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static Icon LoadIconFromStream(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;

        try
        {
            using var icon = new Icon(memory);
            using var bitmap = icon.ToBitmap();
            return CreateIconFromBitmap(bitmap);
        }
        catch
        {
            memory.Position = 0;
            using var bitmap = new Bitmap(memory);
            return CreateIconFromBitmap(bitmap);
        }
    }

    private static Icon CreateIconFromBitmap(Bitmap source)
    {
        using var canvas = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var scale = Math.Min(16f / source.Width, 16f / source.Height) * TrayIconScale;
            var drawWidth = source.Width * scale;
            var drawHeight = source.Height * scale;
            var drawX = (16f - drawWidth) / 2f;
            var drawY = (16f - drawHeight) / 2f;
            graphics.DrawImage(source, drawX, drawY, drawWidth, drawHeight);
        }

        var handle = canvas.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
