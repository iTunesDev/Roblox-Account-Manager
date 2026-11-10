namespace RobloxAccountManager.Models;

public class AppSettings
{
    public bool MultiInstance { get; set; } = true;
    public int LaunchDelaySeconds { get; set; } = 8;
    public string LastPlace { get; set; } = "";
    public string LastJobId { get; set; } = "";

    /// <summary>Never run more than this many Roblox clients at once.</summary>
    public int MaxClients { get; set; } = 6;

    /// <summary>CPU cores each low-resource client may use.</summary>
    public int CoresPerClient { get; set; } = 1;

    public bool MinimizeLowResource { get; set; } = true;

    /// <summary>How often low-resource clients have their memory trimmed.</summary>
    public int TrimEveryMinutes { get; set; } = 2;

    // Auto-rejoin
    public int RejoinDelaySeconds { get; set; } = 15;
    public int MaxRejoinAttempts { get; set; } = 5;
    /// <summary>Also rejoin when the window is still open but the account has dropped out of the game (disconnect screen).</summary>
    public bool RejoinOnDisconnect { get; set; } = true;

    // Discord
    public string DiscordWebhookUrl { get; set; } = "";
    public bool NotifyOnDisconnect { get; set; } = true;
    public bool NotifyOnRejoin { get; set; } = true;

    // Window behaviour
    public bool CloseToTray { get; set; } = true;
    public bool HotkeysEnabled { get; set; } = true;
    public bool LightTheme { get; set; }
    public bool StartWithWindows { get; set; }
    public Guid? StartupPresetId { get; set; }
    public bool WindowTitles { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public string? SkippedVersion { get; set; }
}

public class Preset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Guid> AccountIds { get; set; } = new();
    public string Place { get; set; } = "";
    public string JobId { get; set; } = "";
    public bool JoinTogether { get; set; } = true;

    public string Summary(int accountCount) => $"{accountCount} account(s) · {(JoinTogether ? "same server" : "separate servers")}";
}

public class SavedGame
{
    public long PlaceId { get; set; }
    public string Name { get; set; } = "";
    public string? IconUrl { get; set; }
    public bool Favorite { get; set; }
    public DateTime LastPlayed { get; set; }
}

public class StoreData
{
    public int Version { get; set; } = 1;
    public List<Account> Accounts { get; set; } = new();
    public List<Preset> Presets { get; set; } = new();
    public List<SavedGame> Games { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}
