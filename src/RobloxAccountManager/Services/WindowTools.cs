using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RobloxAccountManager.Services;

/// <summary>Arranges, hides and shows Roblox windows using standard Windows window calls.</summary>
public static class WindowTools
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(int action, int param, ref RECT rect, int winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hWnd, string text);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_RESTORE = 9, SPI_GETWORKAREA = 0x30;

    private static List<IntPtr> RobloxWindows(IEnumerable<int>? pids = null)
    {
        var wanted = pids?.ToHashSet();
        var list = new List<IntPtr>();
        foreach (var p in Process.GetProcessesByName(RobloxLauncher.ClientProcessName))
        {
            using (p)
            {
                if (wanted != null && !wanted.Contains(p.Id)) continue;
                if (p.MainWindowHandle != IntPtr.Zero) list.Add(p.MainWindowHandle);
                else if (HiddenWindows.TryGetValue(p.Id, out var h)) list.Add(h);
            }
        }
        return list;
    }

    // Hidden windows aren't reported as MainWindowHandle any more, so remember them.
    private static readonly Dictionary<int, IntPtr> HiddenWindows = new();
    public static bool AreHidden => HiddenWindows.Count > 0;

    /// <summary>Lays out every Roblox window in an even grid over the main screen's work area.</summary>
    public static int Tile()
    {
        ShowAll();
        var windows = RobloxWindows();
        if (windows.Count == 0) return 0;
        var area = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref area, 0);
        var width = area.Right - area.Left;
        var height = area.Bottom - area.Top;
        var cols = (int)Math.Ceiling(Math.Sqrt(windows.Count));
        var rows = (int)Math.Ceiling(windows.Count / (double)cols);
        int w = width / cols, h = height / rows;
        for (var i = 0; i < windows.Count; i++)
        {
            ShowWindow(windows[i], SW_RESTORE);
            MoveWindow(windows[i], area.Left + (i % cols) * w, area.Top + (i / cols) * h, w, h, true);
        }
        return windows.Count;
    }

    public static void HideAll()
    {
        foreach (var p in Process.GetProcessesByName(RobloxLauncher.ClientProcessName))
        {
            using (p)
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                HiddenWindows[p.Id] = p.MainWindowHandle;
                ShowWindow(p.MainWindowHandle, SW_HIDE);
            }
        }
    }

    public static void ShowAll()
    {
        foreach (var h in HiddenWindows.Values) ShowWindow(h, SW_SHOWNORMAL);
        HiddenWindows.Clear();
    }

    /// <summary>Puts the account name in the Roblox window's title bar so windows are easy to tell apart.</summary>
    public static void SetTitle(int pid, string title)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.MainWindowHandle != IntPtr.Zero) SetWindowText(p.MainWindowHandle, title);
        }
        catch { }
    }
}

/// <summary>Reads CPU and memory use of a client process.</summary>
public static class ProcessStats
{
    public static bool TrySample(int pid, ref TimeSpan lastCpu, ref DateTime lastSample, out double cpuPercent, out double memoryMb)
    {
        cpuPercent = 0;
        memoryMb = 0;
        try
        {
            using var p = Process.GetProcessById(pid);
            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            if (lastSample != default)
            {
                var wall = (now - lastSample).TotalMilliseconds;
                if (wall > 0) cpuPercent = (cpu - lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100;
            }
            lastCpu = cpu;
            lastSample = now;
            memoryMb = p.WorkingSet64 / 1024.0 / 1024.0;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
