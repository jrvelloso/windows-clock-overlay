using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsClockOverlay;

internal enum BatteryReadingState
{
    Available,
    Unavailable,
    Disconnected
}

internal sealed record BluetoothBatteryDevice(
    string StableId,
    string InstanceId,
    string FriendlyName,
    BatteryReadingState State,
    int? BatteryPercent,
    DateTimeOffset? LastReadAt,
    bool HasBatteryProperty,
    string? Diagnostic = null);

internal sealed record BluetoothPnPNode(
    string InstanceId,
    string FriendlyName,
    string? ContainerId,
    bool IsStarted,
    uint BatteryPropertyType,
    byte[]? BatteryPropertyData,
    string? BatteryPropertyError = null);

internal interface IBluetoothDeviceSource
{
    IReadOnlyList<BluetoothPnPNode> ReadPresentBluetoothNodes(CancellationToken cancellationToken);
}

internal static class BluetoothBatteryPropertyParser
{
    internal const uint DevPropTypeByte = 0x03;

    public static bool TryParse(uint propertyType, ReadOnlySpan<byte> data, out int percentage)
    {
        percentage = 0;
        if (propertyType != DevPropTypeByte || data.Length != 1 || data[0] > 100)
        {
            return false;
        }

        percentage = data[0];
        return true;
    }
}

internal static partial class BluetoothDeviceDeduplicator
{
    [GeneratedRegex("(?:^|\\\\)DEV_([0-9A-F]{12})(?:\\\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluetoothLeAddressRegex();

    [GeneratedRegex("&0&([0-9A-F]{12})(?:_|\\\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluetoothEnumeratorAddressRegex();

    private static readonly string[] ServiceSuffixes =
    [
        " Hands-Free AG",
        " Hands-Free HF",
        " Hands-Free",
        " Avrcp Transport",
        " Stereo",
        " LE Device"
    ];

    public static IReadOnlyList<BluetoothBatteryDevice> Deduplicate(
        IReadOnlyList<BluetoothPnPNode> nodes,
        DateTimeOffset readAt)
    {
        return nodes
            .GroupBy(GetStableId, StringComparer.OrdinalIgnoreCase)
            .Select(group => ToDevice(group.Key, group.ToArray(), readAt))
            .OrderBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static string GetStableId(BluetoothPnPNode node)
    {
        if (Guid.TryParse(node.ContainerId, out var containerId) && containerId != Guid.Empty)
        {
            return $"bt-container:{containerId:N}";
        }

        var address = BluetoothLeAddressRegex().Match(node.InstanceId);
        if (!address.Success)
        {
            address = BluetoothEnumeratorAddressRegex().Match(node.InstanceId);
        }
        if (address.Success)
        {
            return $"bt-address:{address.Groups[1].Value.ToUpperInvariant()}";
        }

        return $"bt-instance:{node.InstanceId.Trim().ToUpperInvariant()}";
    }

    private static BluetoothBatteryDevice ToDevice(string stableId, BluetoothPnPNode[] nodes, DateTimeOffset readAt)
    {
        var nodesByQuality = nodes
            .OrderByDescending(node => node.IsStarted)
            .ThenByDescending(node => BluetoothBatteryPropertyParser.TryParse(node.BatteryPropertyType, node.BatteryPropertyData ?? [], out _))
            .ThenBy(node => node.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var chosen = nodesByQuality[0];
        var connected = nodes.Any(node => node.IsStarted);
        var readingNode = nodes
            .Where(node => node.IsStarted && BluetoothBatteryPropertyParser.TryParse(node.BatteryPropertyType, node.BatteryPropertyData ?? [], out _))
            .OrderBy(node => node.InstanceId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!connected)
        {
            var hasProperty = nodes.Any(HasBatteryProperty);
            return new BluetoothBatteryDevice(stableId, chosen.InstanceId, CleanName(chosen.FriendlyName), BatteryReadingState.Disconnected, null, null, hasProperty, "Bluetooth device is present but not started.");
        }

        if (readingNode is not null && BluetoothBatteryPropertyParser.TryParse(readingNode.BatteryPropertyType, readingNode.BatteryPropertyData ?? [], out var percent))
        {
            return new BluetoothBatteryDevice(stableId, readingNode.InstanceId, CleanName(readingNode.FriendlyName), BatteryReadingState.Available, percent, readAt, true);
        }

        var error = nodes.Select(node => node.BatteryPropertyError).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var invalid = nodes.Any(node => node.BatteryPropertyData is not null &&
            !BluetoothBatteryPropertyParser.TryParse(node.BatteryPropertyType, node.BatteryPropertyData, out _));
        var diagnostic = error ?? (invalid ? "Windows returned an invalid battery property." : "No Windows battery property is available for this Bluetooth device.");
        return new BluetoothBatteryDevice(stableId, chosen.InstanceId, CleanName(chosen.FriendlyName), BatteryReadingState.Unavailable, null, null, nodes.Any(HasBatteryProperty), diagnostic);
    }

    private static bool HasBatteryProperty(BluetoothPnPNode node) => node.BatteryPropertyData is not null || !string.IsNullOrWhiteSpace(node.BatteryPropertyError);

    private static string CleanName(string name)
    {
        var cleaned = name.Trim();
        foreach (var suffix in ServiceSuffixes)
        {
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[..^suffix.Length].TrimEnd();
            }
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "Bluetooth device" : cleaned;
    }
}

internal sealed class BluetoothBatteryService : IDisposable
{
    private readonly IBluetoothDeviceSource _source;
    private readonly Func<int> _refreshIntervalSeconds;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, BluetoothBatteryDevice> _knownDevices = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private IReadOnlyList<BluetoothBatteryDevice> _snapshot = Array.Empty<BluetoothBatteryDevice>();
    private bool _disposed;

    public BluetoothBatteryService(
        IBluetoothDeviceSource source,
        Func<int> refreshIntervalSeconds,
        Func<DateTimeOffset>? utcNow = null)
    {
        _source = source;
        _refreshIntervalSeconds = refreshIntervalSeconds;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public event Action<IReadOnlyList<BluetoothBatteryDevice>>? SnapshotChanged;

    public IReadOnlyList<BluetoothBatteryDevice> Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                return _snapshot.ToArray();
            }
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_disposed || _worker is not null)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            _worker = Task.Run(() => PollLoopAsync(_cancellation.Token));
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            _cancellation?.Cancel();
        }
    }

    public async Task RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nodes = await Task.Run(() => _source.ReadPresentBluetoothNodes(cancellationToken), cancellationToken).ConfigureAwait(false);
            var readAt = _utcNow();
            var scan = BluetoothDeviceDeduplicator.Deduplicate(nodes, readAt);
            var discovered = scan.Where(device => device.HasBatteryProperty).ToDictionary(device => device.StableId, StringComparer.OrdinalIgnoreCase);
            var presentIds = scan.Select(device => device.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<BluetoothBatteryDevice> current;

            lock (_stateLock)
            {
                foreach (var (id, device) in discovered)
                {
                    _knownDevices[id] = device;
                }

                foreach (var device in scan.Where(device => !device.HasBatteryProperty && _knownDevices.ContainsKey(device.StableId)))
                {
                    _knownDevices[device.StableId] = device with
                    {
                        HasBatteryProperty = true,
                        State = BatteryReadingState.Unavailable,
                        BatteryPercent = null,
                        LastReadAt = null,
                        Diagnostic = "Windows no longer returns the battery property for this Bluetooth device."
                    };
                }

                foreach (var (id, previous) in _knownDevices.ToArray())
                {
                    if (!presentIds.Contains(id))
                    {
                        _knownDevices[id] = previous with
                        {
                            State = BatteryReadingState.Disconnected,
                            BatteryPercent = null,
                            LastReadAt = null,
                            Diagnostic = "Bluetooth device is no longer present."
                        };
                    }
                }

                current = _knownDevices.Values.OrderBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase).ToArray();
                _snapshot = current;
            }

            LogSnapshot(current);
            Publish(current);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancellation?.Cancel();
        }

        SnapshotChanged = null;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                DashboardDiagnostics.Write("Bluetooth", $"Bluetooth battery refresh failed: {exception.GetType().Name}: {exception.Message}");
                MarkCurrentUnavailable(exception.Message);
            }

            var refreshSeconds = Math.Clamp(_refreshIntervalSeconds(), 10, 600);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(refreshSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void MarkCurrentUnavailable(string reason)
    {
        IReadOnlyList<BluetoothBatteryDevice> current;
        lock (_stateLock)
        {
            foreach (var (id, previous) in _knownDevices.ToArray())
            {
                _knownDevices[id] = previous with
                {
                    State = BatteryReadingState.Unavailable,
                    BatteryPercent = null,
                    LastReadAt = null,
                    Diagnostic = $"Windows battery scan failed: {reason}"
                };
            }

            current = _knownDevices.Values.ToArray();
            _snapshot = current;
        }

        Publish(current);
    }

    private static void LogSnapshot(IReadOnlyList<BluetoothBatteryDevice> devices)
    {
        var values = devices.Count == 0
            ? "no present Bluetooth devices exposing candidate nodes"
            : string.Join("; ", devices.Select(device => $"{device.FriendlyName} [{device.StableId}]={device.BatteryPercent?.ToString() ?? "--"}% ({device.State}; instance={device.InstanceId})"));
        DashboardDiagnostics.Write("Bluetooth", $"Read current Windows PnP battery snapshot: {values}");
    }

    private void Publish(IReadOnlyList<BluetoothBatteryDevice> devices)
    {
        try
        {
            SnapshotChanged?.Invoke(devices);
        }
        catch (Exception exception)
        {
            DashboardDiagnostics.Write("Bluetooth", $"Snapshot listener failed: {exception.GetType().Name}: {exception.Message}");
        }
    }
}

internal sealed class WindowsBluetoothDeviceSource : IBluetoothDeviceSource
{
    private const uint CmGetIdListFilterEnumerator = 0x00000001;
    private const uint CmGetIdListFilterPresent = 0x00000100;
    private const uint CmLocateDevNodeNormal = 0;
    private const uint CmSuccess = 0;
    private const uint CmBufferSmall = 0x1A;
    private const uint CmNoSuchValue = 0x25;
    private const uint DeviceNodeStarted = 0x00000008;
    private const uint DevPropTypeString = 0x12;
    private const uint DevPropTypeGuid = 0x0D;

    private static readonly DevPropKey BatteryPercentageKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DevPropKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly DevPropKey ContainerIdKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly string[] Enumerators = ["BTHENUM", "BTHLE"];

    public IReadOnlyList<BluetoothPnPNode> ReadPresentBluetoothNodes(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Bluetooth PnP discovery requires Windows Configuration Manager APIs.");
        }

        var result = new List<BluetoothPnPNode>();
        foreach (var enumerator in Enumerators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instanceIds = EnumerateInstanceIds(enumerator);
            var locatedCount = 0;
            var startedCount = 0;
            var batteryPropertyCount = 0;
            var locateFailures = new List<string>();
            foreach (var instanceId in instanceIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var locateResult = CM_Locate_DevNodeW(out var deviceNode, instanceId, CmLocateDevNodeNormal);
                if (locateResult != CmSuccess)
                {
                    if (locateFailures.Count < 3)
                    {
                        locateFailures.Add($"{instanceId} (0x{locateResult:X})");
                    }

                    continue;
                }

                locatedCount++;
                var isStarted = CM_Get_DevNode_Status(out var status, out _, deviceNode, 0) == CmSuccess && (status & DeviceNodeStarted) != 0;
                if (isStarted)
                {
                    startedCount++;
                }

                var friendlyName = ReadStringProperty(deviceNode, FriendlyNameKey) ?? DeriveName(instanceId);
                var containerId = ReadGuidProperty(deviceNode, ContainerIdKey);
                var property = ReadProperty(deviceNode, BatteryPercentageKey);
                if (property.Data is not null)
                {
                    batteryPropertyCount++;
                }

                result.Add(new BluetoothPnPNode(
                    instanceId,
                    friendlyName,
                    containerId,
                    isStarted,
                    property.PropertyType,
                    property.Data,
                    property.Error));
            }

            DashboardDiagnostics.Write(
                "BluetoothPnP",
                $"Enumerator={enumerator}; present candidates={instanceIds.Count}; located={locatedCount}; started={startedCount}; battery property payloads={batteryPropertyCount}; locate failures=[{string.Join("; ", locateFailures)}].");
        }

        return result;
    }

    private static IReadOnlyList<string> EnumerateInstanceIds(string enumerator)
    {
        const uint flags = CmGetIdListFilterEnumerator | CmGetIdListFilterPresent;
        var result = CM_Get_Device_ID_List_SizeW(out var characterCount, enumerator, flags);
        if (result != CmSuccess || characterCount == 0)
        {
            DashboardDiagnostics.Write("BluetoothPnP", $"CM_Get_Device_ID_List_SizeW failed or returned empty for {enumerator}; CONFIGRET=0x{result:X}; character count={characterCount}.");
            return Array.Empty<string>();
        }

        var buffer = new char[characterCount];
        result = CM_Get_Device_ID_ListW(enumerator, buffer, characterCount, flags);
        if (result != CmSuccess)
        {
            throw new InvalidOperationException($"CM_Get_Device_ID_ListW failed for {enumerator} (CONFIGRET 0x{result:X}).");
        }

        var instanceIds = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (instanceIds.Length == 0)
        {
            DashboardDiagnostics.Write("BluetoothPnP", $"CM_Get_Device_ID_ListW returned an empty MULTI_SZ for {enumerator}; requested characters={characterCount}.");
        }

        return instanceIds;
    }

    private static string? ReadStringProperty(uint deviceNode, DevPropKey key)
    {
        var property = ReadProperty(deviceNode, key);
        if (property.PropertyType != DevPropTypeString || property.Data is null)
        {
            return null;
        }

        return Encoding.Unicode.GetString(property.Data).TrimEnd('\0').Trim();
    }

    private static string? ReadGuidProperty(uint deviceNode, DevPropKey key)
    {
        var property = ReadProperty(deviceNode, key);
        if (property.PropertyType != DevPropTypeGuid || property.Data is not { Length: >= 16 })
        {
            return null;
        }

        return new Guid(property.Data.AsSpan(0, 16)).ToString("D");
    }

    private static (uint PropertyType, byte[]? Data, string? Error) ReadProperty(uint deviceNode, DevPropKey key)
    {
        var size = 0u;
        var propertyType = 0u;
        var firstResult = CM_Get_DevNode_PropertyW(deviceNode, ref key, out propertyType, null, ref size, 0);
        if (firstResult == CmNoSuchValue)
        {
            return (0, null, null);
        }

        if (firstResult != CmBufferSmall && firstResult != CmSuccess)
        {
            return (0, null, $"CM_Get_DevNode_PropertyW returned CONFIGRET 0x{firstResult:X}.");
        }

        if (size == 0)
        {
            return (propertyType, Array.Empty<byte>(), null);
        }

        var data = new byte[size];
        var secondResult = CM_Get_DevNode_PropertyW(deviceNode, ref key, out propertyType, data, ref size, 0);
        if (secondResult != CmSuccess)
        {
            return (propertyType, null, $"CM_Get_DevNode_PropertyW read returned CONFIGRET 0x{secondResult:X}.");
        }

        return (propertyType, data.AsSpan(0, (int)size).ToArray(), null);
    }

    private static string DeriveName(string instanceId)
    {
        var match = Regex.Match(instanceId, @"DEV_([0-9A-F]{12})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? $"Bluetooth {match.Groups[1].Value.ToUpperInvariant()}" : "Bluetooth device";
    }

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string enumerator, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_ListW(string enumerator, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint deviceNode, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint deviceNode, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint deviceNode,
        ref DevPropKey propertyKey,
        out uint propertyType,
        [Out] byte[]? buffer,
        ref uint bufferSize,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }
}
