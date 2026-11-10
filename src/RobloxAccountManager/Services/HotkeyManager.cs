using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RobloxAccountManager.Services;

/// <summary>System-wide hotkeys (work even when the app isn't focused).</summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, NoRepeat = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5100;

    public HotkeyManager(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)!;
        _source.AddHook(Hook);
    }

    /// <summary>Returns false if another app already uses this combination.</summary>
    public bool Register(uint modifiers, System.Windows.Input.Key key, Action action)
    {
        var vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | NoRepeat, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(Hook);
    }
}
