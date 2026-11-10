using System.Text.RegularExpressions;

namespace RobloxAccountManager.Services;

/// <summary>What to join: a place, optionally a specific server (job id) or a private server link code.</summary>
public record GameTarget(long PlaceId, string? JobId = null, string? PrivateServerLinkCode = null)
{
    private static readonly Regex GamesUrl = new(@"roblox\.com/(?:[a-z]{2}/)?games/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex PlaceIdParam = new(@"[?&]placeId=(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex LinkCodeParam = new(@"[?&]privateServerLinkCode=([\w-]+)", RegexOptions.IgnoreCase);
    private static readonly Regex GameInstanceParam = new(@"[?&](?:gameInstanceId|jobId)=([0-9a-f-]{36})", RegexOptions.IgnoreCase);

    /// <summary>
    /// Accepts a place ID ("920587237"), a game link ("https://www.roblox.com/games/920587237/Name"),
    /// or a private server link ("...games/920587237/Name?privateServerLinkCode=XXXX").
    /// </summary>
    public static bool TryParse(string input, string? jobId, out GameTarget? target, out string error)
    {
        target = null;
        error = "";
        input = input.Trim();
        jobId = string.IsNullOrWhiteSpace(jobId) ? null : jobId.Trim();

        if (input.Length == 0) { error = "Enter a place ID or game link."; return false; }

        long placeId;
        if (long.TryParse(input, out placeId)) { }
        else if (GamesUrl.Match(input) is { Success: true } g) placeId = long.Parse(g.Groups[1].Value);
        else if (PlaceIdParam.Match(input) is { Success: true } p) placeId = long.Parse(p.Groups[1].Value);
        else if (input.Contains("roblox.com/share", StringComparison.OrdinalIgnoreCase))
        {
            error = "Share links (roblox.com/share?...) aren't supported yet. Open the link in a browser and copy the address it redirects to.";
            return false;
        }
        else { error = "Couldn't find a place ID in that link."; return false; }

        var linkCode = LinkCodeParam.Match(input) is { Success: true } l ? l.Groups[1].Value : null;
        if (jobId == null && GameInstanceParam.Match(input) is { Success: true } j) jobId = j.Groups[1].Value;

        target = new GameTarget(placeId, jobId, linkCode);
        return true;
    }

    public string PlaceLauncherUrl(long browserTrackerId)
    {
        var baseUrl = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";
        if (PrivateServerLinkCode != null)
            return $"{baseUrl}?request=RequestPrivateGame&browserTrackerId={browserTrackerId}&placeId={PlaceId}&linkCode={PrivateServerLinkCode}";
        if (JobId != null)
            return $"{baseUrl}?request=RequestGameJob&browserTrackerId={browserTrackerId}&placeId={PlaceId}&gameId={JobId}&isPlayTogetherGame=false";
        return $"{baseUrl}?request=RequestGame&browserTrackerId={browserTrackerId}&placeId={PlaceId}&isPlayTogetherGame=false";
    }
}
