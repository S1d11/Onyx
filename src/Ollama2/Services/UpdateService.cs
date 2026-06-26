using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ollama2.Services;

/// <summary>
/// Checks GitHub releases for updates, downloads the installer, and handles installation.
/// </summary>
public class UpdateService
{
    private const string Owner = "S1d11";
    private const string Repo = "Ollama-2.0";
    private const string ApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = new HttpClient();
    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Ollama2.0-Updater/1.0");
        Http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public event EventHandler<UpdateStatusEventArgs>? StatusChanged;

    private void OnStatus(string status, double progress = 0, string? downloadUrl = null)
    {
        StatusChanged?.Invoke(this, new UpdateStatusEventArgs(status, progress, downloadUrl));
    }

    public static Version CurrentVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            return v ?? new Version(1, 0, 0, 0);
        }
    }

    /// <summary>Check if a newer release exists. Returns null if up to date or error.</summary>
    public async Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            OnStatus("Checking for updates...");
            var json = await Http.GetStringAsync(ApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var latest = ParseVersion(tagName);
            if (latest <= CurrentVersion)
            {
                OnStatus("You are on the latest version.");
                return null;
            }

            var assetUrl = "";
            var assetName = "";
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        assetName = name;
                        break;
                    }
                }
            }

            var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            OnStatus($"Update available: v{latest}");
            return new ReleaseInfo(latest, tagName, body, assetUrl, assetName);
        }
        catch (Exception ex)
        {
            OnStatus($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Download the installer to the update cache folder.</summary>
    public async Task<string?> DownloadUpdateAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(release.DownloadUrl)) return null;

        var updateDir = Path.Combine(App.DataDir, "updates");
        Directory.CreateDirectory(updateDir);
        var dest = Path.Combine(updateDir, release.AssetName);

        try
        {
            OnStatus($"Downloading {release.AssetName}...", 0, release.DownloadUrl);
            using var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var file = File.Create(dest);
            var buffer = new byte[65536];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await file.WriteAsync(buffer, 0, read, ct);
                totalRead += read;
                if (totalBytes > 0)
                    OnStatus("Downloading...", totalRead * 100.0 / totalBytes, release.DownloadUrl);
            }

            OnStatus("Download complete. Ready to install.", 100, release.DownloadUrl);
            return dest;
        }
        catch (Exception ex)
        {
            OnStatus($"Download failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Run the downloaded installer silently and exit the current app.</summary>
    public void InstallUpdate(string installerPath)
    {
        if (!File.Exists(installerPath)) return;
        try
        {
            OnStatus("Installing update...");
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
                UseShellExecute = true,
            });
            // Exit the current app so the installer can replace the .exe
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            OnStatus($"Install failed: {ex.Message}");
        }
    }

    /// <summary>Full flow: check → download → install (if user confirms).</summary>
    public async Task<bool> AutoUpdateAsync(CancellationToken ct = default)
    {
        var release = await CheckForUpdateAsync(ct);
        if (release == null || string.IsNullOrEmpty(release.DownloadUrl)) return false;

        var path = await DownloadUpdateAsync(release, ct);
        if (path == null) return false;

        InstallUpdate(path);
        return true;
    }

    private static Version ParseVersion(string tag)
    {
        var clean = tag.TrimStart('v', 'V');
        if (Version.TryParse(clean, out var v)) return v;
        return new Version(0, 0, 0, 0);
    }
}

public record ReleaseInfo(Version Version, string TagName, string Body, string DownloadUrl, string AssetName);

public class UpdateStatusEventArgs : EventArgs
{
    public string Status { get; }
    public double ProgressPercent { get; }
    public string? DownloadUrl { get; }

    public UpdateStatusEventArgs(string status, double progress, string? downloadUrl)
    {
        Status = status;
        ProgressPercent = progress;
        DownloadUrl = downloadUrl;
    }
}
