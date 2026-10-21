using WindowsClockOverlay;
using Xunit;

namespace WindowsClockOverlay.Tests;

public sealed class DashboardAndBluetoothTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(100, 100)]
    public void BatteryParser_AcceptsWindowsBytePercentage(int input, int expected)
    {
        var valid = BluetoothBatteryPropertyParser.TryParse(
            BluetoothBatteryPropertyParser.DevPropTypeByte,
            [(byte)input],
            out var percentage);

        Assert.True(valid);
        Assert.Equal(expected, percentage);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 0)]
    [InlineData(3, 101)]
    [InlineData(18, 50)]
    public void BatteryParser_RejectsInvalidTypeLengthAndRange(uint propertyType, int input)
    {
        var data = input == 0 && propertyType == 0 ? Array.Empty<byte>() : [(byte)input];

        Assert.False(BluetoothBatteryPropertyParser.TryParse(propertyType, data, out _));
    }

    [Fact]
    public void Deduplicator_MergesBluetoothProfilesByAddressAndPrefersBatteryNode()
    {
        var readAt = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);
        var nodes = new[]
        {
            new BluetoothPnPNode("BTHENUM\\DEV_9402305572B9\\PROFILE", "Zone 300", null, true, 0, null),
            new BluetoothPnPNode("BTHENUM\\SERVICE\\8&ABC&0&9402305572B9_C00000000", "Zone 300 Hands-Free AG", null, true,
                BluetoothBatteryPropertyParser.DevPropTypeByte, [40])
        };

        var devices = BluetoothDeviceDeduplicator.Deduplicate(nodes, readAt);

        var device = Assert.Single(devices);
        Assert.Equal("bt-address:9402305572B9", device.StableId);
        Assert.Equal("Zone 300", device.FriendlyName);
        Assert.Equal(40, device.BatteryPercent);
        Assert.Equal(BatteryReadingState.Available, device.State);
        Assert.Equal(readAt, device.LastReadAt);
    }

    [Fact]
    public void Deduplicator_UsesContainerIdAsStablePhysicalDeviceIdentity()
    {
        var containerId = "63a94f18-8f26-4e24-a408-2c3187ee44e7";
        var nodes = new[]
        {
            new BluetoothPnPNode("BTHENUM\\PROFILE-A", "Headset profile", containerId, true, 0, null),
            new BluetoothPnPNode("BTHENUM\\PROFILE-B", "Headset battery", containerId, true,
                BluetoothBatteryPropertyParser.DevPropTypeByte, [64])
        };

        var device = Assert.Single(BluetoothDeviceDeduplicator.Deduplicate(nodes, DateTimeOffset.UtcNow));

        Assert.Equal("bt-container:63a94f188f264e24a4082c3187ee44e7", device.StableId);
        Assert.Equal(64, device.BatteryPercent);
    }

    [Theory]
    [InlineData("Zone 300 Hands-Free AG", null, "Zone 300")]
    [InlineData("Logitech MX Master 3S", null, "MX 3S")]
    [InlineData("Logitech LIFT", null, "Lift")]
    [InlineData("K68 BT5.0", null, "K68")]
    [InlineData("Logitech MX Master 3S", "Desk mouse", "Desk mouse")]
    public void BatteryLabels_UseDefaultNamesAndConfiguredAlias(string name, string? alias, string expected)
    {
        Assert.Equal(expected, BatteryDeviceLabelFormatter.ResolveShortName(name, alias));
    }

    [Fact]
    public void BatteryLabels_FormatMissingDataAsEmDashAndKeepPercentageColumnStable()
    {
        var widths = Enumerable.Range(0, 101)
            .Select(percent => BatteryDeviceLabelFormatter.FormatPercentage(Device(percent)))
            .Select(text => text.Length)
            .Distinct()
            .ToArray();

        Assert.Equal([4], widths);
        Assert.Equal("  0%", BatteryDeviceLabelFormatter.FormatPercentage(Device(0)));
        Assert.Equal("100%", BatteryDeviceLabelFormatter.FormatPercentage(Device(100)));
        Assert.Equal("—", BatteryDeviceLabelFormatter.FormatPercentage(Device(null, BatteryReadingState.Unavailable)));
    }

    [Fact]
    public void BatteryTooltip_IdentifiesFullDeviceAndUnavailableReading()
    {
        var device = Device(null, BatteryReadingState.Unavailable);

        var tooltip = BatteryDeviceLabelFormatter.FormatTooltip(device, "K68");

        Assert.Contains("K68", tooltip);
        Assert.Contains(device.FriendlyName, tooltip);
        Assert.Contains("Battery reading unavailable", tooltip);
        Assert.Contains("—", tooltip);
    }

    [Fact]
    public void BatteryIndicator_WidthDoesNotChangeWithReadingAndScalesAtCommonDpis()
    {
        using var indicator = new BatteryDeviceIndicator();
        var widthsByDpi = new Dictionary<int, int>();
        foreach (var dpi in new[] { 96, 120, 144 })
        {
            indicator.Update(Device(100), "Zone 300", 30, 15, percentageOnly: false);
            var fullWidth = indicator.GetPreferredWidthPixels(dpi, percentageOnly: false);
            indicator.Update(Device(99), "Zone 300", 30, 15, percentageOnly: false);
            var ninetyNineWidth = indicator.GetPreferredWidthPixels(dpi, percentageOnly: false);
            indicator.Update(Device(1), "Zone 300", 30, 15, percentageOnly: false);
            var lowWidth = indicator.GetPreferredWidthPixels(dpi, percentageOnly: false);
            indicator.Update(Device(null, BatteryReadingState.Unavailable), "Zone 300", 30, 15, percentageOnly: false);
            var unavailableWidth = indicator.GetPreferredWidthPixels(dpi, percentageOnly: false);

            Assert.Equal(fullWidth, ninetyNineWidth);
            Assert.Equal(fullWidth, lowWidth);
            Assert.Equal(fullWidth, unavailableWidth);
            widthsByDpi[dpi] = fullWidth;
        }

        Assert.True(widthsByDpi[120] > widthsByDpi[96]);
        Assert.True(widthsByDpi[144] > widthsByDpi[120]);
    }

    [Fact]
    public void DashboardClockLabel_UsesFixedCompactWidthAcrossSecondChanges()
    {
        using var clock = new DashboardClockLabel { Text = "22:53:01", TaskbarStyled = true };
        clock.PrepareTaskbarTypography(48, 96, 1043, 22f);
        var initialWidth = clock.GetPreferredWidthPixels(96);

        clock.Text = "08:04:59";
        clock.PrepareTaskbarTypography(48, 96, 1043, 22f);

        Assert.Equal(initialWidth, clock.GetPreferredWidthPixels(96));
        Assert.InRange(initialWidth, 80, 180);
    }

    [Fact]
    public async Task BatteryService_ClearsValueOnDisconnectAndCanReconnect()
    {
        var source = new FakeBluetoothDeviceSource();
        var clock = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);
        using var service = new BluetoothBatteryService(source, () => 30, () => clock);
        source.SetNodes([Node("BTHLE\\DEV_D576AB81B64A\\NODE", "K68 BT5.0", 0)]);

        await service.RefreshOnceAsync();
        var first = Assert.Single(service.Snapshot);
        Assert.Equal(0, first.BatteryPercent);
        Assert.Equal(BatteryReadingState.Available, first.State);
        Assert.Equal(clock, first.LastReadAt);

        source.SetNodes([]);
        await service.RefreshOnceAsync();
        var disconnected = Assert.Single(service.Snapshot);
        Assert.Equal(BatteryReadingState.Disconnected, disconnected.State);
        Assert.Null(disconnected.BatteryPercent);
        Assert.Null(disconnected.LastReadAt);

        clock = clock.AddSeconds(30);
        source.SetNodes([Node("BTHLE\\DEV_D576AB81B64A\\NODE", "K68 BT5.0", 72)]);
        await service.RefreshOnceAsync();
        var reconnected = Assert.Single(service.Snapshot);
        Assert.Equal(BatteryReadingState.Available, reconnected.State);
        Assert.Equal(72, reconnected.BatteryPercent);
        Assert.Equal(clock, reconnected.LastReadAt);
    }

    [Fact]
    public async Task BatteryService_OmitsUnrelatedNodesAndClearsMissingPropertyForKnownDevice()
    {
        var source = new FakeBluetoothDeviceSource();
        using var service = new BluetoothBatteryService(source, () => 30);
        source.SetNodes([new BluetoothPnPNode("BTHENUM\\COM3", "Serial link", null, true, 0, null)]);
        await service.RefreshOnceAsync();
        Assert.Empty(service.Snapshot);

        source.SetNodes([Node("BTHLE\\DEV_D576AB81B64A\\NODE", "K68 BT5.0", 50)]);
        await service.RefreshOnceAsync();
        Assert.Equal(50, Assert.Single(service.Snapshot).BatteryPercent);

        source.SetNodes([new BluetoothPnPNode("BTHLE\\DEV_D576AB81B64A\\NODE", "K68 BT5.0", null, true, 0, null)]);
        await service.RefreshOnceAsync();
        var unavailable = Assert.Single(service.Snapshot);
        Assert.Equal(BatteryReadingState.Unavailable, unavailable.State);
        Assert.Null(unavailable.BatteryPercent);
        Assert.Null(unavailable.LastReadAt);
    }

    [Fact]
    public void BatteryService_RepresentsInvalidPropertyAsUnavailableRatherThanZero()
    {
        var devices = BluetoothDeviceDeduplicator.Deduplicate(
        [
            new BluetoothPnPNode("BTHLE\\DEV_D576AB81B64A\\NODE", "K68 BT5.0", null, true,
                BluetoothBatteryPropertyParser.DevPropTypeByte, [255])
        ],
        DateTimeOffset.UtcNow);

        var device = Assert.Single(devices);
        Assert.Equal(BatteryReadingState.Unavailable, device.State);
        Assert.Null(device.BatteryPercent);
        Assert.Null(device.LastReadAt);
        Assert.NotNull(device.Diagnostic);
    }

    [Fact]
    public void LayoutCalculator_PreservesHighPriorityClockAndAddsHiddenStatus()
    {
        var layout = DashboardLayoutCalculator.Calculate(
            availableWidth: 150,
            widgets:
            [
                new DashboardWidgetMeasure(DashboardWidgetIds.BluetoothBattery, 50, 120),
                new DashboardWidgetMeasure(DashboardWidgetIds.Clock, 100, 100)
            ],
            configuredOrder: [DashboardWidgetIds.BluetoothBattery, DashboardWidgetIds.Clock],
            spacing: 6,
            statusWidth: 18);

        Assert.Equal([DashboardWidgetIds.Clock], layout.VisibleWidgetIds);
        Assert.Equal(1, layout.HiddenWidgetCount);
        Assert.True(layout.ShowsHiddenStatus);
        Assert.Equal(124, layout.Width);
        Assert.Contains(DashboardWidgetIds.BluetoothBattery, layout.HiddenWidgetIds);
    }

    [Fact]
    public void LayoutCalculator_UsesConfiguredOrderWhenAllWidgetsFit()
    {
        var layout = DashboardLayoutCalculator.Calculate(
            availableWidth: 250,
            widgets:
            [
                new DashboardWidgetMeasure(DashboardWidgetIds.Clock, 100, 100),
                new DashboardWidgetMeasure(DashboardWidgetIds.BluetoothBattery, 50, 120)
            ],
            configuredOrder: [DashboardWidgetIds.BluetoothBattery, DashboardWidgetIds.Clock],
            spacing: 8,
            statusWidth: 18);

        Assert.Equal([DashboardWidgetIds.BluetoothBattery, DashboardWidgetIds.Clock], layout.VisibleWidgetIds);
        Assert.Equal(228, layout.Width);
        Assert.False(layout.ShowsHiddenStatus);
    }

    [Fact]
    public void LayoutCalculator_ReservesSeparatorBetweenClockAndBatteryWidgets()
    {
        var layout = DashboardLayoutCalculator.Calculate(
            availableWidth: 250,
            widgets:
            [
                new DashboardWidgetMeasure(DashboardWidgetIds.Clock, 100, 100),
                new DashboardWidgetMeasure(DashboardWidgetIds.BluetoothBattery, 50, 120)
            ],
            configuredOrder: [DashboardWidgetIds.Clock, DashboardWidgetIds.BluetoothBattery],
            spacing: 4,
            statusWidth: 18,
            separatorWidth: 1);

        Assert.Equal([DashboardWidgetIds.Clock, DashboardWidgetIds.BluetoothBattery], layout.VisibleWidgetIds);
        Assert.Equal(229, layout.Width);
        Assert.False(layout.ShowsHiddenStatus);
    }

    [Theory]
    [InlineData((int)TaskbarHorizontalAlignment.Left, 2022)]
    [InlineData((int)TaskbarHorizontalAlignment.Center, 2531)]
    [InlineData((int)TaskbarHorizontalAlignment.Right, 3041)]
    public void TaskbarPositionCalculator_AlignsDashboardInsideSafeArea(
        int alignment,
        int expectedLeft)
    {
        var safeArea = new Rectangle(2022, 2112, 1450, 48);

        var dashboard = TaskbarAnchoredManager.CalculateDashboardBounds(
            safeArea,
            431,
            (TaskbarHorizontalAlignment)alignment);

        Assert.Equal(expectedLeft, dashboard.Left);
        Assert.Equal(safeArea.Top, dashboard.Top);
        Assert.Equal(431, dashboard.Width);
        Assert.Equal(safeArea.Height, dashboard.Height);
        Assert.True(dashboard.Left >= safeArea.Left);
        Assert.True(dashboard.Right <= safeArea.Right);
    }

    [Fact]
    public void TaskbarPlacementGeometry_FarLeftUsesWidgetsAndStopsBeforeCenteredTaskList()
    {
        var taskbar = new Rectangle(0, 2112, 3840, 48);
        var widgets = new Rectangle(6, 2112, 152, 48);
        var taskList = new Rectangle(1215, 2112, 793, 48);

        var available = TaskbarPlacementGeometry.TryCalculateSafeBounds(
            TaskbarPlacementRegion.FarLeft,
            taskbar,
            taskList,
            new Rectangle(3518, 2112, 322, 48),
            widgets,
            dpi: 96,
            safeAreaMarginDip: 8,
            farLeftFallbackInsetDip: 180,
            out var safeBounds,
            out var usedFallback,
            out var failureReason);

        Assert.True(available, failureReason);
        Assert.False(usedFallback);
        Assert.Equal(new Rectangle(166, 2112, 1043, 48), safeBounds);
    }

    [Fact]
    public void TaskbarPlacementGeometry_FarLeftFallbackInsetScalesWithDpi()
    {
        var taskbar = new Rectangle(0, 0, 5760, 72);
        var taskList = new Rectangle(1822, 0, 1189, 72);

        var available = TaskbarPlacementGeometry.TryCalculateSafeBounds(
            TaskbarPlacementRegion.FarLeft,
            taskbar,
            taskList,
            Rectangle.Empty,
            widgetsBounds: null,
            dpi: 144,
            safeAreaMarginDip: 8,
            farLeftFallbackInsetDip: 180,
            out var safeBounds,
            out var usedFallback,
            out var failureReason);

        Assert.True(available, failureReason);
        Assert.True(usedFallback);
        Assert.Equal(282, safeBounds.Left);
        Assert.Equal(1813, safeBounds.Right);
        Assert.Equal(72, safeBounds.Height);
    }

    [Fact]
    public void TaskbarPlacementGeometry_RejectsOverlapWithKnownTaskbarControls()
    {
        var taskbar = new Rectangle(0, 2112, 3840, 48);
        var safeBounds = new Rectangle(0, 2112, 3840, 48);
        var dashboard = new Rectangle(140, 2112, 220, 48);

        var fits = TaskbarPlacementGeometry.TryValidateDashboardBounds(
            dashboard,
            taskbar,
            safeBounds,
            [new Rectangle(6, 2112, 152, 48)],
            out var failureReason);

        Assert.False(fits);
        Assert.Contains("overlap", failureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LayoutCalculator_ShowsConstraintStatusWhenNoWidgetFits()
    {
        var layout = DashboardLayoutCalculator.Calculate(
            availableWidth: 20,
            widgets: [new DashboardWidgetMeasure(DashboardWidgetIds.Clock, 100, 100)],
            configuredOrder: [DashboardWidgetIds.Clock],
            spacing: 4,
            statusWidth: 18);

        Assert.Empty(layout.VisibleWidgetIds);
        Assert.Equal(18, layout.Width);
        Assert.True(layout.ShowsHiddenStatus);
        Assert.Equal(1, layout.HiddenWidgetCount);
    }

    [Fact]
    public void FullscreenClassifier_OnlyAcceptsBorderlessDisplayCoveringWindows()
    {
        var display = new Rectangle(0, 0, 3840, 2160);
        var fullDisplay = display;

        Assert.True(TaskbarFullscreenClassifier.IsGenuineFullscreen(fullDisplay, display, false, false, false, false));
        Assert.False(TaskbarFullscreenClassifier.IsGenuineFullscreen(fullDisplay, display, true, false, false, false));
        Assert.False(TaskbarFullscreenClassifier.IsGenuineFullscreen(fullDisplay, display, false, true, false, false));
        Assert.False(TaskbarFullscreenClassifier.IsGenuineFullscreen(fullDisplay, display, false, false, true, false));
        Assert.False(TaskbarFullscreenClassifier.IsGenuineFullscreen(fullDisplay, display, false, false, false, true));
        Assert.False(TaskbarFullscreenClassifier.IsGenuineFullscreen(new Rectangle(0, 0, 3838, 2160), display, false, false, false, false));
    }

    [Fact]
    public void PositioningPolicy_SkipsRedundantSetWindowPosAndCorrectsInvalidState()
    {
        var bounds = new Rectangle(166, 2112, 431, 48);

        Assert.False(TaskbarPositioningPolicy.ShouldSetWindowPos(bounds, bounds, true, true, true));
        Assert.True(TaskbarPositioningPolicy.ShouldSetWindowPos(bounds, new Rectangle(bounds.X + 1, bounds.Y, bounds.Width, bounds.Height), true, true, true));
        Assert.True(TaskbarPositioningPolicy.ShouldSetWindowPos(bounds, bounds, false, true, true));
        Assert.True(TaskbarPositioningPolicy.ShouldSetWindowPos(bounds, bounds, true, false, true));
        Assert.True(TaskbarPositioningPolicy.ShouldSetWindowPos(bounds, bounds, true, true, false));
    }

    [Fact]
    public void VisibilityTracker_OnlyReportsActualStateTransitions()
    {
        var tracker = new DashboardVisibilityTransitionTracker();

        Assert.True(tracker.MoveTo(TaskbarDashboardVisibilityState.Visible));
        Assert.False(tracker.MoveTo(TaskbarDashboardVisibilityState.Visible));
        Assert.True(tracker.MoveTo(TaskbarDashboardVisibilityState.FullscreenSuspended));
        Assert.True(tracker.MoveTo(TaskbarDashboardVisibilityState.Visible));
        Assert.Equal(TaskbarDashboardVisibilityState.Visible, tracker.Current);
    }

    [Fact]
    public void SettingsStore_RoundTripsPhaseTwoSettings()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new OverlaySettingsStore(directory);
            var settings = new OverlaySettings
            {
                DisplayMode = ClockDisplayMode.TaskbarAnchored,
                EnabledWidgets = [DashboardWidgetIds.BluetoothBattery],
                WidgetOrder = [DashboardWidgetIds.BluetoothBattery, DashboardWidgetIds.Clock],
                DeviceVisibility = new Dictionary<string, bool> { ["bt-address:1234567890AB"] = false },
                DeviceAliases = new Dictionary<string, string> { ["bt-address:1234567890AB"] = "Desk keyboard" },
                BatteryRefreshIntervalSeconds = 45,
                LowBatteryThresholdPercent = 35,
                CriticalBatteryThresholdPercent = 12,
                CompactLayout = false,
                BatteryPercentageOnly = true,
                TaskbarHorizontalOffsetDip = 12,
                TaskbarSafeAreaMarginDip = 11,
                HorizontalAlignment = TaskbarHorizontalAlignment.Right,
                PlacementRegion = TaskbarPlacementRegion.RightGap,
                TaskbarFarLeftFallbackInsetDip = 220
            };

            store.Save(settings);
            var loaded = store.Load();

            Assert.Equal(settings.DisplayMode, loaded.DisplayMode);
            Assert.Equal(settings.EnabledWidgets, loaded.EnabledWidgets);
            Assert.Equal(settings.WidgetOrder, loaded.WidgetOrder);
            Assert.Equal(settings.DeviceVisibility, loaded.DeviceVisibility);
            Assert.Equal(settings.DeviceAliases, loaded.DeviceAliases);
            Assert.Equal(45, loaded.BatteryRefreshIntervalSeconds);
            Assert.Equal(35, loaded.LowBatteryThresholdPercent);
            Assert.Equal(12, loaded.CriticalBatteryThresholdPercent);
            Assert.False(loaded.CompactLayout);
            Assert.True(loaded.BatteryPercentageOnly);
            Assert.Equal(12, loaded.TaskbarHorizontalOffsetDip);
            Assert.Equal(11, loaded.TaskbarSafeAreaMarginDip);
            Assert.Equal(TaskbarHorizontalAlignment.Right, loaded.HorizontalAlignment);
            Assert.Equal(TaskbarPlacementRegion.RightGap, loaded.PlacementRegion);
            Assert.Equal(220, loaded.TaskbarFarLeftFallbackInsetDip);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SettingsStore_MigratesLegacySettingsWithBackwardCompatibleDefaults()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new OverlaySettingsStore(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsFilePath)!);
            File.WriteAllText(store.SettingsFilePath, "{\"PositionX\":27,\"ForegroundColorArgb\":-1,\"FontSize\":20}");

            var loaded = store.Load();

            Assert.Equal(27, loaded.PositionX);
            Assert.Equal(TaskbarHorizontalAlignment.Left, loaded.HorizontalAlignment);
            Assert.Equal(8, loaded.TaskbarSafeAreaMarginDip);
            Assert.Equal(TaskbarPlacementRegion.FarLeft, loaded.PlacementRegion);
            Assert.Equal(180, loaded.TaskbarFarLeftFallbackInsetDip);
            Assert.Equal(ClockDisplayMode.TaskbarAnchored, loaded.DisplayMode);
            Assert.True(loaded.ShouldMigrateDisplayMode);
            Assert.True(loaded.ShouldMigrateHorizontalLayout);
            Assert.Equal([DashboardWidgetIds.Clock, DashboardWidgetIds.BluetoothBattery], loaded.EnabledWidgets);
            Assert.Equal(30, loaded.BatteryRefreshIntervalSeconds);
            Assert.Equal(30, loaded.LowBatteryThresholdPercent);
            Assert.Equal(15, loaded.CriticalBatteryThresholdPercent);
            Assert.True(loaded.CompactLayout);
            Assert.False(loaded.BatteryPercentageOnly);

            loaded.ShouldMigrateDisplayMode = false;
            store.Save(loaded);
            var reloaded = store.Load();
            Assert.False(reloaded.ShouldMigrateDisplayMode);
            Assert.False(reloaded.ShouldMigrateHorizontalLayout);
            Assert.Equal(ClockDisplayMode.TaskbarAnchored, reloaded.DisplayMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SettingsStore_MigratesPhaseTwoSettingsWithNewHorizontalDefaults()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new OverlaySettingsStore(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsFilePath)!);
            File.WriteAllText(store.SettingsFilePath, "{\"DisplayMode\":\"TaskbarAnchored\"}");

            var loaded = store.Load();

            Assert.Equal(ClockDisplayMode.TaskbarAnchored, loaded.DisplayMode);
            Assert.False(loaded.ShouldMigrateDisplayMode);
            Assert.True(loaded.ShouldMigrateHorizontalLayout);
            Assert.Equal(TaskbarHorizontalAlignment.Left, loaded.HorizontalAlignment);
            Assert.Equal(8, loaded.TaskbarSafeAreaMarginDip);
            Assert.Equal(TaskbarPlacementRegion.FarLeft, loaded.PlacementRegion);
            Assert.Equal(180, loaded.TaskbarFarLeftFallbackInsetDip);

            store.Save(loaded);
            Assert.False(store.Load().ShouldMigrateHorizontalLayout);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static BluetoothPnPNode Node(string instanceId, string name, byte percentage)
    {
        return new BluetoothPnPNode(instanceId, name, null, true, BluetoothBatteryPropertyParser.DevPropTypeByte, [percentage]);
    }

    private static BluetoothBatteryDevice Device(int? percentage, BatteryReadingState state = BatteryReadingState.Available)
    {
        return new BluetoothBatteryDevice(
            "bt-address:1234567890AB",
            "BTHLE\\DEV_1234567890AB\\NODE",
            "Zone 300 Hands-Free AG",
            state,
            percentage,
            state == BatteryReadingState.Available ? DateTimeOffset.UtcNow : null,
            true);
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowsClockOverlay.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeBluetoothDeviceSource : IBluetoothDeviceSource
    {
        private IReadOnlyList<BluetoothPnPNode> _nodes = Array.Empty<BluetoothPnPNode>();

        public void SetNodes(IReadOnlyList<BluetoothPnPNode> nodes) => _nodes = nodes;

        public IReadOnlyList<BluetoothPnPNode> ReadPresentBluetoothNodes(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _nodes;
        }
    }
}
