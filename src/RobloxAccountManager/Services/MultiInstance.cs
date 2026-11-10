namespace RobloxAccountManager.Services;

/// <summary>
/// Roblox stops a second client by checking for a named singleton object at startup.
/// If this app owns that object first, every client that starts afterwards runs normally
/// side by side. Nothing in the Roblox client is modified.
/// Must be enabled BEFORE the first Roblox window opens.
/// </summary>
public sealed class MultiInstance : IDisposable
{
    private static readonly string[] Names = { "ROBLOX_singletonEvent", "ROBLOX_singletonMutex" };
    private readonly List<Mutex> _held = new();

    public bool IsEnabled => _held.Count > 0;

    /// <summary>Returns false if a Roblox client already owns the lock (close Roblox and try again).</summary>
    public bool Enable()
    {
        if (IsEnabled) return true;
        var ok = false;
        foreach (var name in Names)
        {
            var m = new Mutex(true, name, out var createdNew);
            if (createdNew) { _held.Add(m); ok = true; }
            else m.Dispose();
        }
        return ok;
    }

    public void Disable()
    {
        foreach (var m in _held)
        {
            try { m.ReleaseMutex(); } catch { }
            m.Dispose();
        }
        _held.Clear();
    }

    public void Dispose() => Disable();
}
