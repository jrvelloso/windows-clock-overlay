using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace WindowsClockOverlay;

public sealed class ClockOverlayForm : Form
{
    private readonly Label _clockLabel;
    private readonly System.Windows.Forms.Timer _clockTimer;
    private readonly NotifyIcon _trayIcon;
    private readonly OverlaySettingsStore _settingsStore;
    private readonly Dictionary<int, ToolStripMenuItem> _colorMenuItems = [];
    private readonly Icon _trayAppIcon;

    private bool _useDefaultPosition = true;
    private bool _isDragging;
    private bool _didInitialPlacement;
    private Point _savedLocation;
    private Point _dragStartCursor;
    private Point _dragStartWindow;

    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int RightMargin = 150;
    private const int TopMargin = 0;
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

        _clockLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Consolas", 22f, FontStyle.Bold),
            ForeColor = Color.Lime,
            BackColor = Color.Transparent,
            Text = DateTime.Now.ToString("HH:mm:ss")
        };

        Controls.Add(_clockLabel);
        WireMoveHandlers(this);
        WireMoveHandlers(_clockLabel);

        _clockTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _clockTimer.Tick += (_, _) => UpdateClockAndLayout();
        _clockTimer.Start();

        var trayMenu = BuildTrayMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = _trayAppIcon,
            Text = "Windows Clock Overlay",
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
        SetClickThrough(false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveSettings();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _clockTimer.Stop();
        _clockTimer.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _trayAppIcon.Dispose();
        base.OnFormClosing(e);
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Reset Position", null, (_, _) => ResetToDefaultPosition());
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
    }

    private void SaveSettings()
    {
        var settings = new OverlaySettings
        {
            ForegroundColorArgb = _clockLabel.ForeColor.ToArgb(),
            PositionX = _useDefaultPosition ? null : Location.X,
            PositionY = _useDefaultPosition ? null : Location.Y
        };
        _settingsStore.Save(settings);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_useDefaultPosition)
        {
            ApplyDefaultPosition();
        }
        else
        {
            ClampToPrimaryScreen();
            SaveSettings();
        }
    }

    private void UpdateClockAndLayout()
    {
        _clockLabel.Text = DateTime.Now.ToString("HH:mm:ss");
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
    }

    private void WireMoveHandlers(Control control)
    {
        control.MouseDown += OnMoveMouseDown;
        control.MouseMove += OnMoveMouseMove;
        control.MouseUp += OnMoveMouseUp;
    }

    private void OnMoveMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
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
        _clockLabel.ForeColor = color;
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

    private void SetClickThrough(bool enabled)
    {
        var style = NativeMethods.GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        style |= WsExLayered | WsExToolWindow;
        if (enabled)
        {
            style |= WsExTransparent;
        }
        else
        {
            style &= ~WsExTransparent;
        }

        NativeMethods.SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(style));
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
