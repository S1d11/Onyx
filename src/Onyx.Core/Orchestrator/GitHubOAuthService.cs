using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// GitHub OAuth using the Device Flow (RFC 8628).
///
/// This is the same flow Devin CLI and GitHub CLI use:
///   1. App requests a device code from GitHub
///   2. User goes to github.com/login/device and enters the code
///   3. App polls GitHub until the user authorizes
///   4. App receives an access token
///
/// No client secret needed — only the public client ID.
/// </summary>
public class GitHubOAuthService
{
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string DeviceAuthUrl = "https://github.com/login/device";
    private const string Scopes = "repo read:org user";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly ConfigService _config;

    public GitHubOAuthService(ConfigService config)
    {
        _config = config;
    }

    public bool IsConnected => !string.IsNullOrEmpty(_config.Current.GitHubToken);

    /// <summary>
    /// Start the device flow. Returns the device code + user code for the UI to display.
    /// Then call PollForTokenAsync to wait for the user to authorize.
    /// </summary>
    public async Task<DeviceCodeResponse?> RequestDeviceCodeAsync(CancellationToken ct = default)
    {
        if (!ConnectorCredentials.GitHubConfigured)
            return null;

        var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ConnectorCredentials.GitHubClientId,
                ["scope"] = Scopes,
            }),
        };
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return null;

        var doc = JsonDocument.Parse(json);
        return new DeviceCodeResponse
        {
            DeviceCode = doc.RootElement.GetProperty("device_code").GetString() ?? "",
            UserCode = doc.RootElement.GetProperty("user_code").GetString() ?? "",
            VerificationUri = doc.RootElement.GetProperty("verification_uri").GetString() ?? DeviceAuthUrl,
            ExpiresIn = doc.RootElement.GetProperty("expires_in").GetInt32(),
            Interval = doc.RootElement.TryGetProperty("interval", out var i) ? i.GetInt32() : 5,
        };
    }

    /// <summary>
    /// Poll GitHub for the access token. Call this after displaying the user code.
    /// Returns the access token, or null if the user didn't authorize in time.
    /// </summary>
    public async Task<string?> PollForTokenAsync(string deviceCode, int interval, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMinutes(15); // device codes typically expire in 15 min

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(interval * 1000, ct);

            var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ConnectorCredentials.GitHubClientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }),
            };
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var resp = await Http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch { continue; } // Not JSON — skip and keep polling

            // Check for access_token (success)
            if (doc.RootElement.TryGetProperty("access_token", out var token))
            {
                var accessToken = token.GetString()!;
                _config.Current.GitHubToken = accessToken;
                _config.Save();
                return accessToken;
            }

            // Check for error
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var error = err.GetString();
                switch (error)
                {
                    case "authorization_pending":
                        // User hasn't entered the code yet — keep polling
                        continue;
                    case "slow_down":
                        interval += 5;
                        continue;
                    case "expired_token":
                    case "access_denied":
                    default:
                        return null; // User denied or code expired
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Convenience method: request device code, open browser, poll for token.
    /// Returns the access token or null.
    /// </summary>
    public async Task<string?> ConnectAsync(Action<DeviceCodeResponse>? onCodeReceived = null, CancellationToken ct = default)
    {
        var deviceCode = await RequestDeviceCodeAsync(ct);
        if (deviceCode == null) return null;

        // Notify UI to show the code
        onCodeReceived?.Invoke(deviceCode);

        // Open browser to the verification URL
        OpenBrowser(deviceCode.VerificationUri);

        // Poll until authorized
        return await PollForTokenAsync(deviceCode.DeviceCode, deviceCode.Interval, ct);
    }

    /// <summary>Disconnect — clear the stored token.</summary>
    public void Disconnect()
    {
        _config.Current.GitHubToken = null;
        _config.Save();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch { }
    }
}

/// <summary>Response from GitHub's device code endpoint.</summary>
public class DeviceCodeResponse
{
    public string DeviceCode { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string VerificationUri { get; set; } = "";
    public int ExpiresIn { get; set; }
    public int Interval { get; set; } = 5;
}
