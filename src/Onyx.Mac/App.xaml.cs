using System.IO;
using Ollama2;

namespace Onyx.Mac;

public partial class App : Application
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Onyx");

    public App()
    {
        InitializeComponent();

        // Initialize shared core context with macOS system access
        AppContext.Initialize(DataDir, new MacSystemAccess());

        // Register Mac-specific implementations
        HardwareDetector.Instance = new MacHardwareDetector();

        MainPage = new AppShell();
    }
}
