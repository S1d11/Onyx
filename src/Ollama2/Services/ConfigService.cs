using System.IO;
using System.Text.Json;

namespace Ollama2.Services;

public class ConfigService
{
    private readonly string _path;
    public AppConfig Current { get; private set; } = new();

    public ConfigService(string path) => _path = path;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                Current = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch { Current = new AppConfig(); }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch { /* non-fatal */ }
    }

    public void Update(AppConfig next)
    {
        Current = next;
        Save();
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };
}
