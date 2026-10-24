namespace StadiaXbox;

/// <summary>
/// A decoded snapshot of the Stadia controller's input report.
/// Field names use the Stadia labels; the mapping to Xbox happens in <see cref="XboxOutput"/>.
/// </summary>
public readonly struct StadiaState
{
    // Face buttons
    public bool A { get; init; }
    public bool B { get; init; }
    public bool X { get; init; }
    public bool Y { get; init; }

    // Shoulders / stick clicks
    public bool L1 { get; init; }
    public bool R1 { get; init; }
    public bool L3 { get; init; }
    public bool R3 { get; init; }

    // System buttons
    public bool Options { get; init; }   // ⋯  -> Xbox Back/View
    public bool Menu { get; init; }       // ≡  -> Xbox Start/Menu
    public bool Stadia { get; init; }     // logo -> Xbox Guide
    public bool Assistant { get; init; }  // no Xbox equivalent
    public bool Capture { get; init; }    // no Xbox equivalent

    // D-pad (decoded from the hat value)
    public bool Up { get; init; }
    public bool Down { get; init; }
    public bool Left { get; init; }
    public bool Right { get; init; }

    // Analog, raw 0..255 as the controller reports them (0x80 = centre for sticks)
    public byte LeftX { get; init; }
    public byte LeftY { get; init; }
    public byte RightX { get; init; }
    public byte RightY { get; init; }
    public byte LeftTrigger { get; init; }
    public byte RightTrigger { get; init; }

    /// <summary>Raw hat value 0..7 (8 = centred). Kept for the tester display.</summary>
    public byte Hat { get; init; }

    /// <summary>Resting state with sticks centred — used before the first report arrives.</summary>
    public static StadiaState Neutral => new()
    {
        LeftX = 128,
        LeftY = 128,
        RightX = 128,
        RightY = 128,
        Hat = 8,
    };
}
