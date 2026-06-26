using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ollama2.Services;

public class ChatStore
{
    private readonly string _path;
    public List<StoredChat> Chats { get; private set; } = new();

    public ChatStore(string path) => _path = path;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Chats = JsonSerializer.Deserialize<List<StoredChat>>(File.ReadAllText(_path)) ?? new();
        }
        catch { Chats = new(); }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(Chats, ConfigService.JsonOpts));
        }
        catch { /* non-fatal */ }
    }

    public StoredChat Create(string model)
    {
        var chat = new StoredChat
        {
            Id = System.Guid.NewGuid().ToString("N"),
            Title = "New Chat",
            Model = model,
            CreatedAt = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UpdatedAt = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        Chats.Insert(0, chat);
        return chat;
    }

    public StoredChat? Get(string id) => Chats.FirstOrDefault(c => c.Id == id);

    public void Delete(string id) => Chats.RemoveAll(c => c.Id == id);

    public void Touch(string id)
    {
        var c = Get(id);
        if (c != null)
        {
            c.UpdatedAt = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var idx = Chats.IndexOf(c);
            if (idx > 0) { Chats.RemoveAt(idx); Chats.Insert(0, c); }
        }
    }
}
