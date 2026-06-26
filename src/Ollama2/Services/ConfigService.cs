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
                var saved = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (saved != null)
                {
                    // Merge: start from defaults, overwrite with saved values.
                    // This ensures new fields added in updates get their defaults
                    // instead of being null/zero if the saved file is from an older version.
                    var merged = new AppConfig();
                    foreach (var prop in typeof(AppConfig).GetProperties())
                    {
                        if (!prop.CanRead || !prop.CanWrite) continue;
                        var val = prop.GetValue(saved);
                        // Only overwrite the default if the saved value differs from the property's default
                        // For nullable types, overwrite if non-null; for value types, overwrite always
                        // (since the saved file explicitly serialized them)
                        if (val != null)
                        {
                            prop.SetValue(merged, val);
                        }
                    }
                    Current = merged;
                }
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
