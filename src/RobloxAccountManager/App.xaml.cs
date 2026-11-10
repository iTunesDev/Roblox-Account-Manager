using System.IO;
using System.Windows;
using RobloxAccountManager.Services;

namespace RobloxAccountManager;

public partial class App : Application
{
    private SingleInstance? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxAccountManager");
        _single = new SingleInstance(dataFolder);
        if (!_single.IsFirst)
        {
            // Already running (e.g. a preset shortcut was double-clicked): hand over and quit.
            _single.Forward(e.Args.Length == 0 ? new[] { "--show" } : e.Args);
            Shutdown();
            return;
        }

        var window = new MainWindow(e.Args);
        MainWindow = window;
        _single.Listen(args => Dispatcher.BeginInvoke(() => window.HandleArgs(args)));
        if (!SystemIntegration.IsStartup(e.Args)) window.Show();
        else window.StartHidden();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
