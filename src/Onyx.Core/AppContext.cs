using Ollama2.Orchestrator;
using Ollama2.Services;

namespace Ollama2;

/// <summary>
/// Platform-agnostic application context. Holds all shared services and state.
/// Created once at startup by the platform-specific App shell.
/// </summary>
public sealed class AppContext
{
    private static AppContext? _current;
    public static AppContext Current => _current ?? throw new InvalidOperationException("AppContext not initialized.");

    public static bool IsInitialized => _current != null;

    public static void Initialize(string dataDir)
    {
        if (_current != null) return;
        _current = new AppContext(dataDir);
    }

    private AppContext(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(DataDir);

        Config = new ConfigService(Path.Combine(DataDir, "config.json"));
        Config.Load();

        Ollama = new OllamaClient(() => Config.Current.ServerUrl);
        WebSearch = new WebSearchService();
        Chats = new ChatStore(Path.Combine(DataDir, "chats.json"));
        Chats.Load();

        // Initialize the orchestrator with the built-in web search tool
        Orchestrator = new OrchestratorService(Ollama, () => Config.Current.DefaultModel);
        Orchestrator.Tools.Register(WebSearchTool.Definition, new WebSearchTool(WebSearch, Config.Current.MaxSearchResults));
    }

    public string DataDir { get; }
    public ConfigService Config { get; }
    public OllamaClient Ollama { get; }
    public WebSearchService WebSearch { get; }
    public ChatStore Chats { get; }
    public OrchestratorService Orchestrator { get; }

    public void SaveAll()
    {
        Config.Save();
        Chats.Save();
    }

    // Update-check state populated by background tasks
    public static string? PendingUpdatePath { get; set; }
    public static string? PendingUpdateVersion { get; set; }
    public static string? PendingUpdateError { get; set; }
}
