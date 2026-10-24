using System.Runtime.InteropServices;

namespace StadiaXbox;

internal static class Program
{
    // Only one copy may run: a second one would add a second virtual Xbox pad.
    private const string MutexName = @"Local\StadiaXbox.Running";
    private const string ShowEventName = @"Local\StadiaXbox.Show";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running (probably in the tray): bring that window up instead.
            try
            {
                using var running = EventWaitHandle.OpenExisting(ShowEventName);
                AllowSetForegroundWindow(-1); // ASFW_ANY, so the other copy may take focus
                running.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }

        using var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        bool startInTray = args.Contains(StartupRegistration.TrayArg, StringComparer.OrdinalIgnoreCase);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(showRequested, startInTray));
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
