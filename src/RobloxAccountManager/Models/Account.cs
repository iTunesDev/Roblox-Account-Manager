using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RobloxAccountManager.Models;

public enum AccountStatus { Unknown, Valid, Invalid, Checking }

public class Account : INotifyPropertyChanged
{
    private string _username = "";
    private string _displayName = "";
    private string _alias = "";
    private string _notes = "";
    private string? _avatarUrl;
    private AccountStatus _status = AccountStatus.Unknown;
    private string _region = "Any";
    private bool _lowResource;
    private int? _runningPid;
    private string _group = "";
    private bool _autoRejoin;
    private long? _robux;
    private bool _premium;
    private double _cpu;
    private double _memoryMb;
    private DateTime? _startedAt;

    public Guid Id { get; set; } = Guid.NewGuid();
    public long UserId { get; set; }

    public string Username { get => _username; set => Set(ref _username, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Alias { get => _alias; set { Set(ref _alias, value); OnChanged(nameof(Title)); } }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public string? AvatarUrl { get => _avatarUrl; set => Set(ref _avatarUrl, value); }

    /// <summary>Preferred server region ("Any" = let Roblox choose).</summary>
    public string Region { get => _region; set { Set(ref _region, value); OnChanged(nameof(StatusText)); } }

    /// <summary>Open this account's client in low-resource (AFK) mode.</summary>
    public bool LowResource { get => _lowResource; set { Set(ref _lowResource, value); OnChanged(nameof(StatusText)); } }

    /// <summary>Group or tag, e.g. "AFK squad". Used to filter the account list.</summary>
    public string Group { get => _group; set => Set(ref _group, value); }

    /// <summary>Relaunch automatically if the client closes or gets disconnected.</summary>
    public bool AutoRejoin { get => _autoRejoin; set => Set(ref _autoRejoin, value); }

    public long? Robux { get => _robux; set { Set(ref _robux, value); OnChanged(nameof(RobuxText)); } }
    public bool Premium { get => _premium; set => Set(ref _premium, value); }

    [JsonIgnore] public string RobuxText => Robux is long r ? $"R$ {r:N0}" : "R$ –";

    // ----- Runtime-only state (not saved) -----

    [JsonIgnore] public Services.GameTarget? LastTarget { get; set; }
    /// <summary>True when the user closed the window on purpose, so auto-rejoin leaves it alone.</summary>
    [JsonIgnore] public bool UserClosed { get; set; }
    /// <summary>When this app last launched the account; used to match a Roblox window that appears late.</summary>
    [JsonIgnore] public DateTime? LaunchedAt { get; set; }
    [JsonIgnore] public int RejoinAttempts { get; set; }
    [JsonIgnore] public int NotInGameStrikes { get; set; }
    [JsonIgnore] public DateTime? RejoinAfter { get; set; }
    [JsonIgnore] public TimeSpan LastCpuTime { get; set; }
    [JsonIgnore] public DateTime LastCpuSample { get; set; }

    [JsonIgnore] public double Cpu { get => _cpu; set => Set(ref _cpu, value); }
    [JsonIgnore] public double MemoryMb { get => _memoryMb; set => Set(ref _memoryMb, value); }
    [JsonIgnore] public DateTime? StartedAt { get => _startedAt; set { Set(ref _startedAt, value); OnChanged(nameof(UptimeText)); } }
    [JsonIgnore] public string UptimeText => StartedAt is DateTime t ? FormatSpan(DateTime.Now - t) : "–";
    [JsonIgnore] public bool IsRunning => RunningPid != null;

    public void RaiseUptime() => OnChanged(nameof(UptimeText));

    private static string FormatSpan(TimeSpan s) => s.TotalHours >= 1 ? $"{(int)s.TotalHours}h {s.Minutes}m" : $"{s.Minutes}m {s.Seconds}s";

    [JsonIgnore]
    public int? RunningPid { get => _runningPid; set { Set(ref _runningPid, value); OnChanged(nameof(StatusText)); OnChanged(nameof(IsRunning)); } }

    // ----- Password vault (stored only inside the DPAPI-encrypted file, like the cookie) -----
    private string _loginName = "";
    private string _password = "";
    /// <summary>What you type to sign in: username, email or phone.</summary>
    public string LoginName { get => _loginName; set { Set(ref _loginName, value); OnChanged(nameof(HasPassword)); } }
    public string Password { get => _password; set { Set(ref _password, value); OnChanged(nameof(HasPassword)); } }
    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(Password);

    /// <summary>The .ROBLOSECURITY cookie. Only ever written to disk inside the DPAPI-encrypted store.</summary>
    public string Cookie { get; set; } = "";

    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? LastValidated { get; set; }

    [JsonIgnore]
    public AccountStatus Status { get => _status; set { Set(ref _status, value); OnChanged(nameof(StatusText)); } }

    [JsonIgnore]
    public string StatusText
    {
        get
        {
            var s = Status switch
            {
                AccountStatus.Valid => "Signed in",
                AccountStatus.Invalid => "Login expired",
                AccountStatus.Checking => "Checking...",
                _ => "Not checked",
            };
            if (RunningPid != null) return "Running";
            if (Status == AccountStatus.Valid && LastValidated is DateTime v && DateTime.Now - v > TimeSpan.FromDays(3)) return "Check soon";
            return s;
        }
    }

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(Alias) ? Username : $"{Alias} ({Username})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
        if (name is nameof(Username)) OnChanged(nameof(Title));
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
