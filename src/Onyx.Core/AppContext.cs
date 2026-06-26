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

    public static void Initialize(string dataDir, ISystemAccess? systemAccess = null)
    {
        if (_current != null) return;
        _current = new AppContext(dataDir, systemAccess);
    }

    private AppContext(string dataDir, ISystemAccess? systemAccess = null)
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

        // Register the filesystem connector (always available if system access exists)
        if (systemAccess != null)
        {
            var fsConnector = new FilesystemConnector(systemAccess);
            Orchestrator.Tools.Register(FilesystemConnector.Definition, fsConnector);
            FilesystemConnector = fsConnector;

            // Register the system/OS tool (registry, shell, env, processes, PATH, system info)
            var systemTool = new SystemTool(Ollama, () => Config.Current.DefaultModel, systemAccess);
            Orchestrator.Tools.Register(SystemTool.Definition, systemTool);
            SystemTool = systemTool;
        }

        // Register the GitHub connector + OAuth service (device flow)
        GitHubOAuth = new GitHubOAuthService(Config);
        var githubConnector = new GitHubConnector(() => Config.Current.GitHubToken);
        Orchestrator.Tools.Register(GitHubConnector.Definition, githubConnector);
        GitHubConnector = githubConnector;

        // Register Google connectors (Gmail + Drive) — share the same OAuth service
        GoogleOAuth = new GoogleOAuthService(Config);
        var gmailConnector = new GmailConnector(GoogleOAuth);
        Orchestrator.Tools.Register(GmailConnector.Definition, gmailConnector);
        GmailConnector = gmailConnector;

        var gdriveConnector = new GoogleDriveConnector(GoogleOAuth);
        Orchestrator.Tools.Register(GoogleDriveConnector.Definition, gdriveConnector);
        GoogleDriveConnector = gdriveConnector;
    }

    public string DataDir { get; }
    public ConfigService Config { get; }
    public OllamaClient Ollama { get; }
    public WebSearchService WebSearch { get; }
    public ChatStore Chats { get; }
    public OrchestratorService Orchestrator { get; }
    public FilesystemConnector? FilesystemConnector { get; }
    public SystemTool? SystemTool { get; }
    public GitHubOAuthService GitHubOAuth { get; } = null!;
    public GitHubConnector GitHubConnector { get; } = null!;
    public GoogleOAuthService GoogleOAuth { get; } = null!;
    public GmailConnector GmailConnector { get; } = null!;
    public GoogleDriveConnector GoogleDriveConnector { get; } = null!;

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
