using System.Windows;
using Application = System.Windows.Application;

namespace diashow;

public partial class App : Application
{
    internal UpdateManager UpdateManager { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(e.Args, UpdateManager);
        MainWindow = window;
        window.Show();
        UpdateManager.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UpdateManager.Dispose();
        base.OnExit(e);
    }
}