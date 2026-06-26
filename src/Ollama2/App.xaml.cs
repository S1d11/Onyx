using System;
using System.IO;
using System.Windows;
using Ollama2.Services;

namespace Ollama2;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public ConfigService Config { get; private set; } = null!;
    public OllamaClient Ollama { get; private set; } = null!;
    public WebSearchService WebSearch { get; private set; } = null!;
    public ChatStore Chats { get; private set; } = null!;

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ollama2");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(DataDir);
        Config = new ConfigService(Path.Combine(DataDir, "config.json"));
        Config.Load();
        Ollama = new OllamaClient(() => Config.Current.ServerUrl);
        WebSearch = new WebSearchService();
        Chats = new ChatStore(Path.Combine(DataDir, "chats.json"));
        Chats.Load();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Config.Save();
        Chats.Save();
        base.OnExit(e);
    }
}
