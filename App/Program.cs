using System.Diagnostics;
using System.Runtime.InteropServices;

namespace App;

static class Program
{
    // Windows identity of the app (media card, taskbar grouping). The registry write itself lives
    // in Shared\AppIdentity.cs, which is compiled into both this app and the installer.
    internal const string AppId = AppIdentity.AppId;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    // Lets Windows' own popup menus (window menu, context menus) follow the Windows light/dark setting.
    // Undocumented uxtheme ordinals; they simply do nothing on versions that do not have them.
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\YouTubeMusicDesktopMutex", out bool createdNew);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\YouTubeMusicDesktopShow");

        if (!createdNew)
        {
            // Already running (possibly hidden in the tray): let it take focus, then ask it to show itself.
            try
            {
                using Process current = Process.GetCurrentProcess();
                foreach (Process p in Process.GetProcessesByName(current.ProcessName))
                {
                    using (p)
                    {
                        if (p.Id != current.Id)
                        {
                            AllowSetForegroundWindow(p.Id);
                            break;
                        }
                    }
                }
            }
            catch { }
            showEvent.Set();
            return;
        }

        try { SetCurrentProcessExplicitAppUserModelID(AppId); } catch { }
        AppIdentity.Register(); // media card name/icon
        try { SetPreferredAppMode(1); FlushMenuThemes(); } catch { } // 1 = AllowDark: follow the system theme
        ApplicationConfiguration.Initialize();
        var form = new Form1();

        var registration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
        {
            try
            {
                if (form.IsHandleCreated && !form.IsDisposed)
                    form.BeginInvoke(new Action(form.RestoreFromTray));
            }
            catch { }
        }, null, Timeout.Infinite, false);

        Application.Run(form);
        registration.Unregister(null);
    }
}
