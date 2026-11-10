using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;

namespace RobloxAccountManager.Services;

public record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, string PageUrl);

/// <summary>
/// Checks GitHub Releases for a newer build and installs it: downloads the new exe next to the
/// current one, swaps them (Windows lets a running exe be renamed, not overwritten) and restarts.
/// </summary>
public class Updater
{
    /// <summary>The public GitHub repo whose releases carry RobloxAccountManager.exe.</summary>
    public const string ReleasesRepo = "iTunesDev/Roblox-Account-Manager";
    private const string AssetName = "RobloxAccountManager.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAccountManager-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Returns the newest release if it's newer than this build, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        using var res = await Http.GetAsync($"https://api.github.com/repos/{ReleasesRepo}/releases/latest");
        if (!res.IsSuccessStatusCode) return null; // no releases yet, private repo, or offline
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        var tag = doc.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= CurrentVersion) return null;

        var asset = doc.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind != JsonValueKind.Object) return null;

        return new UpdateInfo(latest, tag,
            doc.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            asset.GetProperty("browser_download_url").GetString()!,
            doc.GetProperty("html_url").GetString() ?? $"https://github.com/{ReleasesRepo}/releases");
    }

    /// <summary>Downloads and installs the update, then starts the new version. The caller should exit right after.</summary>
    public async Task InstallAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        var exe = SystemIntegration.ExePath;
        var dir = Path.GetDirectoryName(exe)!;
        var newPath = Path.Combine(dir, "RobloxAccountManager.new.exe");
        var oldPath = Path.Combine(dir, "RobloxAccountManager.old.exe");

        using (var res = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var src = await res.Content.ReadAsStreamAsync();
            await using var dst = File.Create(newPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }

        // Sanity check: a real build is several MB and starts with the "MZ" exe header.
        var info = new FileInfo(newPath);
        using (var f = File.OpenRead(newPath))
        {
            if (info.Length < 1_000_000 || f.ReadByte() != 'M' || f.ReadByte() != 'Z')
            {
                f.Close();
                File.Delete(newPath);
                throw new InvalidOperationException("The downloaded update looks damaged. Try again later.");
            }
        }

        if (File.Exists(oldPath)) File.Delete(oldPath);
        File.Move(exe, oldPath);   // allowed while running
        File.Move(newPath, exe);
        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
    }

    /// <summary>Removes the previous version left behind by an update.</summary>
    public static void CleanUpOldVersion()
    {
        try
        {
            var old = Path.Combine(Path.GetDirectoryName(SystemIntegration.ExePath)!, "RobloxAccountManager.old.exe");
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* still locked for a moment after restart; next launch will get it */ }
    }
}
