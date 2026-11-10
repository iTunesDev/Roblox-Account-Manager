using System.ComponentModel;

namespace RobloxAccountManager.Models;

/// <summary>A public server as shown in the server browser.</summary>
public class ServerRow : INotifyPropertyChanged
{
    private string _region = "–";

    public string JobId { get; init; } = "";
    public int Playing { get; init; }
    public int MaxPlayers { get; init; }
    public int? Ping { get; init; }

    public string Players => $"{Playing} / {MaxPlayers}";
    public string PingText => Ping is int p ? $"{p} ms" : "–";
    public int FreeSlots => MaxPlayers - Playing;

    public string Region
    {
        get => _region;
        set { _region = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Region))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
