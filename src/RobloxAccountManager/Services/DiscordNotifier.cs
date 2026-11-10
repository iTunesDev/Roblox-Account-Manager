using System.Net.Http;
using System.Net.Http.Json;

namespace RobloxAccountManager.Services;

/// <summary>Posts short status messages to a Discord webhook you create in your own server.</summary>
public class DiscordNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" &&
        (u.Host.EndsWith("discord.com") || u.Host.EndsWith("discordapp.com")) && u.AbsolutePath.StartsWith("/api/webhooks/");

    public async Task SendAsync(string webhookUrl, string title, string message, int color)
    {
        if (!IsValidUrl(webhookUrl)) return;
        try
        {
            var body = new
            {
                username = "Roblox Account Manager",
                embeds = new[] { new { title, description = message, color, timestamp = DateTime.UtcNow.ToString("o") } },
            };
            using var _ = await Http.PostAsJsonAsync(webhookUrl, body);
        }
        catch
        {
            // Notifications are best effort.
        }
    }

    public const int Red = 0xE5484D;
    public const int Green = 0x30A46C;
    public const int Blue = 0x3B82F6;
}
