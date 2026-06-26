using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ollama2.Services;

public class AppConfig
{
    public string ServerUrl { get; set; } = "http://localhost:11434";
    public string DefaultModel { get; set; } = "llama3.2";
    public bool WebSearchEnabled { get; set; } = true;
    public string Theme { get; set; } = "dark";
    public bool SidebarVisible { get; set; } = true;
    public double Zoom { get; set; } = 1.0;
    public double Temperature { get; set; } = 0.8;
    public int TopK { get; set; } = 40;
    public double TopP { get; set; } = 0.9;
    public int NumCtx { get; set; } = 4096;
    public string SystemPrompt { get; set; } = "";
    public int MaxSearchResults { get; set; } = 5;
    public string WebSearchProvider { get; set; } = "duckduckgo";
    public string? WebSearchApiKey { get; set; }
    public string CloseBehavior { get; set; } = "tray"; // "quit" or "tray"
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool ExposeToNetwork { get; set; } = false;
    public string ModelPath { get; set; } = "";
    public bool Stream { get; set; } = true;
    public string Effort { get; set; } = "medium"; // "low", "medium", "high", "max"
    public bool ThinkingEnabled { get; set; } = false;
}

public class ModelInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("digest")] public string Digest { get; set; } = "";
    [JsonPropertyName("modified_at")] public string ModifiedAt { get; set; } = "";
    public string Details { get; set; } = "";
}

public class TagsResponse
{
    [JsonPropertyName("models")] public List<ModelInfo> Models { get; set; } = new();
}

public class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("images")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Images { get; set; }
}

public class ChatRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = new();
    [JsonPropertyName("stream")] public bool Stream { get; set; } = true;
    [JsonPropertyName("options")] public Dictionary<string, object>? Options { get; set; }
    [JsonPropertyName("tools")] public List<ToolDef>? Tools { get; set; }
}

public class ToolDef
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ToolFunction Function { get; set; } = new();
}

public class ToolFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("parameters")] public object Parameters { get; set; } = new { };
}

public class ChatChunk
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("message")] public ChatMessage Message { get; set; } = new();
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("total_duration")] public long TotalDuration { get; set; }
    [JsonPropertyName("eval_count")] public long EvalCount { get; set; }
    [JsonPropertyName("prompt_eval_count")] public long PromptEvalCount { get; set; }
}

public class ChatStoredMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public List<string>? Images { get; set; }
    public List<SearchSource>? Sources { get; set; }
    public string? Error { get; set; }
    public long? EvalCount { get; set; }
    public long? TotalMs { get; set; }
}

public class StoredChat
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New Chat";
    public string Model { get; set; } = "";
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public List<ChatStoredMessage> Messages { get; set; } = new();
}

public class SearchSource
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Snippet { get; set; } = "";
}

public class WebSearchResult
{
    public string Query { get; set; } = "";
    public List<SearchSource> Results { get; set; } = new();
}
