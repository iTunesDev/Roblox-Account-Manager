using System.Diagnostics;

namespace RobloxAccountManager.Services;

/// <summary>Starts the normal installed Roblox player through its roblox-player: protocol handler.</summary>
public class RobloxLauncher
{
    public const string ClientProcessName = "RobloxPlayerBeta";

    private readonly RobloxApi _api;
    private static readonly Random Rng = new();

    public RobloxLauncher(RobloxApi api) => _api = api;

    /// <summary>Launches the client and returns its process id once the window has started, or null if it didn't appear in time.</summary>
    public async Task<int?> LaunchAsync(string cookie, GameTarget target, TimeSpan? waitForClient = null)
    {
        var before = RunningClientIds();
        var ticket = await _api.GetAuthTicketAsync(cookie);
        var trackerId = Rng.NextInt64(100_000_000_000, 999_999_999_999);
        var launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var placeLauncher = Uri.EscapeDataString(target.PlaceLauncherUrl(trackerId));

        var uri = "roblox-player:1+launchmode:play" +
                  $"+gameinfo:{ticket}" +
                  $"+launchtime:{launchTime}" +
                  $"+placelauncherurl:{placeLauncher}" +
                  $"+browsertrackerid:{trackerId}" +
                  "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

        var deadline = DateTime.UtcNow + (waitForClient ?? TimeSpan.FromSeconds(45));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            var added = RunningClientIds().Except(before).ToList();
            if (added.Count > 0) return added.Max();
        }
        return null;
    }

    public static HashSet<int> RunningClientIds()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(ClientProcessName)) { set.Add(p.Id); p.Dispose(); }
        return set;
    }

    public static int CountRunningClients() => RunningClientIds().Count;

    public static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && p.ProcessName == ClientProcessName; }
        catch { return false; }
    }

    public static bool Close(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(); return true; }
        catch { return false; }
    }

    public static int CloseAllClients()
    {
        var n = 0;
        foreach (var p in Process.GetProcessesByName(ClientProcessName))
        {
            try { p.Kill(); n++; } catch { /* already exited or access denied */ }
            finally { p.Dispose(); }
        }
        return n;
    }
}
