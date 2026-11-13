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
            // Already running: opening WinDockIt again shows its settings, like reopening a Mac app;
            // "WinDockIt --launchpad" opens Launchpad instead (handy for a keyboard shortcut).
            // We were just launched by the user, so we may bring a window forward; pass that on to the dock.
            AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            string name = e.Args.Contains("--launchpad", StringComparer.OrdinalIgnoreCase) ? ShowLaunchpadEvent : ShowSettingsEvent;
            try { using var show = EventWaitHandle.OpenExisting(name); show.Set(); } catch { }
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
        Listen(ShowSettingsEvent, dock, () => dock.OpenSettings());
        Listen(ShowLaunchpadEvent, dock, dock.ToggleLaunchpad);
        if (e.Args.Contains("--launchpad", StringComparer.OrdinalIgnoreCase)) dock.ToggleLaunchpad();
    }

    /// <summary>Runs <paramref name="action"/> on the dock whenever another launch of WinDockIt signals <paramref name="name"/>.</summary>
    static void Listen(string name, DockWindow dock, Action action)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        s_signals.Add(signal);
        new Thread(() =>
        {
            while (signal.WaitOne())
                dock.Dispatcher.InvokeAsync(() =>
                {
                    try { action(); } catch (Exception ex) { Log(ex); }
                });
        }) { IsBackground = true, Name = name }.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    /// <summary>Where people can support WinDockIt's developer.</summary>
    internal const string SupportUrl = "https://flossafrica.com/m/stephengade";

    internal static void OpenSupport()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SupportUrl) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log(ex); }
    }

    const string ShowSettingsEvent = "WinDockIt.ShowSettings", ShowLaunchpadEvent = "WinDockIt.ShowLaunchpad";
    static readonly List<EventWaitHandle> s_signals = new();

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
