using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Ollama2;

public partial class MainWindow : Window
{
    private NotifyIconHelper? _tray;
    private bool _reallyClose;
    private readonly Bridge _bridge;

    public MainWindow()
    {
        InitializeComponent();
        _bridge = new Bridge(this);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebView();
        _tray = new NotifyIconHelper(this);
    }

    private async Task InitializeWebView()
    {
        await WebView.EnsureCoreWebView2Async();
        var core = WebView.CoreWebView2;

        var webRoot = ExtractWebAssets();
        core.SetVirtualHostNameToFolderMapping("ollama.app", webRoot,
            CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.UserAgent = "Ollama2.0/2.2 (Windows; +https://ollama.com)";

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += (_, _) => _bridge.SendInitialState();

        core.Navigate("https://ollama.app/index.html");
    }

    private static string ExtractWebAssets()
    {
        var root = Path.Combine(App.DataDir, "web");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var asm = Assembly.GetExecutingAssembly();
        var manifestName = typeof(App).Namespace + ".Web.manifest.txt";
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
            var resName = typeof(App).Namespace + ".Web." + relPath.Replace('/', '.');
            using var s = asm.GetManifestResourceStream(resName);
            if (s == null) continue;
            using var f = File.Create(dest);
            s.CopyTo(f);
        }
        return root;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            _bridge.HandleMessageFromWeb(json);
        }
        catch (Exception ex)
        {
            _bridge.PostToWeb(new { @event = "error", message = ex.Message });
        }
    }

    public void PostWebMessageAsJson(string json) => Dispatcher.Invoke(() =>
        WebView.CoreWebView2?.PostWebMessageAsJson(json));

    public void NavigateReload() => Dispatcher.Invoke(() => WebView.CoreWebView2?.Reload());

    public void ToggleDevTools() => Dispatcher.Invoke(() =>
    {
        if (WebView.CoreWebView2 != null)
            WebView.CoreWebView2.OpenDevToolsWindow();
    });

    public void SetZoom(double z) => Dispatcher.Invoke(() => WebView.ZoomFactor = z);

    // ---- Tray / Close behavior ----
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var behavior = App.Current.Config.Current.CloseBehavior;

        if (behavior == "quit" || _reallyClose)
        {
            // Actually quit — clean up tray
            _tray?.Dispose();
            return;
        }

        // "tray" behavior (default): minimize to tray
        e.Cancel = true;
        Hide();
        _tray?.ShowBalloon("Ollama 2.0", "Ollama 2.0 is still running in the background.");
    }

    public void CloseApp()
    {
        _reallyClose = true;
        Close();
    }

    public void BringToFront()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
