using HidSharp;

namespace StadiaXbox;

public enum Transport { None, Usb, Bluetooth }

/// <summary>
/// Finds the Google Stadia controller as a HID device (works for both the wired USB
/// interface and the Bluetooth LE HID interface, since Windows exposes both through
/// the HID stack with VID 0x18D1 / PID 0x9400), reads its input reports on a
/// background thread and decodes each one into a <see cref="StadiaState"/>.
///
/// The report layout matches SDL's open-source hidapi Stadia driver:
///   report[0] = 0x03 (report id), length 10 or 11
///   report[1] = d-pad hat (0=up, 1=up-right, 2=right, ... 7=up-left, else centred)
///   report[2] = Capture 0x01, Assistant 0x02, Stadia 0x10, Menu 0x20, Options 0x40, R3 0x80
///   report[3] = L3 0x01, R1 0x02, L1 0x04, Y 0x08, X 0x10, B 0x20, A 0x40
///   report[4..7] = LX, LY, RX, RY  (0x80 centre)
///   report[8..9] = left trigger, right trigger (0..255)
/// </summary>
public sealed class StadiaReader : IDisposable
{
    public const int VendorId = 0x18D1;
    public const int ProductId = 0x9400;

    /// <summary>Raised on the background thread for every decoded report.</summary>
    public event Action<StadiaState>? StateChanged;

    /// <summary>Raised when the controller is found/opened or lost.</summary>
    public event Action<bool, Transport>? ConnectionChanged;

    private Thread? _thread;
    private volatile bool _running;

    public bool IsConnected { get; private set; }
    public Transport Transport { get; private set; } = Transport.None;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "StadiaReader" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;
        SetConnection(false, Transport.None);
    }

    private void Loop()
    {
        var buffer = new byte[64];
        while (_running)
        {
            HidDevice? device = FindDevice();
            if (device is null)
            {
                SetConnection(false, Transport.None);
                Thread.Sleep(600);
                continue;
            }

            var transport = GuessTransport(device);
            HidStream? stream = null;
            try
            {
                if (!device.TryOpen(out stream))
                {
                    Thread.Sleep(600);
                    continue;
                }

                stream.ReadTimeout = 1000;
                SetConnection(true, transport);

                while (_running)
                {
                    int count;
                    try
                    {
                        count = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue; // controller idle, keep waiting
                    }

                    if (count <= 0) break;

                    if (TryDecode(buffer, count, out var state))
                        StateChanged?.Invoke(state);
                }
            }
            catch (IOException)
            {
                // Device unplugged or Bluetooth link dropped — fall through to rescan.
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                stream?.Dispose();
                SetConnection(false, Transport.None);
            }

            Thread.Sleep(400);
        }
    }

    private static HidDevice? FindDevice()
    {
        foreach (var dev in DeviceList.Local.GetHidDevices(VendorId, ProductId))
        {
            // The input report that carries the gamepad state is 10-11 bytes;
            // skip any secondary interface that does not expose it.
            try
            {
                if (dev.GetMaxInputReportLength() >= 10)
                    return dev;
            }
            catch
            {
                return dev;
            }
        }
        return null;
    }

    private static Transport GuessTransport(HidDevice device)
    {
        string path;
        try { path = device.DevicePath ?? string.Empty; }
        catch { path = string.Empty; }

        path = path.ToLowerInvariant();
        if (path.Contains("bthle") || path.Contains("{00001812") || path.Contains("bthenum"))
            return Transport.Bluetooth;
        return Transport.Usb;
    }

    private static bool TryDecode(byte[] r, int len, out StadiaState state)
    {
        state = default;
        if (len < 10 || r[0] != 0x03)
            return false;

        byte hat = r[1];
        bool up = hat == 7 || hat == 0 || hat == 1;
        bool right = hat == 1 || hat == 2 || hat == 3;
        bool down = hat == 3 || hat == 4 || hat == 5;
        bool left = hat == 5 || hat == 6 || hat == 7;

        byte b2 = r[2];
        byte b3 = r[3];

        state = new StadiaState
        {
            Hat = hat,
            Up = up,
            Right = right,
            Down = down,
            Left = left,

            Capture = (b2 & 0x01) != 0,
            Assistant = (b2 & 0x02) != 0,
            Stadia = (b2 & 0x10) != 0,
            Menu = (b2 & 0x20) != 0,
            Options = (b2 & 0x40) != 0,
            R3 = (b2 & 0x80) != 0,

            L3 = (b3 & 0x01) != 0,
            R1 = (b3 & 0x02) != 0,
            L1 = (b3 & 0x04) != 0,
            Y = (b3 & 0x08) != 0,
            X = (b3 & 0x10) != 0,
            B = (b3 & 0x20) != 0,
            A = (b3 & 0x40) != 0,

            LeftX = r[4],
            LeftY = r[5],
            RightX = r[6],
            RightY = r[7],
            LeftTrigger = r[8],
            RightTrigger = r[9],
        };
        return true;
    }

    private void SetConnection(bool connected, Transport transport)
    {
        if (connected == IsConnected && transport == Transport) return;
        IsConnected = connected;
        Transport = transport;
        ConnectionChanged?.Invoke(connected, transport);
    }

    public void Dispose() => Stop();
}
