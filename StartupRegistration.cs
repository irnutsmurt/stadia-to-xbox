using Microsoft.Win32;

namespace StadiaXbox;

/// <summary>
/// "Start with Windows": a per-user Run entry (no admin rights needed) that launches
/// the app straight into the tray. Windows' own Startup apps page can switch the
/// entry off without deleting it, so that switch is read and cleared here too.
/// </summary>
internal static class StartupRegistration
{
    public const string TrayArg = "--tray";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "StadiaXbox";

    private static string Command => $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\" {TrayArg}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(ValueName) is not string) return false;

                // Explorer stores 02.. for an entry switched on and 03.. for one switched off.
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) run.SetValue(ValueName, Command);
            else run.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        // Drop any old on/off mark so a fresh entry counts as switched on.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If the app folder was moved, point the entry at this exe instead of the old one.</summary>
    public static void RepairPath()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(ValueName) is not string cmd) return;

            string oldExe = cmd.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .FirstOrDefault() ?? "";
            if (!File.Exists(oldExe)) run.SetValue(ValueName, Command);
        }
        catch { /* best-effort */ }
    }
}
