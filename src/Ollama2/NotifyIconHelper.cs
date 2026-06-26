using System;
using System.Drawing;
using System.Windows.Forms;

namespace Ollama2;

/// <summary>
/// Native Windows system-tray icon (WinForms NotifyIcon hosted in the WPF app).
/// Replicates the Ollama desktop app's tray behavior: left-click shows the window,
/// right-click opens a context menu with New Chat, Models, Settings, Quit.
/// </summary>
internal sealed class NotifyIconHelper : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainWindow _owner;

    public NotifyIconHelper(MainWindow owner)
    {
        _owner = owner;
        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Ollama",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _owner.BringToFront();
        };
    }

    private static Icon CreateIcon()
    {
        // Draw a simple "llama-ish" rounded glyph at 32x32. The real app ships an
        // icon resource; this keeps the build self-contained with no binary asset.
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(0x0B, 0x0D, 0x0F));
            using var brush = new SolidBrush(Color.FromArgb(0xD9, 0x77, 0x57));
            g.FillEllipse(brush, 3, 3, 26, 26);
            using var pen = new Pen(Color.White, 2);
            // stylized "O"
            g.DrawEllipse(pen, 9, 9, 14, 14);
        }
        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("New Chat", null, (_, _) => _owner.PostWebMessageAsJson(
            "{\"event\":\"menu\",\"action\":\"newChat\"}"));
        menu.Items.Add("-");
        menu.Items.Add("Models...", null, (_, _) => _owner.PostWebMessageAsJson(
            "{\"event\":\"menu\",\"action\":\"manageModels\"}"));
        menu.Items.Add("Settings...", null, (_, _) => _owner.PostWebMessageAsJson(
            "{\"event\":\"menu\",\"action\":\"preferences\"}"));
        menu.Items.Add("-");
        menu.Items.Add("Show Window", null, (_, _) => _owner.BringToFront());
        menu.Items.Add("Quit Ollama", null, (_, _) => _owner.CloseApp());
        return menu;
    }

    public void ShowBalloon(string title, string body)
    {
        _icon.ShowBalloonTip(2000, title, body, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
