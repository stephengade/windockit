using System.Windows;

namespace WinDockIt;

public partial class App : Application
{
    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "WinDockIt.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log(ex.Exception);
            ex.Handled = true;
        };

        base.OnStartup(e);
        new DockWindow().Start();
    }

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
