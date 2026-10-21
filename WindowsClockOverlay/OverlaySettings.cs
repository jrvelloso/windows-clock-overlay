using System.Text.Json.Serialization;

namespace WindowsClockOverlay;

internal sealed class OverlaySettings
{
    public int? PositionX { get; set; }
    public int? PositionY { get; set; }
    public int ForegroundColorArgb { get; set; } = Color.Lime.ToArgb();
    public float FontSize { get; set; } = 22f;
    public int TaskbarDashboardWidthDip { get; set; } = 180;
    public int TaskbarHorizontalOffsetDip { get; set; }
    public int TaskbarSafeAreaMarginDip { get; set; } = 8;
    public int TaskbarFarLeftFallbackInsetDip { get; set; } = 180;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskbarHorizontalAlignment HorizontalAlignment { get; set; } = TaskbarHorizontalAlignment.Left;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskbarPlacementRegion PlacementRegion { get; set; } = TaskbarPlacementRegion.FarLeft;
    public List<string> EnabledWidgets { get; set; } = [DashboardWidgetIds.Clock, DashboardWidgetIds.BluetoothBattery];
    public List<string> WidgetOrder { get; set; } = [DashboardWidgetIds.Clock, DashboardWidgetIds.BluetoothBattery];
    public Dictionary<string, bool> DeviceVisibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DeviceAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int BatteryRefreshIntervalSeconds { get; set; } = 30;
    public int LowBatteryThresholdPercent { get; set; } = 30;
    public int CriticalBatteryThresholdPercent { get; set; } = 15;
    public bool CompactLayout { get; set; } = true;
    public bool BatteryPercentageOnly { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ClockDisplayMode DisplayMode { get; set; } = ClockDisplayMode.TaskbarAnchored;

    [JsonIgnore]
    public bool ShouldMigrateDisplayMode { get; set; }

    [JsonIgnore]
    public bool ShouldMigrateHorizontalLayout { get; set; }

    public OverlaySettings Copy()
    {
        return new OverlaySettings
        {
            PositionX = PositionX,
            PositionY = PositionY,
            ForegroundColorArgb = ForegroundColorArgb,
            FontSize = FontSize,
            TaskbarDashboardWidthDip = TaskbarDashboardWidthDip,
            TaskbarHorizontalOffsetDip = TaskbarHorizontalOffsetDip,
            TaskbarSafeAreaMarginDip = TaskbarSafeAreaMarginDip,
            TaskbarFarLeftFallbackInsetDip = TaskbarFarLeftFallbackInsetDip,
            HorizontalAlignment = HorizontalAlignment,
            PlacementRegion = PlacementRegion,
            DisplayMode = DisplayMode,
            EnabledWidgets = EnabledWidgets.ToList(),
            WidgetOrder = WidgetOrder.ToList(),
            DeviceVisibility = new Dictionary<string, bool>(DeviceVisibility, StringComparer.OrdinalIgnoreCase),
            DeviceAliases = new Dictionary<string, string>(DeviceAliases, StringComparer.OrdinalIgnoreCase),
            BatteryRefreshIntervalSeconds = BatteryRefreshIntervalSeconds,
            LowBatteryThresholdPercent = LowBatteryThresholdPercent,
            CriticalBatteryThresholdPercent = CriticalBatteryThresholdPercent,
            CompactLayout = CompactLayout,
            BatteryPercentageOnly = BatteryPercentageOnly,
            ShouldMigrateDisplayMode = ShouldMigrateDisplayMode,
            ShouldMigrateHorizontalLayout = ShouldMigrateHorizontalLayout
        };
    }
}
