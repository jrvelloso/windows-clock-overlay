namespace WindowsClockOverlay;

internal sealed class OverlaySettings
{
    public int? PositionX { get; set; }
    public int? PositionY { get; set; }
    public int ForegroundColorArgb { get; set; } = Color.Lime.ToArgb();
}
