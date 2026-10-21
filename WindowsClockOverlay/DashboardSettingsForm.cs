namespace WindowsClockOverlay;

internal sealed class DashboardSettingsForm : Form
{
    private readonly CheckedListBox _widgetOrder;
    private readonly DataGridView _deviceGrid;
    private readonly NumericUpDown _refreshSeconds;
    private readonly NumericUpDown _lowThreshold;
    private readonly NumericUpDown _criticalThreshold;
    private readonly CheckBox _compactLayout;
    private readonly CheckBox _batteryPercentageOnly;
    private readonly OverlaySettings _originalSettings;

    public DashboardSettingsForm(OverlaySettings settings, IReadOnlyList<BluetoothBatteryDevice> devices)
    {
        _originalSettings = settings.Copy();
        Text = "Dashboard and Bluetooth Devices";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 510);
        AutoScaleMode = AutoScaleMode.Font;

        var widgetsLabel = new Label { AutoSize = true, Location = new Point(14, 13), Text = "Enabled widgets and order (higher priority appears first when space is limited):" };
        _widgetOrder = new CheckedListBox
        {
            CheckOnClick = true,
            Location = new Point(16, 38),
            Size = new Size(245, 93),
            IntegralHeight = false
        };

        var order = settings.WidgetOrder
            .Concat(new[] { DashboardWidgetIds.Clock, DashboardWidgetIds.BluetoothBattery })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => id is DashboardWidgetIds.Clock or DashboardWidgetIds.BluetoothBattery)
            .ToArray();
        foreach (var id in order)
        {
            var item = new WidgetOption(id, id == DashboardWidgetIds.Clock ? "Clock" : "Bluetooth batteries");
            var index = _widgetOrder.Items.Add(item);
            _widgetOrder.SetItemChecked(index, settings.EnabledWidgets.Contains(id, StringComparer.OrdinalIgnoreCase));
        }

        var moveUp = new Button { Location = new Point(271, 42), Size = new Size(62, 28), Text = "Move up" };
        moveUp.Click += (_, _) => MoveSelected(-1);
        var moveDown = new Button { Location = new Point(271, 76), Size = new Size(62, 28), Text = "Move down" };
        moveDown.Click += (_, _) => MoveSelected(1);
        _compactLayout = new CheckBox
        {
            AutoSize = true,
            Location = new Point(362, 42),
            Text = "Compact layout",
            Checked = settings.CompactLayout
        };
        _batteryPercentageOnly = new CheckBox
        {
            AutoSize = true,
            Location = new Point(362, 168),
            Text = "Battery percentages only",
            Checked = settings.BatteryPercentageOnly
        };

        var refreshLabel = new Label { AutoSize = true, Location = new Point(362, 76), Text = "Battery refresh (seconds):" };
        _refreshSeconds = MakeNumeric(537, 72, 10, 600, Math.Clamp(settings.BatteryRefreshIntervalSeconds, 10, 600));
        var lowLabel = new Label { AutoSize = true, Location = new Point(362, 108), Text = "Low battery threshold (%):" };
        _lowThreshold = MakeNumeric(537, 104, 1, 100, Math.Clamp(settings.LowBatteryThresholdPercent, 1, 100));
        var criticalLabel = new Label { AutoSize = true, Location = new Point(362, 140), Text = "Critical threshold (%):" };
        _criticalThreshold = MakeNumeric(537, 136, 0, 99, Math.Clamp(settings.CriticalBatteryThresholdPercent, 0, 99));

        var devicesLabel = new Label { AutoSize = true, Location = new Point(14, 173), Text = "Bluetooth devices: check to show; edit the alias to change its displayed name." };
        _deviceGrid = new DataGridView
        {
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            Location = new Point(16, 198),
            MultiSelect = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            Size = new Size(668, 250)
        };
        _deviceGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "Show",
            Width = 56,
            ThreeState = false
        });
        _deviceGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Device",
            ReadOnly = true,
            Width = 180
        });
        _deviceGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Alias",
            Width = 180
        });
        _deviceGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Windows report",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        _deviceGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_deviceGrid.IsCurrentCellDirty)
            {
                _deviceGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        foreach (var device in devices.OrderBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
        {
            var visible = !settings.DeviceVisibility.TryGetValue(device.StableId, out var value) || value;
            var alias = settings.DeviceAliases.TryGetValue(device.StableId, out var savedAlias) ? savedAlias : string.Empty;
            var reading = device.BatteryPercent.HasValue
                ? $"{device.BatteryPercent}% (read {device.LastReadAt?.ToLocalTime().ToString("HH:mm:ss") ?? "time unavailable"})"
                : device.State == BatteryReadingState.Disconnected ? "Disconnected" : "Unavailable";
            var rowIndex = _deviceGrid.Rows.Add(visible, device.FriendlyName, alias, reading);
            var row = _deviceGrid.Rows[rowIndex];
            row.Tag = device.StableId;
            row.Cells[1].ToolTipText = $"{device.FriendlyName}\n{device.InstanceId}\nStable ID: {device.StableId}";
            row.Cells[3].ToolTipText = device.Diagnostic ?? string.Empty;
        }

        if (devices.Count == 0)
        {
            var note = new Label
            {
                AutoSize = true,
                ForeColor = Color.DimGray,
                Location = new Point(20, 224),
                Text = "No Bluetooth device nodes have been discovered yet. The list fills after the first background refresh."
            };
            Controls.Add(note);
        }

        var save = new Button { DialogResult = DialogResult.OK, Location = new Point(526, 464), Size = new Size(76, 30), Text = "Save" };
        var cancel = new Button { DialogResult = DialogResult.Cancel, Location = new Point(608, 464), Size = new Size(76, 30), Text = "Cancel" };
        Controls.AddRange([widgetsLabel, _widgetOrder, moveUp, moveDown, _compactLayout, refreshLabel, _refreshSeconds, lowLabel, _lowThreshold, criticalLabel, _criticalThreshold, _batteryPercentageOnly, devicesLabel, _deviceGrid, save, cancel]);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public OverlaySettings EditedSettings
    {
        get
        {
            var settings = _originalSettings.Copy();
            settings.EnabledWidgets = _widgetOrder.CheckedItems.Cast<WidgetOption>().Select(option => option.Id).ToList();
            settings.WidgetOrder = _widgetOrder.Items.Cast<WidgetOption>().Select(option => option.Id).ToList();
            settings.CompactLayout = _compactLayout.Checked;
            settings.BatteryPercentageOnly = _batteryPercentageOnly.Checked;
            settings.BatteryRefreshIntervalSeconds = (int)_refreshSeconds.Value;
            settings.LowBatteryThresholdPercent = (int)_lowThreshold.Value;
            settings.CriticalBatteryThresholdPercent = Math.Min((int)_criticalThreshold.Value, Math.Max(0, settings.LowBatteryThresholdPercent - 1));

            foreach (DataGridViewRow row in _deviceGrid.Rows)
            {
                if (row.Tag is not string stableId)
                {
                    continue;
                }

                var visible = Convert.ToBoolean(row.Cells[0].Value ?? true);
                if (visible)
                {
                    settings.DeviceVisibility.Remove(stableId);
                }
                else
                {
                    settings.DeviceVisibility[stableId] = false;
                }

                var alias = Convert.ToString(row.Cells[2].Value)?.Trim();
                if (string.IsNullOrWhiteSpace(alias))
                {
                    settings.DeviceAliases.Remove(stableId);
                }
                else
                {
                    settings.DeviceAliases[stableId] = alias;
                }
            }

            return settings;
        }
    }

    private void MoveSelected(int direction)
    {
        var selectedIndex = _widgetOrder.SelectedIndex;
        var newIndex = selectedIndex + direction;
        if (selectedIndex < 0 || newIndex < 0 || newIndex >= _widgetOrder.Items.Count)
        {
            return;
        }

        var selected = _widgetOrder.Items[selectedIndex];
        var wasChecked = _widgetOrder.GetItemChecked(selectedIndex);
        _widgetOrder.Items.RemoveAt(selectedIndex);
        _widgetOrder.Items.Insert(newIndex, selected);
        _widgetOrder.SetItemChecked(newIndex, wasChecked);
        _widgetOrder.SelectedIndex = newIndex;
    }

    private static NumericUpDown MakeNumeric(int x, int y, int minimum, int maximum, int value)
    {
        return new NumericUpDown
        {
            Location = new Point(x, y),
            Size = new Size(76, 24),
            Minimum = minimum,
            Maximum = maximum,
            Value = value
        };
    }

    private sealed record WidgetOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
