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
            Text = "Onyx",
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
        // Draw a gemstone silhouette at 32x32 — the Onyx brand mark.
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(0x0C, 0x0C, 0x0E));
            // Gem outline: diamond shape
            var points = new System.Drawing.PointF[]
            {
                new(16, 3),   // top
                new(28, 14),  // right
                new(16, 29),  // bottom
                new(4, 14),   // left
            };
            using var brush = new SolidBrush(Color.FromArgb(0xE4, 0xE4, 0xE7));
            g.FillPolygon(brush, points);
            // Facet lines
            using var pen = new Pen(Color.FromArgb(0x0C, 0x0C, 0x0E), 1.5f);
            g.DrawLine(pen, 4, 14, 28, 14);   // horizontal girdle
            g.DrawLine(pen, 10, 14, 16, 3);   // upper-left facet
            g.DrawLine(pen, 22, 14, 16, 3);   // upper-right facet
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
        menu.Items.Add("Quit Onyx", null, (_, _) => _owner.CloseApp());
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
