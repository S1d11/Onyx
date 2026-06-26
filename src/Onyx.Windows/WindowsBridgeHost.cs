using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Ollama2;

/// <summary>
/// Windows-specific implementation of <see cref="IBridgeHost"/>
/// that uses WebView2's <c>PostWebMessageAsJson</c> to communicate with the web UI.
/// </summary>
public sealed class WindowsBridgeHost : IBridgeHost
{
    private readonly MainWindow _window;

    public WindowsBridgeHost(MainWindow window)
    {
        _window = window;
    }

    public void PostMessage(string json)
    {
        _window.Dispatcher.Invoke(() =>
            _window.WebView.CoreWebView2?.PostWebMessageAsJson(json));
    }

    public void ShowError(string message)
    {
        MessageBox.Show(_window, message, "Onyx", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public string? BrowseFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }
}
