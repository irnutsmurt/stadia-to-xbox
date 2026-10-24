using System.Reflection;

namespace StadiaXbox;

/// <summary>
/// Draws the controller photo and lights up each button on it as it is pressed,
/// plus live dots for the two sticks (with the current deadzone drawn as a ring).
/// Positions are stored as fractions of the 1600x900 source image so they scale
/// with the displayed size.
/// </summary>
public sealed class ControllerView : Control
{
    // Temporary: draw an outline on every mapped position so placement can be
    // verified against the photo without pressing buttons.
    public static bool DebugMarkers = false;

    private readonly Image? _image;
    private readonly Calibration _cal;
    private StadiaState _state = StadiaState.Neutral;

    // name -> (x, y, radius) as fractions of the image width/height.
    private readonly record struct Marker(float X, float Y, float R);

    // Centres measured from the source photo (dark-pixel centroids).
    private static readonly Dictionary<string, Marker> Markers = new()
    {
        ["A"] = new(0.7101f, 0.3104f, 0.024f),
        ["B"] = new(0.7559f, 0.2334f, 0.024f),
        ["X"] = new(0.6617f, 0.2313f, 0.024f),
        ["Y"] = new(0.7083f, 0.1531f, 0.024f),
        ["L1"] = new(0.2828f, 0.0628f, 0.032f),
        ["R1"] = new(0.7133f, 0.0608f, 0.032f),
        ["Up"] = new(0.2860f, 0.1730f, 0.026f),
        ["Down"] = new(0.2860f, 0.3000f, 0.026f),
        ["Left"] = new(0.2410f, 0.2370f, 0.026f),
        ["Right"] = new(0.3310f, 0.2370f, 0.026f),
        ["Options"] = new(0.4125f, 0.1458f, 0.023f),
        ["Menu"] = new(0.5842f, 0.1453f, 0.023f),
        ["Assistant"] = new(0.4445f, 0.2314f, 0.020f),
        ["Capture"] = new(0.5526f, 0.2307f, 0.021f),
        ["Stadia"] = new(0.4970f, 0.4080f, 0.024f),
    };

    private static readonly Marker LeftStick = new(0.3753f, 0.3780f, 0.055f);
    private static readonly Marker RightStick = new(0.6128f, 0.3780f, 0.055f);

    public ControllerView(Calibration cal)
    {
        _cal = cal;
        DoubleBuffered = true;
        _image = LoadEmbeddedImage();
    }

    public void SetState(in StadiaState s)
    {
        _state = s;
        Invalidate();
    }

    private static Image? LoadEmbeddedImage()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("stadia.png", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            return stream is null ? null : Image.FromStream(stream);
        }
        catch
        {
            return null;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        if (_image is null)
        {
            TextRenderer.DrawText(g, "controller image not found", Font, ClientRectangle,
                Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        // Fit the 16:9 image inside the control, letterboxed.
        float cw = Width, ch = Height;
        float ar = (float)_image.Width / _image.Height;
        float dw = cw, dh = cw / ar;
        if (dh > ch) { dh = ch; dw = ch * ar; }
        float dx = (cw - dw) / 2f, dy = (ch - dh) / 2f;
        var dest = new RectangleF(dx, dy, dw, dh);
        g.DrawImage(_image, dest);

        PointF Map(Marker m) => new(dest.X + m.X * dest.Width, dest.Y + m.Y * dest.Height);
        float Px(float nr) => nr * dest.Width;

        // Optional: outline every marker to check placement against the photo.
        if (DebugMarkers)
        {
            using var dbg = new Pen(Color.FromArgb(230, 255, 40, 40), 2f);
            foreach (var (_, m) in Markers)
            {
                var p = Map(m);
                float r = Px(m.R);
                g.DrawEllipse(dbg, p.X - r, p.Y - r, r * 2, r * 2);
            }
        }

        // Pressed-button highlights
        using var fill = new SolidBrush(Color.FromArgb(185, 80, 225, 125));
        using var ring = new Pen(Color.FromArgb(255, 150, 255, 180), 3f);
        foreach (var (name, m) in Markers)
        {
            if (!IsPressed(name)) continue;
            var p = Map(m);
            float r = Px(m.R);
            g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2);
            g.DrawEllipse(ring, p.X - r, p.Y - r, r * 2, r * 2);
        }

        DrawStick(g, dest, LeftStick, _state.LeftX, _state.LeftY, _state.L3, _cal.LeftDeadzone, _cal.LeftCenterX, _cal.LeftCenterY);
        DrawStick(g, dest, RightStick, _state.RightX, _state.RightY, _state.R3, _cal.RightDeadzone, _cal.RightCenterX, _cal.RightCenterY);
    }

    private void DrawStick(Graphics g, RectangleF dest, Marker m, byte rawX, byte rawY, bool pressed, float deadzone, int centerX, int centerY)
    {
        float cx = dest.X + m.X * dest.Width;
        float cy = dest.Y + m.Y * dest.Height;
        float travel = m.R * dest.Width;

        // Deadzone ring
        float dz = deadzone * travel;
        if (dz > 1f)
        {
            using var dzPen = new Pen(Color.FromArgb(120, 230, 170, 80), 1.5f);
            g.DrawEllipse(dzPen, cx - dz, cy - dz, dz * 2, dz * 2);
        }

        // Travel boundary
        using var edge = new Pen(Color.FromArgb(90, 200, 200, 210), 1f);
        g.DrawEllipse(edge, cx - travel, cy - travel, travel * 2, travel * 2);

        // Live position dot (recentred on the measured rest point)
        float nx = Math.Clamp((rawX - centerX) / 127f, -1f, 1f);
        float ny = Math.Clamp((rawY - centerY) / 127f, -1f, 1f); // screen: positive y is down, matches
        float px = cx + nx * travel;
        float py = cy + ny * travel;
        float r = travel * 0.28f;
        using var dot = new SolidBrush(pressed ? Color.FromArgb(70, 210, 120) : Color.FromArgb(90, 160, 240));
        g.FillEllipse(dot, px - r, py - r, r * 2, r * 2);
    }

    private bool IsPressed(string name) => name switch
    {
        "A" => _state.A,
        "B" => _state.B,
        "X" => _state.X,
        "Y" => _state.Y,
        "L1" => _state.L1,
        "R1" => _state.R1,
        "Up" => _state.Up,
        "Down" => _state.Down,
        "Left" => _state.Left,
        "Right" => _state.Right,
        "Options" => _state.Options,
        "Menu" => _state.Menu,
        "Assistant" => _state.Assistant,
        "Capture" => _state.Capture,
        "Stadia" => _state.Stadia,
        _ => false,
    };
}
