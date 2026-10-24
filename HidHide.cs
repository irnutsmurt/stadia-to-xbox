using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HidSharp;

namespace StadiaXbox;

/// <summary>
/// Optional HidHide integration: hides the Stadia controller's HID interfaces from
/// every other app so games only see the virtual Xbox pad, while this app is put on
/// HidHide's allow-list so it can still read the controller. Everything goes through
/// HidHide's own command-line tool. Reading the configuration needs no admin rights;
/// changing it falls back to one UAC prompt when HidHide asks for elevation.
/// </summary>
internal static partial class HidHide
{
    private static readonly string Cli = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe");

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsInstalled => File.Exists(Cli);

    private sealed record Config(bool Cloaked, bool Inverse, string[] Devices, string[] Apps);

    /// <summary>
    /// True when the Stadia controller is hidden from other apps and this app may still
    /// read it; null when HidHide isn't installed or doesn't answer.
    /// </summary>
    public static bool? IsHidden()
    {
        var config = Read();
        return config is null ? null : IsSetUp(config);
    }

    /// <summary>Hides or un-hides the Stadia controller. Throws if it didn't take effect.</summary>
    public static void SetHidden(bool hide)
    {
        var config = Read() ?? throw new InvalidOperationException("HidHide isn't responding.");
        if (config.Inverse)
            throw new InvalidOperationException(
                "HidHide's inverse mode is on, so its app list blocks apps instead of allowing them. " +
                "Turn inverse mode off in the HidHide client first.");

        var commands = new List<string[]>();
        if (hide)
        {
            // Allow this app first, or hiding would also lock the bridge out.
            if (!config.Apps.Contains(ExePath, StringComparer.OrdinalIgnoreCase))
                commands.Add(new[] { "--app-reg", ExePath });
            foreach (var id in PresentStadiaIds().Where(id => !config.Devices.Contains(id, StringComparer.OrdinalIgnoreCase)))
                commands.Add(new[] { "--dev-hide", id });
            if (!config.Cloaked)
                commands.Add(new[] { "--cloak-on" });
        }
        else
        {
            // Other hidden devices and the global on/off switch belong to the user; leave them alone.
            foreach (var id in config.Devices.Where(IsStadia))
                commands.Add(new[] { "--dev-unhide", id });
        }
        if (commands.Count == 0) return;

        // Try without elevation first; only ask for admin rights if HidHide refused.
        foreach (var command in commands) Run(command);
        if (Applied(hide)) return;

        RunElevated(commands);
        if (!Applied(hide))
            throw new InvalidOperationException("HidHide didn't accept the change.");
    }

    private static bool Applied(bool hide)
    {
        var config = Read();
        if (config is null) return false;
        return hide ? IsSetUp(config) : !config.Devices.Any(IsStadia);
    }

    private static bool IsSetUp(Config config)
    {
        if (!config.Cloaked || config.Inverse) return false;
        if (!config.Apps.Contains(ExePath, StringComparer.OrdinalIgnoreCase)) return false;
        if (!config.Devices.Any(IsStadia)) return false;
        // A connection that isn't hidden yet (e.g. USB when only Bluetooth was) doesn't count.
        return PresentStadiaIds().All(id => config.Devices.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    // USB: HID\VID_18D1&PID_9400&MI_01\...   Bluetooth LE: HID\{00001812-...}_Dev_VID&0218d1_PID&9400_...\...
    private static bool IsStadia(string instanceId) =>
        instanceId.Contains("VID_18D1&PID_9400", StringComparison.OrdinalIgnoreCase) ||
        instanceId.Contains("VID&0218D1_PID&9400", StringComparison.OrdinalIgnoreCase);

    private static Config? Read()
    {
        if (!IsInstalled) return null;
        var cloak = Run("--cloak-state");
        var inverse = Run("--inv-state");
        var devices = Run("--dev-list");
        var apps = Run("--app-list");
        if (cloak.ExitCode != 0 || devices.ExitCode != 0 || apps.ExitCode != 0) return null;

        // The CLI prints its configuration back as the commands that would recreate it.
        return new Config(
            cloak.Output.Contains("--cloak-on"),
            inverse.Output.Contains("--inv-on"),
            Quoted(devices.Output, "--dev-hide"),
            Quoted(apps.Output, "--app-reg"));
    }

    private static string[] Quoted(string output, string command) =>
        QuotedArg().Matches(output)
            .Where(m => m.Groups["cmd"].Value == command)
            .Select(m => m.Groups["arg"].Value)
            .ToArray();

    [GeneratedRegex("(?<cmd>--[a-z-]+) \"(?<arg>[^\"]*)\"")]
    private static partial Regex QuotedArg();

    /// <summary>Instance IDs of the Stadia controller's HID interfaces that are connected now.</summary>
    private static IEnumerable<string> PresentStadiaIds() =>
        DeviceList.Local.GetHidDevices(StadiaReader.VendorId, StadiaReader.ProductId)
            .Select(d => InstanceIdOf(d.DevicePath))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static (int ExitCode, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(10_000)) { p.Kill(); return (-1, output); }
            return (p.ExitCode, output);
        }
        catch (Win32Exception)
        {
            return (-1, "");
        }
    }

    /// <summary>
    /// Runs all the commands in one elevated cmd, so there is a single UAC prompt.
    /// Throws <see cref="OperationCanceledException"/> if the prompt is declined.
    /// </summary>
    private static void RunElevated(IEnumerable<string[]> commands)
    {
        string line = string.Join(" & ", commands.Select(c => $"\"{Cli}\" " + string.Join(" ", c.Select(a => $"\"{a}\""))));
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{line}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(30_000);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC declined
        {
            throw new OperationCanceledException("The administrator prompt was declined.", ex);
        }
    }

    // The device instance ID (what HidHide lists) behind a HID interface path.
    private static string? InstanceIdOf(string interfacePath)
    {
        var key = new DevPropKey { FmtId = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), Pid = 256 }; // DEVPKEY_Device_InstanceId
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, null, ref size, 0);
        if (size == 0) return null;

        var buffer = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, buffer, ref size, 0) != 0) return null;
        return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_PropertyW(string deviceInterface, ref DevPropKey propertyKey,
        out uint propertyType, byte[]? buffer, ref uint bufferSize, uint flags);
}
