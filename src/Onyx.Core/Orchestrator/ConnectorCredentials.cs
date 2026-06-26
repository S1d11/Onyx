namespace Ollama2.Orchestrator;

/// <summary>
/// Baked-in OAuth credentials for connectors.
///
/// These client IDs are PUBLIC — they're meant to be embedded in desktop apps.
/// OAuth client IDs are not secrets; they're identifiers. The security comes from
/// the redirect URI (for browser flow) or the device flow (no secret at all).
///
/// To set up:
///   1. Google: Go to https://console.cloud.google.com/apis/credentials
///      - Create a project (or use existing)
///      - Enable Gmail API + Google Drive API
///      - Create OAuth 2.0 Client ID (type: Desktop app)
///      - Copy the Client ID and Client Secret below
///
///   2. GitHub: Go to https://github.com/settings/developers
///      - Click "New OAuth App"
///      - Homepage URL: https://github.com/S1d11/Onyx
///      - Authorization callback URL: http://localhost:8888/callback
///      - Copy the Client ID below (no secret needed for device flow)
///
///   3. Future apps: Add their credentials here and implement the IConnectorAuth interface.
/// </summary>
public static class ConnectorCredentials
{
    // ---- Google ----
    // Create at: https://console.cloud.google.com/apis/credentials
    // Type: Desktop app
    // Scopes: gmail.readonly, gmail.send, drive
    public const string GoogleClientId = "PASTE_YOUR_GOOGLE_CLIENT_ID.apps.googleusercontent.com";
    public const string GoogleClientSecret = "PASTE_YOUR_GOOGLE_CLIENT_SECRET";

    // ---- GitHub ----
    // Create at: https://github.com/settings/developers
    // Type: OAuth App (device flow — no secret needed)
    // Scopes: repo, read:org, user
    public const string GitHubClientId = "Ov23liM6T0io5K7znJBD";

    /// <summary>Check if Google credentials are configured (not placeholder).</summary>
    public static bool GoogleConfigured =>
        !GoogleClientId.StartsWith("PASTE_") && !GoogleClientSecret.StartsWith("PASTE_");

    /// <summary>Check if GitHub credentials are configured (not placeholder).</summary>
    public static bool GitHubConfigured =>
        !GitHubClientId.StartsWith("PASTE_");
}
