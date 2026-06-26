namespace Ollama2;

/// <summary>
/// Platform-agnostic abstraction for sending messages from C# to the web UI.
/// Implemented by the platform-specific shell (WPF WebView2, MAUI WebView, etc.)
/// </summary>
public interface IBridgeHost
{
    /// <summary>Post a JSON payload to the web view's message handler.</summary>
    void PostMessage(string json);

    /// <summary>Show a native error dialog or notification.</summary>
    void ShowError(string message);

    /// <summary>Open a native folder browser. Returns null if cancelled or unsupported.</summary>
    string? BrowseFolder();
}
