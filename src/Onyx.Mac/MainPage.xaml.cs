using System.Reflection;
using System.Text;
using System.Web;
using Ollama2;

namespace Onyx.Mac;

public partial class MainPage : ContentPage
{
    private Bridge _bridge = null!;
    private MacBridgeHost _host = null!;

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object? sender, EventArgs e)
    {
        _host = new MacBridgeHost(WebView);
        _bridge = new Bridge(_host);

        var webRoot = ExtractWebAssets();
        var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var url = $"file://{webRoot}/index.html?v={cacheBuster}";

        WebView.Source = new UrlWebViewSource { Url = url };
    }

    /// <summary>
    /// Intercepts custom `onyx://msg` URLs fired by the JS bridge to route messages to C#.
    /// </summary>
    private void OnWebViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url.StartsWith("onyx://msg"))
        {
            e.Cancel = true;
            var uri = new Uri(e.Url);
            var query = HttpUtility.ParseQueryString(uri.Query);
            var json = query.Get("data");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    _bridge.HandleMessageFromWeb(json);
                }
                catch (Exception ex)
                {
                    _bridge.PostToWeb(new { @event = "error", message = ex.Message });
                }
            }
            return;
        }

        // Allow everything else
    }

    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result == WebNavigationResult.Success)
        {
            // Inject the JS bridge adapter so the web UI can talk to C#
            InjectBridgeAdapter();
        }
    }

    private void InjectBridgeAdapter()
    {
        var script = """
            (function() {
                if (window._onyxBridgeInjected) return;
                window._onyxBridgeInjected = true;

                // Override the Windows-specific chrome.webview API with a Mac-compatible one
                if (!window.chrome) window.chrome = {};
                window.chrome.webview = {
                    postMessage: function(msg) {
                        var json = typeof msg === 'string' ? msg : JSON.stringify(msg);
                        window.location.href = 'onyx://msg?data=' + encodeURIComponent(json);
                    },
                    addEventListener: function(type, handler) {
                        if (type === 'message') {
                            if (!window._onyxMessageHandlers) window._onyxMessageHandlers = [];
                            window._onyxMessageHandlers.push(handler);
                        }
                    }
                };

                // Global handler that C# will call via EvaluateJavaScriptAsync
                window.handleMessage = function(json) {
                    var msg = typeof json === 'string' ? JSON.parse(json) : json;
                    if (window._onyxMessageHandlers) {
                        window._onyxMessageHandlers.forEach(function(h) {
                            try { h({ data: msg }); } catch(e) {}
                        });
                    }
                };
            })();
            """;
        WebView.EvaluateJavaScriptAsync(script);
    }

    private static string ExtractWebAssets()
    {
        var root = Path.Combine(App.DataDir, "web");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        var asm = typeof(AppContext).Assembly;
        var coreNs = typeof(AppContext).Namespace;
        var manifestName = coreNs + ".Web.manifest.txt";

        using var manifestStream = asm.GetManifestResourceStream(manifestName);
        if (manifestStream == null) return root;

        using var sr = new StreamReader(manifestStream);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var relPath = line.Trim();
            var dest = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var resName = coreNs + ".Web." + relPath.Replace('/', '.');
            using var s = asm.GetManifestResourceStream(resName);
            if (s == null) continue;
            using var f = File.Create(dest);
            s.CopyTo(f);
        }
        return root;
    }
}
