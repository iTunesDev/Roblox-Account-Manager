using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RobloxAccountManager.Services;

public record RobloxUser(long Id, string Name, string DisplayName);
public record Presence(bool InGame, long PlaceId, string? JobId);
public record PublicServer(string JobId, int Playing, int MaxPlayers, int? Ping = null);
public record PlaceInfo(long PlaceId, long UniverseId, string Name, string? IconUrl, long Playing);

/// <summary>Thin wrapper over the public Roblox web endpoints the official site and client use.</summary>
public class RobloxApi
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RobloxAccountManager/1.0");
        return c;
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string? cookie)
    {
        var r = new HttpRequestMessage(method, url);
        if (cookie != null) r.Headers.Add("Cookie", $".ROBLOSECURITY={cookie}");
        return r;
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>
    /// Sends an authenticated POST. Roblox answers the first POST with 403 and an x-csrf-token header;
    /// we retry once with that token, the same way the website does.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAsync(string url, string cookie, object? body, Action<HttpRequestMessage>? extra = null)
    {
        string? csrf = null;
        for (var attempt = 0; ; attempt++)
        {
            var req = Req(HttpMethod.Post, url, cookie);
            req.Headers.Referrer = new Uri("https://www.roblox.com/");
            if (csrf != null) req.Headers.Add("x-csrf-token", csrf);
            req.Content = body == null ? new StringContent("", Encoding.UTF8, "application/json") : Json(body);
            extra?.Invoke(req);

            var res = await Http.SendAsync(req);
            if (attempt == 0 && res.StatusCode == HttpStatusCode.Forbidden && res.Headers.TryGetValues("x-csrf-token", out var tokens))
            {
                csrf = tokens.First();
                res.Dispose();
                continue;
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                res.Dispose();
                throw new InvalidOperationException("This account's login has expired. Sign in to it again.");
            }
            return res;
        }
    }

    /// <summary>Returns the signed-in user for a cookie, or null if the cookie is no longer valid.</summary>
    public async Task<RobloxUser?> GetAuthenticatedUserAsync(string cookie)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie));
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        return new RobloxUser(doc.GetProperty("id").GetInt64(), doc.GetProperty("name").GetString() ?? "", doc.GetProperty("displayName").GetString() ?? "");
    }

    public async Task<string?> GetHeadshotUrlAsync(long userId)
    {
        try
        {
            var url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false";
            var doc = await Http.GetFromJsonAsync<JsonElement>(url);
            var data = doc.GetProperty("data");
            return data.GetArrayLength() > 0 ? data[0].GetProperty("imageUrl").GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Gets a one-time authentication ticket, which the Roblox player exchanges for a session.</summary>
    public async Task<string> GetAuthTicketAsync(string cookie)
    {
        using var res = await PostAsync("https://auth.roblox.com/v1/authentication-ticket", cookie, null,
            r => r.Headers.Add("RBXAuthenticationNegotiation", "1"));
        res.EnsureSuccessStatusCode();
        if (res.Headers.TryGetValues("rbx-authentication-ticket", out var ticket))
            return ticket.First();
        throw new InvalidOperationException("Roblox did not return a login ticket.");
    }

    /// <summary>
    /// Where the account is right now. An account can always see its own server (job) id,
    /// which is what lets the session joiner send other accounts to the same server.
    /// </summary>
    public Task<Presence> GetOwnPresenceAsync(string cookie, long userId) => GetPresenceAsync(cookie, userId);

    /// <summary>
    /// Where a user is, as seen by the account owning <paramref name="cookie"/>. The server id is only
    /// filled in when that user's privacy settings let this account join them.
    /// </summary>
    public async Task<Presence> GetPresenceAsync(string cookie, long userId)
    {
        using var res = await PostAsync("https://presence.roblox.com/v1/presence/users", cookie, new { userIds = new[] { userId } });
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var list = doc.GetProperty("userPresences");
        if (list.GetArrayLength() == 0) return new Presence(false, 0, null);
        var p = list[0];
        var inGame = p.TryGetProperty("userPresenceType", out var t) && t.GetInt32() == 2;
        long placeId = p.TryGetProperty("placeId", out var pid) && pid.ValueKind == JsonValueKind.Number ? pid.GetInt64() : 0;
        string? jobId = p.TryGetProperty("gameId", out var gid) && gid.ValueKind == JsonValueKind.String ? gid.GetString() : null;
        return new Presence(inGame && jobId != null, placeId, jobId);
    }

    /// <summary>One page of a place's public servers, plus the cursor for the next page.</summary>
    public async Task<(List<PublicServer> Servers, string? Next)> GetPublicServersAsync(long placeId, string? cursor)
    {
        var url = $"https://games.roblox.com/v1/games/{placeId}/servers/0?sortOrder=Desc&excludeFullGames=true&limit=100";
        if (cursor != null) url += "&cursor=" + Uri.EscapeDataString(cursor);
        using var res = await Http.GetAsync(url);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var list = new List<PublicServer>();
        foreach (var s in doc.GetProperty("data").EnumerateArray())
        {
            list.Add(new PublicServer(
                s.GetProperty("id").GetString() ?? "",
                s.TryGetProperty("playing", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt32() : 0,
                s.GetProperty("maxPlayers").GetInt32(),
                s.TryGetProperty("ping", out var pg) && pg.ValueKind == JsonValueKind.Number ? pg.GetInt32() : null));
        }
        var next = doc.TryGetProperty("nextPageCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
        return (list, next);
    }

    /// <summary>
    /// Asks Roblox's matchmaker for a server's network address without actually joining.
    /// Used to work out which region a public server is hosted in.
    /// </summary>
    public async Task<string?> GetServerAddressAsync(string cookie, long placeId, string jobId)
    {
        using var res = await PostAsync("https://gamejoin.roblox.com/v1/join-game-instance", cookie,
            new { placeId, gameId = jobId, isTeleport = false, gameJoinAttemptId = Guid.NewGuid().ToString() },
            r => { r.Headers.UserAgent.Clear(); r.Headers.UserAgent.ParseAdd("Roblox/WinInet"); });
        if (!res.IsSuccessStatusCode) return null;
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        if (!doc.TryGetProperty("joinScript", out var js) || js.ValueKind != JsonValueKind.Object) return null;
        if (js.TryGetProperty("UdmuxEndpoints", out var ud) && ud.ValueKind == JsonValueKind.Array && ud.GetArrayLength() > 0
            && ud[0].TryGetProperty("Address", out var addr))
            return addr.GetString();
        return js.TryGetProperty("MachineAddress", out var ma) ? ma.GetString() : null;
    }

    /// <summary>Name, icon and player count for a place.</summary>
    public async Task<PlaceInfo?> GetPlaceInfoAsync(long placeId)
    {
        try
        {
            var u = await Http.GetFromJsonAsync<JsonElement>($"https://apis.roblox.com/universes/v1/places/{placeId}/universe");
            var universeId = u.GetProperty("universeId").GetInt64();
            var g = await Http.GetFromJsonAsync<JsonElement>($"https://games.roblox.com/v1/games?universeIds={universeId}");
            var game = g.GetProperty("data")[0];
            string? icon = null;
            try
            {
                var t = await Http.GetFromJsonAsync<JsonElement>($"https://thumbnails.roblox.com/v1/games/icons?universeIds={universeId}&size=150x150&format=Png&isCircular=false");
                icon = t.GetProperty("data")[0].GetProperty("imageUrl").GetString();
            }
            catch { }
            return new PlaceInfo(placeId, universeId, game.GetProperty("name").GetString() ?? $"Place {placeId}", icon,
                game.TryGetProperty("playing", out var pl) && pl.ValueKind == JsonValueKind.Number ? pl.GetInt64() : 0);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The account's Robux balance, or null if it couldn't be read.</summary>
    public async Task<long?> GetRobuxAsync(string cookie, long userId)
    {
        try
        {
            using var res = await Http.SendAsync(Req(HttpMethod.Get, $"https://economy.roblox.com/v1/users/{userId}/currency", cookie));
            if (!res.IsSuccessStatusCode) return null;
            var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
            return doc.GetProperty("robux").GetInt64();
        }
        catch { return null; }
    }

    public async Task<bool?> GetPremiumAsync(string cookie, long userId)
    {
        try
        {
            using var res = await Http.SendAsync(Req(HttpMethod.Get, $"https://premiumfeatures.roblox.com/v1/users/{userId}/validate-membership", cookie));
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync();
            return bool.TryParse(text.Trim(), out var b) ? b : null;
        }
        catch { return null; }
    }

    public async Task<long?> GetUserIdByUsernameAsync(string username)
    {
        using var res = await Http.PostAsJsonAsync("https://users.roblox.com/v1/usernames/users",
            new { usernames = new[] { username.Trim() }, excludeBannedUsers = true });
        if (!res.IsSuccessStatusCode) return null;
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = doc.GetProperty("data");
        return data.GetArrayLength() > 0 ? data[0].GetProperty("id").GetInt64() : null;
    }
}
