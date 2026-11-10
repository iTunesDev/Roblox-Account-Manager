using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace RobloxAccountManager.Services;

/// <summary>Start-with-Windows, desktop shortcuts and the command line the app understands.</summary>
public static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RobloxAccountManager";

    public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{ExePath}\" --startup");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Creates a shortcut on the desktop that launches a preset (the app opens it, or tells the running copy to).</summary>
    public static string CreatePresetShortcut(string presetName, Guid presetId)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var safe = string.Concat(presetName.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(desktop, $"{(safe.Length == 0 ? "Preset" : safe)} (Roblox).lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = ExePath;
            link.Arguments = $"--preset {presetId}";
            link.WorkingDirectory = Path.GetDirectoryName(ExePath);
            link.IconLocation = ExePath + ",0";
            link.Description = $"Launch the \"{presetName}\" preset";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        return path;
    }

    public static Guid? PresetFromArgs(string[] args)
    {
        var i = Array.IndexOf(args, "--preset");
        return i >= 0 && i + 1 < args.Length && Guid.TryParse(args[i + 1], out var id) ? id : null;
    }

    public static bool IsStartup(string[] args) => args.Contains("--startup");
}

/// <summary>
/// Keeps a single copy of the app running. A second launch (e.g. a preset shortcut) hands its
/// command line to the first copy and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\RobloxAccountManager.App";
    private const string EventName = @"Local\RobloxAccountManager.Activate";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _event;
    private readonly string _argsFile;
    private RegisteredWaitHandle? _wait;

    public bool IsFirst { get; }

    public SingleInstance(string dataFolder)
    {
        _mutex = new Mutex(true, MutexName, out var created);
        IsFirst = created;
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        Directory.CreateDirectory(dataFolder);
        _argsFile = Path.Combine(dataFolder, "pending-args.txt");
    }

    /// <summary>Called from the second copy: pass our arguments to the running one.</summary>
    public void Forward(string[] args)
    {
        File.WriteAllText(_argsFile, string.Join("\n", args));
        _event.Set();
    }

    /// <summary>Called from the first copy: run <paramref name="onActivate"/> whenever another copy forwards to us.</summary>
    public void Listen(Action<string[]> onActivate)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) =>
        {
            string[] args = Array.Empty<string>();
            try
            {
                if (File.Exists(_argsFile))
                {
                    args = File.ReadAllText(_argsFile).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    File.Delete(_argsFile);
                }
            }
            catch { }
            onActivate(args);
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
        _event.Dispose();
    }
}
