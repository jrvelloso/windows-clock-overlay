using System.Drawing.Drawing2D;

namespace WindowsClockOverlay;

internal static class DashboardWidgetIds
{
    public const string Clock = "clock";
    public const string BluetoothBattery = "bluetoothBattery";
}

internal interface IDashboardWidget
{
    string Id { get; }
    int Priority { get; }
    Control View { get; }
    int GetPreferredWidthPixels(int dpi, bool compact);
    void ApplyBounds(Rectangle bounds, int dpi, bool compact);
}

internal sealed record DashboardWidgetMeasure(string Id, int Priority, int Width, bool Enabled = true);

internal sealed record DashboardLayoutResult(IReadOnlyList<string> VisibleWidgetIds, int Width, int HiddenWidgetCount, bool ShowsHiddenStatus)
{
    public IReadOnlyList<string> HiddenWidgetIds { get; init; } = Array.Empty<string>();
}

internal static class DashboardLayoutCalculator
{
    public static DashboardLayoutResult Calculate(
        int availableWidth,
        IReadOnlyList<DashboardWidgetMeasure> widgets,
        IReadOnlyList<string> configuredOrder,
        int spacing,
        int statusWidth,
        int separatorWidth = 0)
    {
        var order = configuredOrder
            .Select((id, index) => (id, index))
            .GroupBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        var candidates = widgets
            .Where(widget => widget.Enabled && widget.Width > 0)
            .OrderByDescending(widget => widget.Priority)
            .ThenBy(widget => order.GetValueOrDefault(widget.Id, int.MaxValue))
            .ToArray();

        var selected = new List<DashboardWidgetMeasure>();
        var hidden = new List<DashboardWidgetMeasure>();
        var used = 0;
        foreach (var widget in candidates)
        {
            var candidateWidth = selected.Count == 0 ? widget.Width : used + spacing + widget.Width;
            if (separatorWidth > 0 && selected.Any(selectedWidget => IsClockBatteryPair(selectedWidget.Id, widget.Id)))
            {
                candidateWidth += spacing + separatorWidth;
            }

            if (candidateWidth <= availableWidth)
            {
                selected.Add(widget);
                used = candidateWidth;
            }
            else
            {
                hidden.Add(widget);
            }
        }

        var showsStatus = hidden.Count > 0 && statusWidth > 0;
        if (showsStatus && used + spacing + statusWidth > availableWidth)
        {
            var visible = selected.OrderByDescending(widget => widget.Priority).ThenBy(widget => order.GetValueOrDefault(widget.Id, int.MaxValue)).ToArray();
            while (visible.Length > 0 && CalculateCombinedWidth(visible, spacing, separatorWidth) + spacing + statusWidth > availableWidth)
            {
                var removed = visible[^1];
                hidden.Add(removed);
                visible = visible[..^1];
            }

            selected = visible.ToList();
            used = CalculateCombinedWidth(selected, spacing, separatorWidth);
            if (selected.Count == 0)
            {
                showsStatus = statusWidth <= availableWidth;
            }
            else if (used + spacing + statusWidth > availableWidth)
            {
                showsStatus = false;
            }
        }

        selected = selected
            .OrderBy(widget => order.GetValueOrDefault(widget.Id, int.MaxValue))
            .ThenByDescending(widget => widget.Priority)
            .ToList();

        var finalWidth = CalculateCombinedWidth(selected, spacing, separatorWidth);
        if (showsStatus)
        {
            finalWidth += (selected.Count == 0 ? 0 : spacing) + statusWidth;
        }

        return new DashboardLayoutResult(
            selected.Select(widget => widget.Id).ToArray(),
            Math.Min(availableWidth, finalWidth),
            hidden.Count,
            showsStatus)
        {
            HiddenWidgetIds = hidden.Select(widget => widget.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static int CalculateCombinedWidth(IReadOnlyList<DashboardWidgetMeasure> widgets, int spacing, int separatorWidth)
    {
        var width = widgets.Sum(widget => widget.Width) + spacing * Math.Max(0, widgets.Count - 1);
        if (separatorWidth > 0 &&
            widgets.Any(widget => string.Equals(widget.Id, DashboardWidgetIds.Clock, StringComparison.OrdinalIgnoreCase)) &&
            widgets.Any(widget => string.Equals(widget.Id, DashboardWidgetIds.BluetoothBattery, StringComparison.OrdinalIgnoreCase)))
        {
            width += spacing + separatorWidth;
        }

        return width;
    }

    private static bool IsClockBatteryPair(string first, string second) =>
        (string.Equals(first, DashboardWidgetIds.Clock, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(second, DashboardWidgetIds.BluetoothBattery, StringComparison.OrdinalIgnoreCase)) ||
        (string.Equals(first, DashboardWidgetIds.BluetoothBattery, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(second, DashboardWidgetIds.Clock, StringComparison.OrdinalIgnoreCase));
}

internal sealed class DashboardSeparator : Control
{
    private const int SeparatorInsetDip = 8;

    public DashboardSeparator()
    {
        BackColor = Color.Magenta;
        TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public static int GetWidthPixels(int dpi) => Math.Max(1, ClockWidget.DipToPixels(1, dpi));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var scale = Math.Max(1, DeviceDpi) / 96f;
        var inset = ClockWidget.DipToPixels(SeparatorInsetDip, Math.Max(1, DeviceDpi));
        using var pen = new Pen(Color.FromArgb(82, 86, 92), Math.Max(1f, scale));
        var x = Math.Max(0, (Width - 1) / 2);
        e.Graphics.DrawLine(pen, x, inset, x, Math.Max(inset, Height - inset));
    }
}

internal sealed class DashboardClockLabel : Label
{
    private const float MinimumClockFontSize = 11f;
    private Font? _clockFont;
    private bool _taskbarStyled;
    private int _preparedHeightPixels = -1;
    private int _preparedDpi = -1;
    private int _preparedAvailableWidthPixels = -1;
    private float _preparedPreferredFontSize = -1;
    private int _clockTextWidth;
    private int _clockTextHeight;
    private int _horizontalPaddingPixels;
    private int _preferredWidthPixels;

    public DashboardClockLabel()
    {
        AutoSize = false;
        BackColor = Color.Transparent;
        TextAlign = ContentAlignment.MiddleCenter;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public bool TaskbarStyled
    {
        get => _taskbarStyled;
        set
        {
            if (_taskbarStyled == value)
            {
                return;
            }

            _taskbarStyled = value;
            Invalidate();
        }
    }

    public void PrepareTaskbarTypography(int heightPixels, int dpi, int availableWidthPixels, float preferredFontSize)
    {
        dpi = dpi > 0 ? dpi : 96;
        if (_preparedHeightPixels == heightPixels &&
            _preparedDpi == dpi &&
            _preparedAvailableWidthPixels == availableWidthPixels &&
            Math.Abs(_preparedPreferredFontSize - preferredFontSize) < 0.01f)
        {
            return;
        }

        var maximumPointSize = Math.Clamp(11.5f + preferredFontSize * 0.2f, 13.5f, 19f);
        var verticalPadding = ClockWidget.DipToPixels(4, dpi);
        var horizontalPadding = ClockWidget.DipToPixels(3, dpi);
        Font? clockFont = null;
        var clockMeasure = Size.Empty;

        for (var pointSize = maximumPointSize; pointSize >= MinimumClockFontSize; pointSize -= 0.5f)
        {
            var candidate = new Font("Consolas", pointSize, FontStyle.Bold, GraphicsUnit.Point);
            var candidateMeasure = MeasureClockText(candidate);
            var candidateWidth = horizontalPadding * 2 + candidateMeasure.Width;

            if (candidateWidth <= availableWidthPixels && candidateMeasure.Height + verticalPadding * 2 <= heightPixels)
            {
                clockFont = candidate;
                clockMeasure = candidateMeasure;
                break;
            }

            candidate.Dispose();
        }

        if (clockFont is null)
        {
            clockFont = new Font("Consolas", MinimumClockFontSize, FontStyle.Bold, GraphicsUnit.Point);
            clockMeasure = MeasureClockText(clockFont);
        }

        _clockFont?.Dispose();
        _clockFont = clockFont;
        _clockTextWidth = clockMeasure.Width;
        _clockTextHeight = clockMeasure.Height;
        _horizontalPaddingPixels = horizontalPadding;
        _preferredWidthPixels = horizontalPadding * 2 + _clockTextWidth;
        _preparedHeightPixels = heightPixels;
        _preparedDpi = dpi;
        _preparedAvailableWidthPixels = availableWidthPixels;
        _preparedPreferredFontSize = preferredFontSize;
        Invalidate();
    }

    public int GetPreferredWidthPixels(int dpi)
    {
        if (_preferredWidthPixels <= 0)
        {
            PrepareTaskbarTypography(Math.Max(24, Height), dpi, 1000, 22f);
        }

        return _preferredWidthPixels;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_taskbarStyled || _clockFont is null)
        {
            base.OnPaint(e);
            return;
        }

        var textWidth = Math.Min(_clockTextWidth, Math.Max(0, ClientSize.Width - _horizontalPaddingPixels * 2));
        var left = Math.Max(_horizontalPaddingPixels, (ClientSize.Width - textWidth) / 2);
        var top = Math.Max(0, (ClientSize.Height - _clockTextHeight) / 2);

        // ClearType blends colored subpixels against the magenta transparency key,
        // which leaves pink fringes on the taskbar. Use single-bit rendering so
        // visible clock pixels stay truly white and keyed pixels stay transparent.
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        using var brush = new SolidBrush(Color.White);
        e.Graphics.DrawString(
            Text,
            _clockFont,
            brush,
            new RectangleF(left, top, textWidth, _clockTextHeight),
            format);
    }

    private static Size MeasureClockText(Font font)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        var measure = graphics.MeasureString("88:88:88", font, int.MaxValue, format);
        return new Size((int)Math.Ceiling(measure.Width), (int)Math.Ceiling(measure.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clockFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class ClockWidget : IDashboardWidget, IDisposable
{
    private const float MinimumFontSize = 10f;
    private readonly Label _label;
    private float _preferredFontSize = 22f;
    private int _horizontalPadding = 8;
    private int _verticalPadding = 2;
    private int _preparedHeightPixels = -1;
    private int _preparedDpi = -1;
    private int _preparedAvailableWidthPixels = -1;
    private float _preparedPreferredFontSize = -1;
    private string? _preparedText;

    public ClockWidget(Label label)
    {
        _label = label;
        _label.AccessibleName = "Clock, current local time";
        _label.AutoSize = false;
        _label.TextAlign = ContentAlignment.MiddleCenter;
    }

    public string Id => DashboardWidgetIds.Clock;

    public int Priority => 100;

    public Control View => _label;

    public Label Label => _label;

    public void SetPreferredFontSize(float size)
    {
        if (Math.Abs(_preferredFontSize - size) >= 0.01f)
        {
            _preferredFontSize = size;
            _preparedHeightPixels = -1;
        }
    }

    public void Prepare(int heightPixels, int dpi, int availableWidthPixels)
    {
        if (_label is DashboardClockLabel taskbarLabel && taskbarLabel.TaskbarStyled)
        {
            taskbarLabel.PrepareTaskbarTypography(heightPixels, dpi, availableWidthPixels, _preferredFontSize);
            return;
        }

        if (_preparedHeightPixels == heightPixels &&
            _preparedDpi == dpi &&
            _preparedAvailableWidthPixels == availableWidthPixels &&
            Math.Abs(_preparedPreferredFontSize - _preferredFontSize) < 0.01f &&
            string.Equals(_preparedText, _label.Text, StringComparison.Ordinal))
        {
            return;
        }

        _horizontalPadding = DipToPixels(5, dpi);
        _verticalPadding = DipToPixels(2, dpi);
        var maximumTextHeight = heightPixels - _verticalPadding * 2;
        Font? replacementFont = null;

        for (var size = _preferredFontSize; size >= MinimumFontSize; size -= 0.5f)
        {
            var candidate = new Font("Consolas", size, FontStyle.Bold, GraphicsUnit.Point);
            var measure = TextRenderer.MeasureText(_label.Text, candidate, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (measure.Height <= maximumTextHeight && measure.Width + _horizontalPadding * 2 <= availableWidthPixels)
            {
                if (string.Equals(_label.Font.FontFamily.Name, candidate.FontFamily.Name, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(_label.Font.Size - candidate.Size) < 0.01f &&
                    _label.Font.Style == candidate.Style)
                {
                    candidate.Dispose();
                }
                else
                {
                    replacementFont = candidate;
                }

                break;
            }

            candidate.Dispose();
        }

        if (replacementFont is not null)
        {
            var previous = _label.Font;
            _label.Font = replacementFont;
            previous.Dispose();
        }

        var padding = new Padding(_horizontalPadding, _verticalPadding, _horizontalPadding, _verticalPadding);
        if (_label.Padding != padding)
        {
            _label.Padding = padding;
        }

        _preparedHeightPixels = heightPixels;
        _preparedDpi = dpi;
        _preparedAvailableWidthPixels = availableWidthPixels;
        _preparedPreferredFontSize = _preferredFontSize;
        _preparedText = _label.Text;
    }

    public int GetPreferredWidthPixels(int dpi, bool compact)
    {
        if (_label is DashboardClockLabel taskbarLabel && taskbarLabel.TaskbarStyled)
        {
            return taskbarLabel.GetPreferredWidthPixels(dpi);
        }

        var measured = TextRenderer.MeasureText(_label.Text, _label.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return measured.Width + _horizontalPadding * 2;
    }

    public void ApplyBounds(Rectangle bounds, int dpi, bool compact)
    {
        _label.Bounds = bounds;
        _label.TextAlign = ContentAlignment.MiddleCenter;
    }

    public void Dispose()
    {
        // The label and its font are owned by the parent form.
    }

    internal static int DipToPixels(int dips, int dpi) => Math.Max(0, (int)Math.Ceiling((double)dips * dpi / 96));
}

internal sealed class BluetoothBatteryWidget : IDashboardWidget, IDisposable
{
    private readonly Panel _panel = new()
    {
        BackColor = Color.Magenta,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
        TabStop = false
    };
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 10_000, InitialDelay = 450, ReshowDelay = 100, ShowAlways = true };
    private readonly Dictionary<string, BatteryDeviceIndicator> _indicatorsByStableId = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<BatteryDeviceIndicator> _indicators = Array.Empty<BatteryDeviceIndicator>();
    private IReadOnlyList<BluetoothBatteryDevice> _devices = Array.Empty<BluetoothBatteryDevice>();
    private IReadOnlyList<BluetoothBatteryDevice> _visibleDevices = Array.Empty<BluetoothBatteryDevice>();
    private Dictionary<string, bool> _visibility = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private int _lowThreshold = 30;
    private int _criticalThreshold = 15;
    private int _spacingDip = 12;
    private bool _percentageOnly;

    public string Id => DashboardWidgetIds.BluetoothBattery;

    public int Priority => 50;

    public Control View => _panel;

    public IReadOnlyList<BluetoothBatteryDevice> Devices => _devices;

    public bool UpdateDevices(IReadOnlyList<BluetoothBatteryDevice> devices, OverlaySettings settings)
    {
        var previousLayoutKey = CreateLayoutKey(_visibleDevices, _aliases, _percentageOnly);
        var previousVisibleIds = _visibleDevices.Select(device => device.StableId).ToArray();
        _devices = devices.ToArray();
        _visibility = new Dictionary<string, bool>(settings.DeviceVisibility, StringComparer.OrdinalIgnoreCase);
        _aliases = new Dictionary<string, string>(settings.DeviceAliases, StringComparer.OrdinalIgnoreCase);
        _lowThreshold = Math.Clamp(settings.LowBatteryThresholdPercent, 1, 100);
        _criticalThreshold = Math.Clamp(settings.CriticalBatteryThresholdPercent, 0, _lowThreshold - 1);
        _percentageOnly = settings.BatteryPercentageOnly;

        _visibleDevices = _devices
            .Where(device => !_visibility.TryGetValue(device.StableId, out var visible) || visible)
            .OrderBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var presentIds = _devices.Select(device => device.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var staleId in _indicatorsByStableId.Keys.Where(id => !presentIds.Contains(id)).ToArray())
        {
            var staleIndicator = _indicatorsByStableId[staleId];
            _toolTip.SetToolTip(staleIndicator, null);
            _panel.Controls.Remove(staleIndicator);
            staleIndicator.Dispose();
            _indicatorsByStableId.Remove(staleId);
        }

        foreach (var device in _devices)
        {
            if (!_indicatorsByStableId.TryGetValue(device.StableId, out var indicator))
            {
                indicator = new BatteryDeviceIndicator
                {
                    ForeColor = Color.White,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty
                };
                _indicatorsByStableId.Add(device.StableId, indicator);
            }

            var configuredAlias = _aliases.TryGetValue(device.StableId, out var alias) ? alias : null;
            var shortName = BatteryDeviceLabelFormatter.ResolveShortName(device.FriendlyName, configuredAlias);
            indicator.Update(device, shortName, _lowThreshold, _criticalThreshold, _percentageOnly);
            _toolTip.SetToolTip(indicator, BatteryDeviceLabelFormatter.FormatTooltip(device, shortName));
        }

        var nextVisibleIds = _visibleDevices.Select(device => device.StableId).ToArray();
        _indicators = nextVisibleIds.Select(id => _indicatorsByStableId[id]).ToArray();
        if (!previousVisibleIds.SequenceEqual(nextVisibleIds, StringComparer.OrdinalIgnoreCase))
        {
            _panel.SuspendLayout();
            foreach (Control control in _panel.Controls.Cast<Control>().ToArray())
            {
                _panel.Controls.Remove(control);
            }

            foreach (var indicator in _indicators)
            {
                _panel.Controls.Add(indicator);
            }

            _panel.ResumeLayout(performLayout: false);
        }

        var nextLayoutKey = CreateLayoutKey(_visibleDevices, _aliases, _percentageOnly);
        return !string.Equals(previousLayoutKey, nextLayoutKey, StringComparison.Ordinal);
    }

    public int GetPreferredWidthPixels(int dpi, bool compact)
    {
        var spacing = ClockWidget.DipToPixels(compact ? 8 : _spacingDip, dpi);
        return _visibleDevices.Count == 0
            ? 0
            : _indicators.Sum(indicator => indicator.GetPreferredWidthPixels(dpi, _percentageOnly)) + Math.Max(0, _indicators.Count - 1) * spacing;
    }

    public void ApplyBounds(Rectangle bounds, int dpi, bool compact)
    {
        _spacingDip = compact ? 8 : 12;
        var spacing = ClockWidget.DipToPixels(_spacingDip, dpi);
        var x = 0;
        for (var index = 0; index < _indicators.Count; index++)
        {
            var indicator = _indicators[index];
            var itemWidth = indicator.GetPreferredWidthPixels(dpi, _percentageOnly);
            var itemBounds = new Rectangle(x, 0, itemWidth, bounds.Height);
            if (indicator.Bounds != itemBounds)
            {
                indicator.Bounds = itemBounds;
            }

            x += itemWidth + spacing;
        }

        if (_panel.Bounds != bounds)
        {
            _panel.Bounds = bounds;
        }
    }

    public void Dispose()
    {
        _toolTip.Dispose();
        _panel.Dispose();
    }

    private static string CreateLayoutKey(
        IReadOnlyList<BluetoothBatteryDevice> devices,
        IReadOnlyDictionary<string, string> aliases,
        bool percentageOnly)
    {
        var names = devices.Select(device =>
        {
            var alias = aliases.TryGetValue(device.StableId, out var value) ? value : null;
            var shortName = BatteryDeviceLabelFormatter.ResolveShortName(device.FriendlyName, alias);
            return $"{device.StableId}={shortName}";
        });
        return $"percentage-only={percentageOnly}|{string.Join(";", names)}";
    }
}

internal sealed class BatteryDeviceIndicator : Control
{
    private const int MaximumNameWidthDip = 86;
    private const int PillHeightDip = 26;
    private const int PillPaddingDip = 5;
    private const int PillRadiusDip = 8;
    private const int BatteryIconBodyWidthDip = 16;
    private const int BatteryIconCapWidthDip = 2;
    private const int BatteryIconCapGapDip = 1;
    private const int TextGapDip = 6;
    private readonly Font _nameFont = new("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _percentageFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private BluetoothBatteryDevice? _device;
    private string _displayName = "Bluetooth device";
    private int _lowThreshold = 30;
    private int _criticalThreshold = 15;
    private bool _percentageOnly;
    private int _cachedWidthDpi;
    private string? _cachedWidthName;
    private bool _cachedWidthPercentageOnly;
    private int _cachedPreferredWidth;
    private int _cachedPercentageColumnWidth;
    private int _pillRegionDpi = -1;
    private Size _pillRegionSize = Size.Empty;

    public BatteryDeviceIndicator()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.Indicator;
    }

    public void Update(
        BluetoothBatteryDevice device,
        string displayName,
        int lowThreshold,
        int criticalThreshold,
        bool percentageOnly)
    {
        var visualChanged = _device?.BatteryPercent != device.BatteryPercent ||
            _device?.State != device.State ||
            !string.Equals(_displayName, displayName, StringComparison.Ordinal) ||
            _lowThreshold != lowThreshold ||
            _criticalThreshold != criticalThreshold ||
            _percentageOnly != percentageOnly;
        _device = device;
        _displayName = displayName;
        _lowThreshold = lowThreshold;
        _criticalThreshold = criticalThreshold;
        _percentageOnly = percentageOnly;
        var state = device.State switch
        {
            BatteryReadingState.Disconnected => "disconnected",
            BatteryReadingState.Unavailable => "battery percentage unavailable",
            _ => "connected"
        };
        var percentage = BatteryDeviceLabelFormatter.FormatPercentage(device).Trim();
        var readTime = device.LastReadAt.HasValue ? $" Windows PnP read at {device.LastReadAt.Value.ToLocalTime():HH:mm:ss}." : string.Empty;
        AccessibleName = $"{displayName}, {device.FriendlyName}, {state}, {percentage}.{readTime}";
        AccessibleDescription = $"Bluetooth device {device.FriendlyName}; stable identifier {device.StableId}; displayed as {displayName}; {state}.{readTime}";

        if (visualChanged)
        {
            Invalidate();
        }
    }

    public int GetPreferredWidthPixels(int dpi, bool percentageOnly)
    {
        dpi = dpi > 0 ? dpi : 96;
        if (_cachedWidthDpi == dpi && _cachedWidthPercentageOnly == percentageOnly &&
            string.Equals(_cachedWidthName, _displayName, StringComparison.Ordinal))
        {
            return _cachedPreferredWidth;
        }

        var padding = ClockWidget.DipToPixels(PillPaddingDip, dpi);
        var iconContentWidth = GetBatteryIconWidthPixels(dpi);
        var gap = ClockWidget.DipToPixels(TextGapDip, dpi);
        var percentageWidth = MeasureTextPixels("100%", _percentageFont, dpi).Width + Math.Max(1, ClockWidget.DipToPixels(1, dpi));
        var nameWidth = 0;
        if (!percentageOnly)
        {
            var measuredNameWidth = MeasureTextPixels(_displayName, _nameFont, dpi).Width;
            nameWidth = Math.Min(measuredNameWidth, ClockWidget.DipToPixels(MaximumNameWidthDip, dpi));
        }

        _cachedWidthDpi = dpi;
        _cachedWidthName = _displayName;
        _cachedWidthPercentageOnly = percentageOnly;
        _cachedPercentageColumnWidth = percentageWidth;
        _cachedPreferredWidth = padding * 2 + iconContentWidth + gap + percentageWidth +
            (percentageOnly ? 0 : nameWidth + gap);
        return _cachedPreferredWidth;
    }

    private static Size MeasureTextPixels(string text, Font font, int dpi)
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        var measured = graphics.MeasureString(text, font, int.MaxValue, format);
        return new Size((int)Math.Ceiling(measured.Width), (int)Math.Ceiling(measured.Height));
    }

    private static int GetBatteryIconWidthPixels(int dpi) =>
        ClockWidget.DipToPixels(BatteryIconBodyWidthDip, dpi) +
        ClockWidget.DipToPixels(BatteryIconCapGapDip, dpi) +
        ClockWidget.DipToPixels(BatteryIconCapWidthDip, dpi);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdatePillRegion();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdatePillRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.None;
        e.Graphics.PageUnit = GraphicsUnit.Pixel;
        // Avoid ClearType subpixel tinting over the magenta transparency key.
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

        var percent = _device?.State == BatteryReadingState.Available ? _device.BatteryPercent : null;
        var color = percent.HasValue && percent.Value < _criticalThreshold
            ? Color.FromArgb(232, 145, 132)
            : percent.HasValue && percent.Value < _lowThreshold
                ? Color.FromArgb(232, 193, 126)
                : percent.HasValue
                    ? Color.FromArgb(177, 216, 186)
                    : Color.White;

        var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        var scale = Math.Max(0.75f, dpi / 96f);
        var pillBounds = GetPillBounds(dpi);
        using (var pillPath = CreateRoundedRectangle(pillBounds, ClockWidget.DipToPixels(PillRadiusDip, dpi)))
        using (var pillFill = new SolidBrush(Color.FromArgb(42, 45, 52)))
        using (var pillOutline = new Pen(Color.FromArgb(68, 74, 84), Math.Max(1f, scale)))
        {
            e.Graphics.FillPath(pillFill, pillPath);
            e.Graphics.DrawPath(pillOutline, pillPath);
        }

        var iconWidth = Math.Max(14, ClockWidget.DipToPixels(BatteryIconBodyWidthDip, dpi));
        var iconHeight = Math.Max(8, ClockWidget.DipToPixels(9, dpi));
        var iconX = ClockWidget.DipToPixels(PillPaddingDip, dpi);
        var iconY = (Height - iconHeight) / 2;
        var iconCapGap = ClockWidget.DipToPixels(BatteryIconCapGapDip, dpi);
        var iconCapWidth = Math.Max(2, ClockWidget.DipToPixels(BatteryIconCapWidthDip, dpi));
        var iconContentWidth = iconWidth + iconCapGap + iconCapWidth;
        // Keep this outline opaque so the transparency-key color cannot tint it.
        using var outline = new Pen(Color.FromArgb(190, 197, 207), Math.Max(1f, scale));
        using var fill = new SolidBrush(color);
        var body = new RectangleF(iconX, iconY, iconWidth, iconHeight);
        using (var bodyPath = CreateRoundedRectangle(body, Math.Max(1f, scale * 2)))
        {
            e.Graphics.DrawPath(outline, bodyPath);
        }

        var cap = new RectangleF(body.Right + iconCapGap, body.Y + body.Height * 0.3f, iconCapWidth, body.Height * 0.4f);
        using (var capPath = CreateRoundedRectangle(cap, Math.Max(1f, scale)))
        {
            e.Graphics.DrawPath(outline, capPath);
        }

        var fillWidth = percent.HasValue ? Math.Max(0, (int)Math.Floor((iconWidth - 4) * percent.Value / 100d)) : 0;
        if (fillWidth > 0)
        {
            var fillBounds = new RectangleF(iconX + 2, iconY + 2, fillWidth, Math.Max(1, iconHeight - 4));
            using var fillPath = CreateRoundedRectangle(fillBounds, Math.Max(1f, scale));
            e.Graphics.FillPath(fill, fillPath);
        }

        var gap = ClockWidget.DipToPixels(TextGapDip, dpi);
        var padding = ClockWidget.DipToPixels(PillPaddingDip, dpi);
        var percentageWidth = _cachedWidthDpi == dpi && _cachedPercentageColumnWidth > 0
            ? _cachedPercentageColumnWidth
            : MeasureTextPixels("100%", _percentageFont, dpi).Width + Math.Max(1, ClockWidget.DipToPixels(1, dpi));
        var textStart = iconX + iconContentWidth + gap;
        var percentageRect = new Rectangle(Math.Max(textStart, Width - padding - percentageWidth), 0, Math.Min(percentageWidth, Width), Height);
        var percentageText = _device is null ? "—" : BatteryDeviceLabelFormatter.FormatPercentage(_device);
        using (var percentageFormat = (StringFormat)StringFormat.GenericTypographic.Clone())
        using (var percentageBrush = new SolidBrush(color))
        {
            percentageFormat.Alignment = StringAlignment.Far;
            percentageFormat.LineAlignment = StringAlignment.Center;
            percentageFormat.FormatFlags |= StringFormatFlags.NoWrap;
            e.Graphics.DrawString(
                percentageText,
                _percentageFont,
                percentageBrush,
                new RectangleF(percentageRect.X, percentageRect.Y, percentageRect.Width, percentageRect.Height),
                percentageFormat);
        }

        if (!_percentageOnly)
        {
            var nameLeft = textStart;
            var nameRight = percentageRect.Left - gap;
            var nameRect = new Rectangle(nameLeft, 0, Math.Max(0, nameRight - nameLeft), Height);
            using var nameFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
            using var nameBrush = new SolidBrush(Color.White);
            nameFormat.Alignment = StringAlignment.Near;
            nameFormat.LineAlignment = StringAlignment.Center;
            nameFormat.Trimming = StringTrimming.EllipsisCharacter;
            nameFormat.FormatFlags |= StringFormatFlags.NoWrap;
            e.Graphics.DrawString(
                _displayName,
                _nameFont,
                nameBrush,
                new RectangleF(nameRect.X, nameRect.Y, nameRect.Width, nameRect.Height),
                nameFormat);
        }
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        radius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f);
        var diameter = Math.Max(1f, radius * 2f);
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private RectangleF GetPillBounds(int dpi)
    {
        var pillHeight = Math.Min(Height, ClockWidget.DipToPixels(PillHeightDip, dpi));
        return new RectangleF(
            0,
            Math.Max(0, (Height - pillHeight) / 2f),
            Math.Max(1, Width),
            Math.Max(1, pillHeight));
    }

    private void UpdatePillRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        if (_pillRegionDpi == dpi && _pillRegionSize == Size)
        {
            return;
        }

        using var path = CreateRoundedRectangle(GetPillBounds(dpi), ClockWidget.DipToPixels(PillRadiusDip, dpi));
        var nextRegion = new Region(path);
        var previousRegion = Region;
        Region = nextRegion;
        previousRegion?.Dispose();
        _pillRegionDpi = dpi;
        _pillRegionSize = Size;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _nameFont.Dispose();
            _percentageFont.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class DashboardStatusIndicator : Control
{
    public DashboardStatusIndicator()
    {
        BackColor = Color.Magenta;
        ForeColor = Color.FromArgb(255, 195, 65);
        AccessibleRole = AccessibleRole.Indicator;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetHiddenWidgets(IReadOnlyList<string> ids)
    {
        Text = $"{ids.Count}!";
        AccessibleName = $"Dashboard space is limited. Hidden widgets: {string.Join(", ", ids)}.";
        AccessibleDescription = AccessibleName;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}
