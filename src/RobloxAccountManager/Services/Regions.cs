using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace RobloxAccountManager.Services;

public static class Regions
{
    public const string Any = "Any";

    public static readonly string[] All =
    {
        Any, "US East", "US Central", "US West", "UK", "Europe", "Asia", "Oceania", "South America",
    };

    /// <summary>Buckets a location into one of the regions above.</summary>
    public static string Classify(string countryCode, double lon)
    {
        switch (countryCode)
        {
            case "US":
            case "CA":
                return lon > -90 ? "US East" : lon > -105 ? "US Central" : "US West";
            case "GB":
            case "IE":
                return "UK";
            case "AU":
            case "NZ":
                return "Oceania";
            case "BR":
            case "AR":
            case "CL":
            case "CO":
            case "PE":
                return "South America";
            case "MX":
                return "US Central";
        }
        // Rough fallback by longitude for anything not listed above.
        if (lon >= -30 && lon < 45) return "Europe";
        if (lon >= 45) return "Asia";
        return "US East";
    }
}

/// <summary>
/// Looks up which region a Roblox server IP sits in, using the free ip-api.com batch service.
/// Only Roblox server IPs are sent, never anything about you or your accounts. Results are cached.
/// </summary>
public class GeoLocator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, string> _cache = new();

    public async Task<Dictionary<string, string>> LocateAsync(IEnumerable<string> ips)
    {
        var result = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var ip in ips.Distinct())
        {
            if (_cache.TryGetValue(ip, out var r)) result[ip] = r;
            else missing.Add(ip);
        }

        foreach (var chunk in missing.Chunk(100))
        {
            try
            {
                using var res = await Http.PostAsJsonAsync("http://ip-api.com/batch?fields=status,query,countryCode,lon", chunk);
                res.EnsureSuccessStatusCode();
                var arr = await res.Content.ReadFromJsonAsync<JsonElement>();
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.GetProperty("status").GetString() != "success") continue;
                    var ip = e.GetProperty("query").GetString()!;
                    var region = Regions.Classify(e.GetProperty("countryCode").GetString() ?? "", e.GetProperty("lon").GetDouble());
                    _cache[ip] = region;
                    result[ip] = region;
                }
            }
            catch
            {
                // Region lookup is best effort; servers we can't place are just skipped.
            }
        }
        return result;
    }
}

/// <summary>Finds public servers of a place hosted in a chosen region.</summary>
public class RegionServerFinder
{
    private readonly RobloxApi _api;
    private readonly GeoLocator _geo;

    public RegionServerFinder(RobloxApi api, GeoLocator geo)
    {
        _api = api;
        _geo = geo;
    }

    public record Found(string JobId, string Region, int FreeSlots);

    /// <summary>Works out the region of each given server. Servers that can't be placed are left out.</summary>
    public async Task<Dictionary<string, string>> LocateServersAsync(string cookie, long placeId, IEnumerable<string> jobIds,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var ids = jobIds.ToList();
        var addresses = new Dictionary<string, string>();
        for (var i = 0; i < ids.Count && !ct.IsCancellationRequested; i++)
        {
            progress?.Report($"Checking server regions ({i + 1}/{ids.Count})...");
            try
            {
                var ip = await _api.GetServerAddressAsync(cookie, placeId, ids[i]);
                if (ip != null) addresses[ids[i]] = ip;
            }
            catch (InvalidOperationException) { throw; }
            catch { /* skip this server */ }
            await Task.Delay(150, ct); // be gentle with Roblox's rate limits
        }
        var regions = await _geo.LocateAsync(addresses.Values);
        return addresses.Where(a => regions.ContainsKey(a.Value)).ToDictionary(a => a.Key, a => regions[a.Value]);
    }

    /// <summary>
    /// Scans up to <paramref name="maxServers"/> public servers and returns those in the wanted regions,
    /// roomiest first. <paramref name="cookie"/> is any signed-in account; it's only used to ask the
    /// matchmaker for server addresses.
    /// </summary>
    public async Task<List<Found>> FindAsync(string cookie, long placeId, ISet<string> wantedRegions,
        IProgress<string>? progress = null, int maxServers = 60, CancellationToken ct = default)
    {
        var servers = new List<PublicServer>();
        string? cursor = null;
        do
        {
            var (page, next) = await _api.GetPublicServersAsync(placeId, cursor);
            servers.AddRange(page);
            cursor = next;
        } while (cursor != null && servers.Count < maxServers && !ct.IsCancellationRequested);
        servers = servers.Take(maxServers).ToList();

        var regions = await LocateServersAsync(cookie, placeId, servers.Select(s => s.JobId), progress, ct);
        return servers
            .Where(s => regions.TryGetValue(s.JobId, out var r) && wantedRegions.Contains(r))
            .Select(s => new Found(s.JobId, regions[s.JobId], s.MaxPlayers - s.Playing))
            .OrderByDescending(f => f.FreeSlots)
            .ToList();
    }
}
