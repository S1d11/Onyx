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

        // Initialize shared core context
        AppContext.Initialize(DataDir);

        // Register Mac-specific implementations
        HardwareDetector.Instance = new MacHardwareDetector();

        MainPage = new AppShell();
    }
}
