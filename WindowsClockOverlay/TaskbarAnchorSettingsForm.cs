namespace WindowsClockOverlay;

internal sealed class TaskbarAnchorSettingsForm : Form
{
    private readonly NumericUpDown _offsetInput;
    private readonly NumericUpDown _safeAreaMarginInput;
    private readonly NumericUpDown _farLeftFallbackInsetInput;
    private readonly ComboBox _alignmentInput;
    private readonly ComboBox _placementRegionInput;

    public TaskbarAnchorSettingsForm(
        int horizontalOffsetDip,
        int safeAreaMarginDip,
        TaskbarHorizontalAlignment horizontalAlignment,
        int farLeftFallbackInsetDip,
        TaskbarPlacementRegion placementRegion)
    {
        Text = "Taskbar Anchored Layout";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 290);
        AutoScaleMode = AutoScaleMode.Font;

        var description = new Label
        {
            AutoSize = false,
            Location = new Point(14, 8),
            Size = new Size(410, 46),
            Text = "Placement region and horizontal alignment are independent. FarLeft uses the Widgets button when available, otherwise the configurable inset.",
            TextAlign = ContentAlignment.MiddleLeft
        };

        var placementRegionLabel = new Label
        {
            AutoSize = true,
            Location = new Point(16, 66),
            Text = "Placement region:"
        };
        _placementRegionInput = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(278, 62),
            Size = new Size(144, 24)
        };
        _placementRegionInput.Items.AddRange(Enum.GetNames<TaskbarPlacementRegion>());
        _placementRegionInput.SelectedItem = Enum.IsDefined(placementRegion)
            ? placementRegion.ToString()
            : TaskbarPlacementRegion.FarLeft.ToString();

        var alignmentLabel = new Label
        {
            AutoSize = true,
            Location = new Point(16, 100),
            Text = "Horizontal alignment:"
        };
        _alignmentInput = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(278, 96),
            Size = new Size(144, 24)
        };
        _alignmentInput.Items.AddRange(Enum.GetNames<TaskbarHorizontalAlignment>());
        _alignmentInput.SelectedItem = Enum.IsDefined(horizontalAlignment)
            ? horizontalAlignment.ToString()
            : TaskbarHorizontalAlignment.Left.ToString();

        var safeAreaMarginLabel = new Label
        {
            AutoSize = true,
            Location = new Point(16, 134),
            Text = "Safe-area margin (DIP):"
        };
        _safeAreaMarginInput = new NumericUpDown
        {
            Location = new Point(278, 130),
            Size = new Size(144, 24),
            Minimum = 0,
            Maximum = 64,
            Increment = 1,
            Value = Math.Clamp(safeAreaMarginDip, 0, 64)
        };

        var fallbackInsetLabel = new Label
        {
            AutoSize = true,
            Location = new Point(16, 168),
            Text = "Left inset if Widgets unavailable (DIP):"
        };
        _farLeftFallbackInsetInput = new NumericUpDown
        {
            Location = new Point(278, 164),
            Size = new Size(144, 24),
            Minimum = 0,
            Maximum = 1000,
            Increment = 10,
            Value = Math.Clamp(farLeftFallbackInsetDip, 0, 1000),
            Enabled = placementRegion == TaskbarPlacementRegion.FarLeft
        };

        var offsetLabel = new Label
        {
            AutoSize = true,
            Location = new Point(16, 202),
            Text = "Extra left offset (DIP):"
        };
        _offsetInput = new NumericUpDown
        {
            Location = new Point(278, 198),
            Size = new Size(144, 24),
            Minimum = 0,
            Maximum = 2000,
            Increment = 10,
            Value = Math.Clamp(horizontalOffsetDip, 0, 2000)
        };
        _placementRegionInput.SelectedIndexChanged += (_, _) =>
        {
            _farLeftFallbackInsetInput.Enabled = PlacementRegion == TaskbarPlacementRegion.FarLeft;
        };

        var saveButton = new Button
        {
            DialogResult = DialogResult.OK,
            Location = new Point(268, 246),
            Size = new Size(76, 28),
            Text = "Save"
        };
        var cancelButton = new Button
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(346, 246),
            Size = new Size(76, 28),
            Text = "Cancel"
        };

        Controls.AddRange([
            description,
            placementRegionLabel,
            _placementRegionInput,
            alignmentLabel,
            _alignmentInput,
            safeAreaMarginLabel,
            _safeAreaMarginInput,
            fallbackInsetLabel,
            _farLeftFallbackInsetInput,
            offsetLabel,
            _offsetInput,
            saveButton,
            cancelButton]);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public int HorizontalOffsetDip => (int)_offsetInput.Value;

    public int SafeAreaMarginDip => (int)_safeAreaMarginInput.Value;

    public int FarLeftFallbackInsetDip => (int)_farLeftFallbackInsetInput.Value;

    public TaskbarHorizontalAlignment HorizontalAlignment =>
        Enum.TryParse<TaskbarHorizontalAlignment>(_alignmentInput.SelectedItem?.ToString(), out var alignment)
            ? alignment
            : TaskbarHorizontalAlignment.Left;

    public TaskbarPlacementRegion PlacementRegion =>
        Enum.TryParse<TaskbarPlacementRegion>(_placementRegionInput.SelectedItem?.ToString(), out var region)
            ? region
            : TaskbarPlacementRegion.FarLeft;
}
