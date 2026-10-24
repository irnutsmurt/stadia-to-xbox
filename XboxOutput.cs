using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace StadiaXbox;

/// <summary>
/// Presents a virtual Xbox 360 controller through the ViGEmBus driver and feeds it
/// the decoded Stadia state. Games read the virtual pad through XInput exactly as
/// they would a real Xbox controller.
/// </summary>
public sealed class XboxOutput : IDisposable
{
    private readonly Calibration _cal;
    private ViGEmClient? _client;
    private IXbox360Controller? _pad;

    public XboxOutput(Calibration calibration) => _cal = calibration;

    public bool IsRunning => _pad is not null;

    /// <summary>
    /// Connects the virtual controller. Throws if the ViGEmBus driver is missing,
    /// which the caller surfaces to the user.
    /// </summary>
    public void Connect()
    {
        if (_pad is not null) return;

        _client = new ViGEmClient();
        _pad = _client.CreateXbox360Controller();
        _pad.AutoSubmitReport = false; // we batch a full report per frame
        _pad.Connect();
    }

    public void Update(in StadiaState s)
    {
        var pad = _pad;
        if (pad is null) return;

        pad.SetButtonState(Xbox360Button.A, s.A);
        pad.SetButtonState(Xbox360Button.B, s.B);
        pad.SetButtonState(Xbox360Button.X, s.X);
        pad.SetButtonState(Xbox360Button.Y, s.Y);

        pad.SetButtonState(Xbox360Button.LeftShoulder, s.L1);
        pad.SetButtonState(Xbox360Button.RightShoulder, s.R1);
        pad.SetButtonState(Xbox360Button.LeftThumb, s.L3);
        pad.SetButtonState(Xbox360Button.RightThumb, s.R3);

        pad.SetButtonState(Xbox360Button.Back, s.Options);
        pad.SetButtonState(Xbox360Button.Start, s.Menu);
        pad.SetButtonState(Xbox360Button.Guide, s.Stadia);

        pad.SetButtonState(Xbox360Button.Up, s.Up);
        pad.SetButtonState(Xbox360Button.Down, s.Down);
        pad.SetButtonState(Xbox360Button.Left, s.Left);
        pad.SetButtonState(Xbox360Button.Right, s.Right);

        var (lx, ly) = ApplyStick(s.LeftX, s.LeftY, _cal.LeftCenterX, _cal.LeftCenterY, _cal.LeftDeadzone, _cal.LeftMax);
        var (rx, ry) = ApplyStick(s.RightX, s.RightY, _cal.RightCenterX, _cal.RightCenterY, _cal.RightDeadzone, _cal.RightMax);
        pad.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, ly);
        pad.SetAxisValue(Xbox360Axis.RightThumbX, rx);
        pad.SetAxisValue(Xbox360Axis.RightThumbY, ry);

        pad.SetSliderValue(Xbox360Slider.LeftTrigger, ScaleTrigger(s.LeftTrigger, _cal.LeftTriggerMin, _cal.LeftTriggerMax));
        pad.SetSliderValue(Xbox360Slider.RightTrigger, ScaleTrigger(s.RightTrigger, _cal.RightTriggerMin, _cal.RightTriggerMax));

        pad.SubmitReport();
    }

    /// <summary>
    /// Converts a raw stick (bytes 0..255) to XInput axes using the measured centre,
    /// a radial inner dead zone (drift cure) and the measured maximum travel, rescaling
    /// so the stick still reaches full tilt. Y is inverted to match XInput's
    /// positive-is-up convention.
    /// </summary>
    internal static (short x, short y) ApplyStick(byte rawX, byte rawY, int centerX, int centerY, float deadzone, float max)
    {
        float nx = Math.Clamp((rawX - centerX) / 127f, -1f, 1f);
        float ny = Math.Clamp((rawY - centerY) / 127f, -1f, 1f);
        ny = -ny; // controller reports positive-down; XInput wants positive-up

        float mag = MathF.Sqrt(nx * nx + ny * ny);
        if (mag <= deadzone || mag < 1e-4f)
            return (0, 0);

        float upper = MathF.Max(max, deadzone + 0.1f);
        float scaled = MathF.Min((mag - deadzone) / (upper - deadzone), 1f);
        float ux = nx / mag;
        float uy = ny / mag;

        short ox = (short)Math.Clamp(ux * scaled * short.MaxValue, short.MinValue, short.MaxValue);
        short oy = (short)Math.Clamp(uy * scaled * short.MaxValue, short.MinValue, short.MaxValue);
        return (ox, oy);
    }

    /// <summary>Rescales a trigger so its measured rest reads 0 and full press reads 255.</summary>
    internal static byte ScaleTrigger(byte raw, int min, int max)
    {
        if (max <= min + 1) return raw;
        float t = (raw - min) / (float)(max - min);
        return (byte)Math.Clamp(t * 255f, 0f, 255f);
    }

    public void Dispose()
    {
        try { _pad?.Disconnect(); } catch { }
        _pad = null;
        _client?.Dispose();
        _client = null;
    }
}
