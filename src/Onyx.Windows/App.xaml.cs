using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ollama2.Services;
using Onyx.Windows;

namespace Ollama2;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "Onyx-SingleInstance-Mutex-v1";

    public static new App Current => (App)Application.Current;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance check
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            BringExistingToFront();
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Initialize shared core context with Windows system access
        AppContext.Initialize(DataDir, new WindowsSystemAccess());

        // Register Windows-specific implementations
        HardwareDetector.Instance = new WindowsHardwareDetector();
        StartupRegistration.Instance = new WindowsStartupRegistration();

        // Background update check on startup (if enabled)
        if (AppContext.Current.Config.Current.CheckUpdatesOnStartup)
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
                        AppContext.PendingUpdateError = "Update found but no download URL available.";
                        return;
                    }
                    var path = await updater.DownloadUpdateAsync(release);
                    if (!string.IsNullOrEmpty(path))
                    {
                        AppContext.PendingUpdatePath = path;
                        AppContext.PendingUpdateVersion = release.TagName;
                        // Notify the UI window directly if it's already loaded
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (MainWindow is MainWindow win && win.IsLoaded)
                            {
                                win.PostWebMessageAsJson(JsonSerializer.Serialize(new
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
                    AppContext.PendingUpdateError = ex.Message;
                }
            });
        }
    }

    private static void BringExistingToFront()
    {
        try
        {
            var currentId = Environment.ProcessId;
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("Onyx"))
            {
                if (proc.Id == currentId) continue;

                var hwnd = proc.MainWindowHandle;

                // If MainWindowHandle is zero (window hidden in tray), search all top-level windows
                if (hwnd == nint.Zero)
                {
                    hwnd = FindWindowByProcessId((uint)proc.Id);
                }

                if (hwnd == nint.Zero) continue;

                // Restore if minimized, show if hidden
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);
                else if (!IsWindowVisible(hwnd))
                    ShowWindow(hwnd, SW_SHOW);

                SetForegroundWindow(hwnd);
                break;
            }
        }
        catch { /* best effort */ }
    }

    private static nint FindWindowByProcessId(uint targetPid)
    {
        nint found = nint.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == targetPid)
            {
                found = hWnd;
                return false; // stop enumerating
            }
            return true;
        }, nint.Zero);
        return found;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppContext.Current?.SaveAll();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
