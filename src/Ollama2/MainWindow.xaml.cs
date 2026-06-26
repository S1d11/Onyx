using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Ollama2.Services;

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

        // Extract embedded Web/ resources to a temp folder and map a virtual host
        var webRoot = ExtractWebAssets();
        core.SetVirtualHostNameToFolderMapping("ollama.app", webRoot,
            CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.UserAgent = "Ollama/1.0 (Windows; +https://ollama.com)";

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += (_, _) =>
        {
            // Push initial state to the UI
            _bridge.SendInitialState();
        };

        core.Navigate("https://ollama.app/index.html");
    }

    private static string ExtractWebAssets()
    {
        var root = Path.Combine(App.DataDir, "web");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var asm = Assembly.GetExecutingAssembly();
        // Embedded-resource names flatten folder separators to '.', which is
        // ambiguous for filenames that contain dots. We ship a manifest listing
        // the true relative paths and rebuild the folder tree from it.
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
        {
            if (WebView.CoreWebView2.Settings.AreDevToolsEnabled)
                WebView.CoreWebView2.OpenDevToolsWindow();
        }
    });

    public void SetZoom(double z) => Dispatcher.Invoke(() => WebView.ZoomFactor = z);

    // ---- Menu handlers ----
    private void NewChat_Click(object sender, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "newChat" });
    private void NewWindow_Click(object sender, RoutedEventArgs e) =>
        new MainWindow { Title = "Ollama" }.Show();
    private void OpenChat_Click(object sender, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "openChat" });
    private void ExportChat_Click(object sender, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "exportChat" });
    private void ImportChat_Click(object sender, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "importChat" });
    private void Exit_Click(object sender, RoutedEventArgs e) => CloseApp();

    private void Edit_Undo(object s, RoutedEventArgs e) => _bridge.PostToWeb(new { @event = "menu", action = "editUndo" });
    private void Edit_Redo(object s, RoutedEventArgs e) => _bridge.PostToWeb(new { @event = "menu", action = "editRedo" });
    private void Edit_Cut(object s, RoutedEventArgs e) => _bridge.PostToWeb(new { @event = "menu", action = "editCut" });
    private void Edit_Copy(object s, RoutedEventArgs e) => _bridge.PostToWeb(new { @event = "menu", action = "editCopy" });
    private void Edit_Paste(object s, RoutedEventArgs e) => _bridge.PostToWeb(new { @event = "menu", action = "editPaste" });
    private void Edit_SelectAll(object s, RoutedEventArgs e) => _bridge.PostToWeb(new { @event = "menu", action = "editSelectAll" });
    private void ClearConversation_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "clearConversation" });
    private void DeleteChat_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "deleteChat" });

    private void ToggleSidebar_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "toggleSidebar" });
    private void ZoomIn_Click(object s, RoutedEventArgs e) => SetZoom(Math.Min(5, WebView.ZoomFactor + 0.1));
    private void ZoomOut_Click(object s, RoutedEventArgs e) => SetZoom(Math.Max(0.25, WebView.ZoomFactor - 0.1));
    private void ZoomReset_Click(object s, RoutedEventArgs e) => SetZoom(1.0);
    private void ToggleTheme_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "toggleTheme" });
    private void Reload_Click(object s, RoutedEventArgs e) => NavigateReload();
    private void DevTools_Click(object s, RoutedEventArgs e) => ToggleDevTools();

    private void PullModel_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "pullModel" });
    private void DeleteModel_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "deleteModel" });
    private void RefreshModels_Click(object s, RoutedEventArgs e) => _ = _bridge.RefreshModelsAsync();
    private void ManageModels_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "manageModels" });

    private void Preferences_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "preferences" });
    private void ServerSettings_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "serverSettings" });

    private void HelpDocs_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://github.com/ollama/ollama/blob/main/docs/api.md");
    private void HelpModels_Click(object s, RoutedEventArgs e) =>
        OpenUrl("https://ollama.com/library");
    private void HelpShortcuts_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "shortcuts" });
    private void CheckUpdates_Click(object s, RoutedEventArgs e) =>
        _bridge.PostToWeb(new { @event = "menu", action = "checkUpdates" });
    private void About_Click(object s, RoutedEventArgs e) =>
        MessageBox.Show(this, "Ollama\nVersion 1.0.0\n\nA 1:1 clone of the Ollama desktop app.\nNative Windows app (WPF + WebView2).\n\nTalks to a local `ollama serve` instance at http://localhost:11434.",
            "About Ollama", MessageBoxButton.OK, MessageBoxImage.Information);

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ---- Tray / close behavior (minimize to tray like the real Ollama app) ----
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloon("Ollama", "Ollama is still running in the background.");
            return;
        }
        _tray?.Dispose();
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
