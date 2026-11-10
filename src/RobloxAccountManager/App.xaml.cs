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
            if (ReplaceOlderCopy())
            {
                // An older version was running (usually hidden in the tray). It has gone, so we take over.
                _single.Dispose();
                _single = new SingleInstance(dataFolder);
            }
            if (!_single.IsFirst)
            {
                // Same version already running (e.g. a preset shortcut was double-clicked): hand over and quit.
                _single.Forward(e.Args.Length == 0 ? new[] { "--show" } : e.Args);
                Shutdown();
                return;
            }
        }

        var window = new MainWindow(e.Args);
        MainWindow = window;
        _single.Listen(args => Dispatcher.BeginInvoke(() => window.HandleArgs(args)));
        if (!SystemIntegration.IsStartup(e.Args)) window.Show();
        else window.StartHidden();
    }

    /// <summary>
    /// If the copy already running is an older version, asks it to quit (or ends it if it's too old to
    /// understand) so this newer one can start. Returns true if an older copy was closed.
    /// </summary>
    private bool ReplaceOlderCopy()
    {
        var me = Environment.ProcessId;
        var mine = Updater.CurrentVersion;
        var older = new List<System.Diagnostics.Process>();
        foreach (var name in new[] { "RobloxAccountManager", "RobloxAccountManager.old" })
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                if (p.Id == me) { p.Dispose(); continue; }
                Version? theirs = null;
                try { Version.TryParse(p.MainModule?.FileVersionInfo.FileVersion, out theirs); } catch { }
                if (theirs == null || new Version(theirs.Major, theirs.Minor, Math.Max(0, theirs.Build)) < mine) older.Add(p);
                else p.Dispose();
            }
        }
        if (older.Count == 0) return false;

        _single!.Forward(new[] { "--exit-for-update" });
        foreach (var p in older)
        {
            using (p)
            {
                try
                {
                    if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
        }
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
