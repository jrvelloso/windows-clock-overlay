using System.IO;
using System.Text.Json;

namespace WindowsClockOverlay;

internal sealed class OverlaySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsFilePath;

    public OverlaySettingsStore(string? localAppDataPath = null)
    {
        var basePath = Path.Combine(
            localAppDataPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsClockOverlay");
        _settingsFilePath = Path.Combine(basePath, "settings.json");
    }

    public string SettingsFilePath => _settingsFilePath;

    public OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                var defaults = new OverlaySettings();
                defaults.ShouldMigrateDisplayMode = true;
                DashboardDiagnostics.Write("Settings", $"No settings file at {_settingsFilePath}; using defaults with requested display mode {defaults.DisplayMode}.");
                return defaults;
            }

            var json = File.ReadAllText(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<OverlaySettings>(json) ?? new OverlaySettings();
            var displayModeWasPersisted = false;
            var horizontalAlignmentWasPersisted = false;
            var safeAreaMarginWasPersisted = false;
            var placementRegionWasPersisted = false;
            var farLeftFallbackInsetWasPersisted = false;
            try
            {
                using var document = JsonDocument.Parse(json);
                displayModeWasPersisted = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(nameof(OverlaySettings.DisplayMode), out _);
                horizontalAlignmentWasPersisted = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(nameof(OverlaySettings.HorizontalAlignment), out _);
                safeAreaMarginWasPersisted = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(nameof(OverlaySettings.TaskbarSafeAreaMarginDip), out _);
                placementRegionWasPersisted = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(nameof(OverlaySettings.PlacementRegion), out _);
                farLeftFallbackInsetWasPersisted = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(nameof(OverlaySettings.TaskbarFarLeftFallbackInsetDip), out _);
            }
            catch (JsonException)
            {
                // Deserialization below reports malformed settings through the outer handler.
            }

            if (!Enum.IsDefined(settings.DisplayMode))
            {
                DashboardDiagnostics.Write("Settings", $"Saved display mode '{settings.DisplayMode}' is invalid; defaulting to Taskbar Anchored.");
                settings.DisplayMode = ClockDisplayMode.TaskbarAnchored;
                displayModeWasPersisted = false;
            }

            if (!Enum.IsDefined(settings.HorizontalAlignment))
            {
                DashboardDiagnostics.Write("Settings", $"Saved horizontal alignment '{settings.HorizontalAlignment}' is invalid; defaulting to Left.");
                settings.HorizontalAlignment = TaskbarHorizontalAlignment.Left;
            }

            if (!Enum.IsDefined(settings.PlacementRegion))
            {
                DashboardDiagnostics.Write("Settings", $"Saved placement region '{settings.PlacementRegion}' is invalid; defaulting to FarLeft.");
                settings.PlacementRegion = TaskbarPlacementRegion.FarLeft;
            }

            if (!float.IsFinite(settings.FontSize))
            {
                settings.FontSize = 22f;
            }

            settings.TaskbarDashboardWidthDip = Math.Clamp(settings.TaskbarDashboardWidthDip, 160, 500);
            settings.TaskbarHorizontalOffsetDip = Math.Clamp(settings.TaskbarHorizontalOffsetDip, 0, 2000);
            settings.TaskbarSafeAreaMarginDip = Math.Clamp(settings.TaskbarSafeAreaMarginDip, 0, 64);
            settings.TaskbarFarLeftFallbackInsetDip = Math.Clamp(settings.TaskbarFarLeftFallbackInsetDip, 0, 1000);
            settings.EnabledWidgets ??= [DashboardWidgetIds.Clock, DashboardWidgetIds.BluetoothBattery];
            settings.WidgetOrder ??= [DashboardWidgetIds.Clock, DashboardWidgetIds.BluetoothBattery];
            settings.DeviceVisibility ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            settings.DeviceAliases ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings.BatteryRefreshIntervalSeconds = Math.Clamp(settings.BatteryRefreshIntervalSeconds, 10, 600);
            settings.LowBatteryThresholdPercent = Math.Clamp(settings.LowBatteryThresholdPercent, 1, 100);
            settings.CriticalBatteryThresholdPercent = Math.Clamp(settings.CriticalBatteryThresholdPercent, 0, settings.LowBatteryThresholdPercent - 1);
            settings.EnabledWidgets = settings.EnabledWidgets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            settings.WidgetOrder = settings.WidgetOrder
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Concat(new[] { DashboardWidgetIds.Clock, DashboardWidgetIds.BluetoothBattery }
                    .Where(id => !settings.WidgetOrder.Contains(id, StringComparer.OrdinalIgnoreCase)))
                .ToList();
            settings.ShouldMigrateDisplayMode = !displayModeWasPersisted;
            settings.ShouldMigrateHorizontalLayout = !horizontalAlignmentWasPersisted ||
                !safeAreaMarginWasPersisted ||
                !placementRegionWasPersisted ||
                !farLeftFallbackInsetWasPersisted;

            DashboardDiagnostics.Write(
                "Settings",
                $"Loaded {_settingsFilePath}; DisplayModePresent={displayModeWasPersisted}; HorizontalAlignmentPresent={horizontalAlignmentWasPersisted}; SafeAreaMarginPresent={safeAreaMarginWasPersisted}; PlacementRegionPresent={placementRegionWasPersisted}; FarLeftFallbackInsetPresent={farLeftFallbackInsetWasPersisted}; requested display mode={settings.DisplayMode}; placement region={settings.PlacementRegion}; horizontal alignment={settings.HorizontalAlignment}; safe-area margin={settings.TaskbarSafeAreaMarginDip} DIP; far-left fallback inset={settings.TaskbarFarLeftFallbackInsetDip} DIP; dashboard width={settings.TaskbarDashboardWidthDip} DIP; horizontal offset={settings.TaskbarHorizontalOffsetDip} DIP.");

            return settings;
        }
        catch (Exception exception)
        {
            DashboardDiagnostics.Write("Settings", $"Could not load {_settingsFilePath}; using backward compatible defaults. {exception.GetType().Name}: {exception.Message}");
            return new OverlaySettings();
        }
    }

    public void Save(OverlaySettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = _settingsFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _settingsFilePath, true);
        DashboardDiagnostics.Write(
            "Settings",
            $"Saved {_settingsFilePath}; requested display mode={settings.DisplayMode}; placement region={settings.PlacementRegion}; horizontal alignment={settings.HorizontalAlignment}; safe-area margin={settings.TaskbarSafeAreaMarginDip} DIP; far-left fallback inset={settings.TaskbarFarLeftFallbackInsetDip} DIP; dashboard width={settings.TaskbarDashboardWidthDip} DIP; horizontal offset={settings.TaskbarHorizontalOffsetDip} DIP.");
    }
}
