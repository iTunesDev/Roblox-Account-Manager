using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace RobloxAccountManager.Services;

/// <summary>Suggests usernames (checked against Roblox's own availability check) and strong passwords.</summary>
public static class CredentialGenerator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string[] Adjectives =
    {
        "Swift", "Silent", "Lucky", "Cosmic", "Frosty", "Golden", "Hidden", "Mighty", "Neon", "Pixel", "Quiet", "Rapid",
        "Shadow", "Sunny", "Turbo", "Velvet", "Wild", "Zesty", "Brave", "Clever", "Electric", "Fuzzy", "Jolly", "Lunar",
        "Mystic", "Noble", "Rusty", "Solar", "Stormy", "Tiny", "Witty", "Crimson", "Azure", "Amber", "Arctic", "Blazing",
    };

    private static readonly string[] Nouns =
    {
        "Falcon", "Panda", "Comet", "Otter", "Wolf", "Rocket", "Tiger", "Fox", "Dragon", "Penguin", "Raven", "Koala",
        "Meteor", "Ninja", "Pilot", "Ranger", "Golem", "Wizard", "Shark", "Badger", "Lynx", "Phoenix", "Yeti", "Gecko",
        "Hawk", "Moose", "Orbit", "Pebble", "Quasar", "Sprout", "Taco", "Voyager", "Walrus", "Beacon", "Cactus", "Drift",
    };

    private static int R(int max) => RandomNumberGenerator.GetInt32(max);

    public static string NewUsername()
    {
        var name = Adjectives[R(Adjectives.Length)] + Nouns[R(Nouns.Length)];
        var style = R(3);
        name = style switch
        {
            0 => name + R(10, 9999),
            1 => name + "_" + R(10, 999),
            _ => name + R(100, 99999),
        };
        return name.Length > 20 ? name[..20] : name;
    }

    private static int R(int min, int max) => RandomNumberGenerator.GetInt32(min, max + 1);

    /// <summary>A 16-character password with upper, lower, digits and symbols, from a cryptographic RNG.</summary>
    public static string NewPassword(int length = 16)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!@#$%^&*?-_+=";
        var all = upper + lower + digits + symbols;
        var chars = new List<char>
        {
            upper[R(upper.Length)], lower[R(lower.Length)], digits[R(digits.Length)], symbols[R(symbols.Length)],
        };
        while (chars.Count < length) chars.Add(all[R(all.Length)]);
        // Shuffle so the guaranteed characters aren't always first.
        for (var i = chars.Count - 1; i > 0; i--) { var j = R(i + 1); (chars[i], chars[j]) = (chars[j], chars[i]); }
        return new string(chars.ToArray());
    }

    /// <summary>Asks Roblox whether a username can be used. Returns (ok, message).</summary>
    public static async Task<(bool Ok, string Message)> CheckUsernameAsync(string username)
    {
        try
        {
            var url = $"https://auth.roblox.com/v1/usernames/validate?request.username={Uri.EscapeDataString(username)}&request.birthday=2000-01-01T00:00:00.000Z&request.context=Signup";
            var doc = await Http.GetFromJsonAsync<JsonElement>(url);
            var code = doc.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            var msg = doc.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return (code == 0, code == 0 ? "Available" : (msg.Length > 0 ? msg : "Not available"));
        }
        catch
        {
            return (true, "Couldn't check right now");
        }
    }

    /// <summary>Generates usernames until Roblox says one is free (up to a few tries).</summary>
    public static async Task<string> NewAvailableUsernameAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            var name = NewUsername();
            if ((await CheckUsernameAsync(name)).Ok) return name;
        }
        return NewUsername();
    }
}
