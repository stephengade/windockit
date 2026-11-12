using System.Windows;

namespace WinDockIt;

public partial class App : Application
{
    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Watchdog copy started when the taskbar is hidden: restores it if the dock dies.
        if (e.Args.Length == 2 && e.Args[0] == "--watchdog" && int.TryParse(e.Args[1], out int pid))
        {
            TaskbarHider.RunWatchdog(pid);
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, "WinDockIt.SingleInstance", out bool created);
        if (!created)
        {
            // Already running: opening WinDockIt again shows its settings, like reopening a Mac app.
            // We were just launched by the user, so we may bring a window forward; pass that on to the dock.
            AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            try { using var show = EventWaitHandle.OpenExisting(ShowSettingsEvent); show.Set(); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log(ex.Exception);
            ex.Handled = true;
        };

        base.OnStartup(e);
        var dock = new DockWindow();
        dock.Start();
        _showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEvent);
        new Thread(() =>
        {
            while (_showSettings.WaitOne())
                dock.Dispatcher.InvokeAsync(() =>
                {
                    try { dock.OpenSettings(); } catch (Exception ex) { Log(ex); }
                });
        }) { IsBackground = true, Name = "Show settings" }.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    const string ShowSettingsEvent = "WinDockIt.ShowSettings";
    EventWaitHandle? _showSettings;

    internal static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(DockConfig.Folder);
            File.AppendAllText(Path.Combine(DockConfig.Folder, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
    }
}
