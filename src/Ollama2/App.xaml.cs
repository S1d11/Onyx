using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ollama2.Services;

namespace Ollama2;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "Onyx-SingleInstance-Mutex-v1";

    public static new App Current => (App)Application.Current;

    public ConfigService Config { get; private set; } = null!;
    public OllamaClient Ollama { get; private set; } = null!;
    public WebSearchService WebSearch { get; private set; } = null!;
    public ChatStore Chats { get; private set; } = null!;

    /// <summary>Populated by the background startup update check. UI is notified via Bridge.</summary>
    public static string? PendingUpdatePath { get; set; }
    public static string? PendingUpdateVersion { get; set; }
    public static string? PendingUpdateError { get; set; }

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Onyx");

    static App()
    {
        // Migrate data from the old "Ollama2" directory if the new "Onyx" directory doesn't exist yet
        var oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama2");
        if (Directory.Exists(oldDir) && !Directory.Exists(DataDir))
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                foreach (var file in Directory.GetFiles(oldDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    if (name == "config.json" || name == "chats.json")
                        File.Copy(file, Path.Combine(DataDir, name), overwrite: false);
                }
            }
            catch { /* best-effort migration */ }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance check
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is running — bring it to front and exit
            BringExistingToFront();
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Directory.CreateDirectory(DataDir);
        Config = new ConfigService(Path.Combine(DataDir, "config.json"));
        Config.Load();
        Ollama = new OllamaClient(() => Config.Current.ServerUrl);
        WebSearch = new WebSearchService();
        Chats = new ChatStore(Path.Combine(DataDir, "chats.json"));
        Chats.Load();

        // Background update check on startup (if enabled)
        if (Config.Current.CheckUpdatesOnStartup)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var updater = new UpdateService();
                    var release = await updater.CheckForUpdateAsync();
                    if (release == null) return;
                    if (string.IsNullOrEmpty(release.DownloadUrl))
                    {
                        PendingUpdateError = "Update found but no download URL available.";
                        return;
                    }
                    var path = await updater.DownloadUpdateAsync(release);
                    if (!string.IsNullOrEmpty(path))
                    {
                        PendingUpdatePath = path;
                        PendingUpdateVersion = release.TagName;
                        // Notify the UI window directly if it's already loaded
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (MainWindow is MainWindow win && win.IsLoaded)
                            {
                                win.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    @event = "updateReady",
                                    version = release.TagName,
                                    path = path,
                                }));
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    PendingUpdateError = ex.Message;
                }
            });
        }
    }

    private static void BringExistingToFront()
    {
        try
        {
            // Find the existing Onyx process that isn't this one
            var currentId = Environment.ProcessId;
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("Onyx"))
            {
                if (proc.Id == currentId) continue;
                if (proc.MainWindowHandle == nint.Zero) continue;

                if (IsIconic(proc.MainWindowHandle))
                    ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(proc.MainWindowHandle);
                break;
            }
        }
        catch { /* best effort */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Config?.Save();
        Chats?.Save();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
