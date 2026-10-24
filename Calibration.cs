using System.Text.Json;

namespace StadiaXbox;

/// <summary>
/// Per-stick calibration: the measured resting centre, a radial inner dead zone
/// (the cure for drift) and the measured maximum travel used to rescale to full
/// range. Produced by the calibration wizard and persisted so it survives restarts.
/// </summary>
public sealed class Calibration
{
    public int LeftCenterX { get; set; } = 128;
    public int LeftCenterY { get; set; } = 128;
    public int RightCenterX { get; set; } = 128;
    public int RightCenterY { get; set; } = 128;

    public float LeftDeadzone { get; set; } = 0.08f;
    public float RightDeadzone { get; set; } = 0.08f;

    public float LeftMax { get; set; } = 1.0f;
    public float RightMax { get; set; } = 1.0f;

    // Trigger range: raw value at rest -> 0, raw value at full press -> 255.
    public int LeftTriggerMin { get; set; } = 0;
    public int LeftTriggerMax { get; set; } = 255;
    public int RightTriggerMin { get; set; } = 0;
    public int RightTriggerMax { get; set; } = 255;

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StadiaXbox", "calibration.json");

    public void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* calibration persistence is best-effort */ }
    }

    public static Calibration Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
                return JsonSerializer.Deserialize<Calibration>(System.IO.File.ReadAllText(FilePath)) ?? new Calibration();
        }
        catch { }
        return new Calibration();
    }

    /// <summary>Copy measured values in from another instance (used by the wizard).</summary>
    public void CopyFrom(Calibration o)
    {
        LeftCenterX = o.LeftCenterX; LeftCenterY = o.LeftCenterY;
        RightCenterX = o.RightCenterX; RightCenterY = o.RightCenterY;
        LeftDeadzone = o.LeftDeadzone; RightDeadzone = o.RightDeadzone;
        LeftMax = o.LeftMax; RightMax = o.RightMax;
        LeftTriggerMin = o.LeftTriggerMin; LeftTriggerMax = o.LeftTriggerMax;
        RightTriggerMin = o.RightTriggerMin; RightTriggerMax = o.RightTriggerMax;
    }
}
