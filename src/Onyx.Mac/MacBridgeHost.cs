using Ollama2;

namespace Onyx.Mac;

/// <summary>
/// macOS-specific implementation of <see cref="IBridgeHost"/>
/// that uses MAUI WebView's <c>EvaluateJavaScriptAsync</c> to communicate with the web UI.
/// </summary>
public sealed class MacBridgeHost : IBridgeHost
{
    private readonly WebView _webView;

    public MacBridgeHost(WebView webView)
    {
        _webView = webView;
    }

    public void PostMessage(string json)
    {
        // Escape the JSON string for JavaScript injection
        var escaped = json.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r");
        var script = $"if(window.handleMessage){{try{{window.handleMessage('{escaped}');}}catch(e){{console.error(e);}}}}";
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await _webView.EvaluateJavaScriptAsync(script); }
            catch { /* WebView may not be ready */ }
        });
    }

    public void ShowError(string message)
    {
        // On Mac, errors are shown via JS toast or console; native alert would interrupt flow
        PostMessage(new { @event = "error", message });
    }

    public string? BrowseFolder()
    {
        // Use macOS folder picker via P/Invoke to NSOpenPanel, or return null for now
        // A full implementation would use PlatformInterop or .NET MAUI's folder picker APIs
        return null;
    }
}
