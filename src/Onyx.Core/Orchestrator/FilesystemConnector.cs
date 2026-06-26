using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// Filesystem MCP connector. Provides the LLM with structured access to the
/// user's filesystem: list, read, write, delete files and directories.
///
/// This is separate from the SystemTool (which handles registry, shell, env,
/// processes, PATH, and system info) so the user can see them as distinct
/// connectors in the Connections grid.
/// </summary>
public class FilesystemConnector : ITool
{
    private readonly ISystemAccess _system;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Name => "filesystem";
    public bool IsConnected => true;

    public FilesystemConnector(ISystemAccess system)
    {
        _system = system;
    }

    public async Task<ToolResult> ExecuteAsync(string input, Intent intent, CancellationToken ct = default)
    {
        try
        {
            var action = ParseAction(input);
            if (action == null)
                return new ToolResult { ToolName = Name, Success = false, Output = "Could not determine what filesystem action to perform." };

            var result = await ExecuteActionAsync(action, ct);
            return new ToolResult
            {
                ToolName = Name,
                Success = true,
                Output = result,
                Metadata = new() { ["action"] = action.Action },
            };
        }
        catch (Exception ex)
        {
            return new ToolResult { ToolName = Name, Success = false, Output = ex.Message };
        }
    }

    private FsAction? ParseAction(string input)
    {
        var lower = input.ToLowerInvariant();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        var desktop = Path.Combine(home, "Desktop");
        var documents = Path.Combine(home, "Documents");

        // Simple keyword-based routing (the LLM can also be more specific)
        if (lower.Contains("list") || lower.Contains("show me") || lower.Contains("what's in") || lower.Contains("contents of"))
        {
            var path = ExtractPath(input, home, downloads, desktop, documents);
            return new FsAction { Action = "list_dir", Path = path };
        }
        if (lower.Contains("read") || lower.Contains("open file") || lower.Contains("contents of file") || lower.Contains("show file"))
        {
            var path = ExtractPath(input, home, downloads, desktop, documents);
            return new FsAction { Action = "read_file", Path = path };
        }
        if (lower.Contains("write") || lower.Contains("save to") || lower.Contains("create file") || lower.Contains("put in"))
        {
            var (path, content) = ExtractWrite(input, home, downloads, desktop, documents);
            return new FsAction { Action = "write_file", Path = path, Content = content };
        }
        if (lower.Contains("delete") || lower.Contains("remove") || lower.Contains("trash"))
        {
            var path = ExtractPath(input, home, downloads, desktop, documents);
            return new FsAction { Action = "delete_file", Path = path };
        }
        if (lower.Contains("create folder") || lower.Contains("make folder") || lower.Contains("new folder") || lower.Contains("create dir") || lower.Contains("make dir"))
        {
            var path = ExtractPath(input, home, downloads, desktop, documents);
            return new FsAction { Action = "create_dir", Path = path };
        }
        if (lower.Contains("file info") || lower.Contains("file details") || lower.Contains("size of") || lower.Contains("when was"))
        {
            var path = ExtractPath(input, home, downloads, desktop, documents);
            return new FsAction { Action = "file_info", Path = path };
        }
        if (lower.Contains("exists") || lower.Contains("does file") || lower.Contains("is there"))
        {
            var path = ExtractPath(input, home, downloads, desktop, documents);
            return new FsAction { Action = "file_exists", Path = path };
        }

        // Fallback: try to extract a path and guess the action
        var fallbackPath = ExtractPath(input, home, downloads, desktop, documents);
        if (!string.IsNullOrEmpty(fallbackPath))
        {
            return new FsAction { Action = "file_info", Path = fallbackPath };
        }

        return null;
    }

    private static string ExtractPath(string input, string home, string downloads, string desktop, string documents)
    {
        // Replace common references with actual paths
        input = input.Replace("my downloads", downloads, StringComparison.OrdinalIgnoreCase)
                     .Replace("the downloads", downloads, StringComparison.OrdinalIgnoreCase)
                     .Replace("downloads folder", downloads, StringComparison.OrdinalIgnoreCase)
                     .Replace("downloads", downloads, StringComparison.OrdinalIgnoreCase)
                     .Replace("my desktop", desktop, StringComparison.OrdinalIgnoreCase)
                     .Replace("the desktop", desktop, StringComparison.OrdinalIgnoreCase)
                     .Replace("desktop folder", desktop, StringComparison.OrdinalIgnoreCase)
                     .Replace("desktop", desktop, StringComparison.OrdinalIgnoreCase)
                     .Replace("my documents", documents, StringComparison.OrdinalIgnoreCase)
                     .Replace("the documents", documents, StringComparison.OrdinalIgnoreCase)
                     .Replace("documents folder", documents, StringComparison.OrdinalIgnoreCase)
                     .Replace("documents", documents, StringComparison.OrdinalIgnoreCase)
                     .Replace("my home", home, StringComparison.OrdinalIgnoreCase)
                     .Replace("home directory", home, StringComparison.OrdinalIgnoreCase);

        // Resolve ~ and environment variables
        input = input.Replace("~", home, StringComparison.OrdinalIgnoreCase)
                     .Replace("%USERPROFILE%", home, StringComparison.OrdinalIgnoreCase);

        // Try to find a path pattern
        var words = input.Split(new[] { ' ', '"', '\'', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in words)
        {
            if (w.StartsWith(home, StringComparison.OrdinalIgnoreCase) ||
                w.StartsWith(downloads, StringComparison.OrdinalIgnoreCase) ||
                w.StartsWith(desktop, StringComparison.OrdinalIgnoreCase) ||
                w.StartsWith(documents, StringComparison.OrdinalIgnoreCase) ||
                w.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) ||
                w.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase) ||
                w.StartsWith("/home/", StringComparison.OrdinalIgnoreCase))
            {
                return w.TrimEnd('.', ',', ';', '!', '?');
            }
        }

        // If user just said "downloads" without any other path, default to downloads dir
        if (input.IndexOf(downloads, StringComparison.OrdinalIgnoreCase) >= 0) return downloads;
        if (input.IndexOf(desktop, StringComparison.OrdinalIgnoreCase) >= 0) return desktop;
        if (input.IndexOf(documents, StringComparison.OrdinalIgnoreCase) >= 0) return documents;
        if (input.IndexOf(home, StringComparison.OrdinalIgnoreCase) >= 0) return home;

        return home;
    }

    private static (string path, string content) ExtractWrite(string input, string home, string downloads, string desktop, string documents)
    {
        // Find the content after common separators
        var path = ExtractPath(input, home, downloads, desktop, documents);
        var content = "";

        var afterPath = input;
        if (input.IndexOf(" called ", StringComparison.OrdinalIgnoreCase) >= 0)
            afterPath = input[(input.IndexOf(" called ", StringComparison.OrdinalIgnoreCase) + 8)..];
        else if (input.IndexOf(" named ", StringComparison.OrdinalIgnoreCase) >= 0)
            afterPath = input[(input.IndexOf(" named ", StringComparison.OrdinalIgnoreCase) + 7)..];
        else if (input.IndexOf(" with ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var idx = input.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
            afterPath = input[(idx + 6)..];
            content = afterPath.Trim();
        }

        return (path, content);
    }

    private async Task<string> ExecuteActionAsync(FsAction action, CancellationToken ct)
    {
        switch (action.Action)
        {
            case "list_dir":
                return await _system.ListDirectoryAsync(action.Path, ct);
            case "read_file":
                return await _system.ReadFileAsync(action.Path, ct);
            case "write_file":
                return await _system.WriteFileAsync(action.Path, action.Content ?? "", ct);
            case "delete_file":
                return await _system.DeleteFileAsync(action.Path, ct);
            case "create_dir":
                return await _system.CreateDirectoryAsync(action.Path, ct);
            case "file_info":
                return await _system.GetFileInfoAsync(action.Path, ct);
            case "file_exists":
                return await _system.FileExistsAsync(action.Path, ct);
            default:
                return $"Unknown filesystem action: {action.Action}";
        }
    }

    public static ToolDefinition Definition => new()
    {
        Name = "filesystem",
        Description = "Filesystem access. Read, write, delete, and list files and directories on the user's computer. Can create folders, check if files exist, and get file metadata (size, dates). Always available.",
        Category = "system",
        Triggers = new() { "file", "folder", "directory", "read", "write", "delete", "list", "create", "contents", "path" },
        RequiresConfirmation = true,
        Enabled = true,
    };
}

class FsAction
{
    public string Action { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Content { get; set; }
}
