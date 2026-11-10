using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RobloxAccountManager.Services;

/// <summary>
/// Low-resource mode for idle/AFK clients. Only uses normal Windows process controls
/// (window state, priority, CPU affinity, working-set trimming). The Roblox client itself is untouched.
/// </summary>
public static class ClientOptimizer
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private const int SW_MINIMIZE = 6;
    private static int _nextCore;

    /// <summary>Applies low-resource settings once the client's window exists. Safe to call more than once.</summary>
    public static async Task ApplyAsync(int pid, int coresPerClient, bool minimize)
    {
        try
        {
            using var p = Process.GetProcessById(pid);

            p.PriorityClass = ProcessPriorityClass.BelowNormal;
            p.ProcessorAffinity = NextAffinity(coresPerClient);

            if (minimize)
            {
                // The window shows up a few seconds after the process starts.
                for (var i = 0; i < 30; i++)
                {
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero) { ShowWindow(p.MainWindowHandle, SW_MINIMIZE); break; }
                    await Task.Delay(1000);
                }
            }
        }
        catch
        {
            // Process exited or access was denied; nothing to do.
        }
    }

    /// <summary>Asks Windows to page out memory the client isn't actively using. Cheap and safe to repeat.</summary>
    public static void TrimMemory(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            EmptyWorkingSet(p.Handle);
        }
        catch { }
    }

    /// <summary>Spreads clients round-robin across cores so they don't all pile onto core 0.</summary>
    private static IntPtr NextAffinity(int cores)
    {
        var total = Math.Min(Environment.ProcessorCount, 64);
        cores = Math.Clamp(cores, 1, total);
        long mask = 0;
        for (var i = 0; i < cores; i++)
        {
            mask |= 1L << ((_nextCore + i) % total);
        }
        _nextCore = (_nextCore + cores) % total;
        return new IntPtr(mask);
    }
}
