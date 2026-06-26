using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// Handles Google OAuth 2.0 flow for desktop apps and token refresh.
/// Uses loopback IP redirect (http://localhost:port) for the OAuth callback.
/// </summary>
public class GoogleOAuthService
{
    private const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    // Gmail + Drive scopes (read-only for Gmail, full for Drive)
    private const string Scopes = "https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/drive";

    private readonly ConfigService _config;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public GoogleOAuthService(ConfigService config)
    {
        _config = config;
    }

    public bool IsConnected => !string.IsNullOrEmpty(_config.Current.GoogleRefreshToken);

    /// <summary>Get a valid access token, refreshing if necessary.</summary>
    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        var cfg = _config.Current;
        if (string.IsNullOrEmpty(cfg.GoogleRefreshToken) || string.IsNullOrEmpty(cfg.GoogleClientId))
            return null;

        // Check if current token is still valid (with 60s buffer)
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrEmpty(cfg.GoogleAccessToken) && cfg.GoogleTokenExpiry > now + 60)
            return cfg.GoogleAccessToken;

        // Refresh the token
        return await RefreshTokenAsync(ct);
    }

    /// <summary>Refresh the access token using the stored refresh token.</summary>
    public async Task<string?> RefreshTokenAsync(CancellationToken ct = default)
    {
        var cfg = _config.Current;
        if (string.IsNullOrEmpty(cfg.GoogleRefreshToken) || string.IsNullOrEmpty(cfg.GoogleClientId) || string.IsNullOrEmpty(cfg.GoogleClientSecret))
            return null;

        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = cfg.GoogleClientId!,
                ["client_secret"] = cfg.GoogleClientSecret!,
                ["refresh_token"] = cfg.GoogleRefreshToken!,
                ["grant_type"] = "refresh_token",
            });

            var resp = await Http.PostAsync(TokenUrl, content, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return null;

            var doc = JsonDocument.Parse(json);
            var accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt64();

            cfg.GoogleAccessToken = accessToken;
            cfg.GoogleTokenExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;
            _config.Save();

            return accessToken;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Start the OAuth flow using baked-in credentials: open browser for user consent,
    /// listen on loopback for callback. Returns the refresh token, or null on failure.
    /// No user credential entry needed — just click connect and sign in.
    /// </summary>
    public async Task<string?> StartOAuthFlowAsync(CancellationToken ct = default)
    {
        if (!ConnectorCredentials.GoogleConfigured)
            return null;

        var clientId = ConnectorCredentials.GoogleClientId;
        var clientSecret = ConnectorCredentials.GoogleClientSecret;

        // Find a free port
        var listener = new HttpListener();
        var port = FindFreePort();
        var redirectUri = $"http://localhost:{port}/";
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        // Save credentials for token refresh later
        _config.Current.GoogleClientId = clientId;
        _config.Current.GoogleClientSecret = clientSecret;
        _config.Save();

        // Build auth URL
        var authUrl = $"{AuthUrl}?client_id={Uri.EscapeDataString(clientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&response_type=code" +
                      $"&scope={Uri.EscapeDataString(Scopes)}" +
                      $"&access_type=offline" +
                      $"&prompt=consent";

        // Open browser
        OpenBrowser(authUrl);

        // Wait for callback
        var ctx = await listener.GetContextAsync();
        var code = ctx.Request.QueryString["code"];
        var error = ctx.Request.QueryString["error"];

        // Respond to browser
        var responseHtml = string.IsNullOrEmpty(error)
            ? "<html><body style='font-family:system-ui;text-align:center;padding:60px'><h2>Authorization successful!</h2><p>You can close this window and return to Onyx.</p></body></html>"
            : $"<html><body style='font-family:system-ui;text-align:center;padding:60px'><h2>Authorization failed</h2><p>{error}</p></body></html>";
        var responseBytes = Encoding.UTF8.GetBytes(responseHtml);
        ctx.Response.ContentType = "text/html";
        ctx.Response.ContentLength64 = responseBytes.Length;
        ctx.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
        ctx.Response.OutputStream.Close();

        listener.Stop();

        if (!string.IsNullOrEmpty(error)) return null;
        if (string.IsNullOrEmpty(code)) return null;

        // Exchange code for tokens
        return await ExchangeCodeForTokensAsync(code, redirectUri, ct);
    }

    /// <summary>Exchange authorization code for access + refresh tokens.</summary>
    private async Task<string?> ExchangeCodeForTokensAsync(string code, string redirectUri, CancellationToken ct)
    {
        var cfg = _config.Current;
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = cfg.GoogleClientId!,
                ["client_secret"] = cfg.GoogleClientSecret!,
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri,
            });

            var resp = await Http.PostAsync(TokenUrl, content, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return null;

            var doc = JsonDocument.Parse(json);
            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            var accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt64();

            if (string.IsNullOrEmpty(refreshToken)) return null;

            cfg.GoogleRefreshToken = refreshToken;
            cfg.GoogleAccessToken = accessToken;
            cfg.GoogleTokenExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn;
            _config.Save();

            return refreshToken;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Disconnect — clear all Google tokens.</summary>
    public void Disconnect()
    {
        _config.Current.GoogleClientId = null;
        _config.Current.GoogleClientSecret = null;
        _config.Current.GoogleRefreshToken = null;
        _config.Current.GoogleAccessToken = null;
        _config.Current.GoogleTokenExpiry = 0;
        _config.Save();
    }

    private static int FindFreePort()
    {
        using var sock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)sock.LocalEndPoint!).Port;
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
