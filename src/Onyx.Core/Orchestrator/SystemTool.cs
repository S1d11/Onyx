using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// The system tool gives the orchestrator full access to the user's computer:
/// filesystem (read/write/delete), shell (cmd, powershell, bash), registry
/// (Windows), environment variables, PATH, processes, and system info.
///
/// The tool works in two phases:
///   1. Parse: sends the user's message to the LLM to determine the specific
///      action + parameters (structured JSON). This is where the LLM "understands"
///      what the user wants to do on the system.
///   2. Execute: calls ISystemAccess to perform the action and returns the result.
///
/// Destructive operations (file writes, deletes, registry writes, command
/// execution) can require user confirmation via the ConfirmationRequested event.
/// </summary>
public class SystemTool : ITool
{
    private readonly OllamaClient _ollama;
    private readonly Func<string> _getModel;
    private readonly ISystemAccess _system;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string Name => "system";

    /// <summary>Raised when a destructive operation needs user confirmation.</summary>
    public event EventHandler<SystemConfirmationEventArgs>? ConfirmationRequested;

    /// <summary>Whether to auto-approve destructive operations without UI confirmation.</summary>
    public bool AutoApproveDestructive { get; set; } = false;

    public SystemTool(OllamaClient ollama, Func<string> getModel, ISystemAccess system)
    {
        _ollama = ollama;
        _getModel = getModel;
        _system = system;
    }

    public async Task<ToolResult> ExecuteAsync(string input, Intent intent, CancellationToken ct = default)
    {
        try
        {
            // Phase 1: Use LLM to parse the user's request into a structured action
            var action = await ParseActionAsync(input, ct);
            if (action == null)
                return new ToolResult { ToolName = Name, Success = false, Output = "Could not determine what system action to perform." };

            // Phase 2: Check if destructive and needs confirmation
            if (action.IsDestructive && !AutoApproveDestructive)
            {
                var approved = RequestConfirmation(action);
                if (!approved)
                    return new ToolResult { ToolName = Name, Success = false, Output = "User denied the operation." };
            }

            // Phase 3: Execute the action
            var output = await ExecuteActionAsync(action, ct);
            return new ToolResult
            {
                ToolName = Name,
                Success = true,
                Output = output,
                Metadata = new() { ["action"] = action.Action, ["isDestructive"] = action.IsDestructive },
            };
        }
        catch (Exception ex)
        {
            return new ToolResult { ToolName = Name, Success = false, Output = ex.Message };
        }
    }

    /// <summary>Use the LLM to parse the user's natural language request into a structured system action.</summary>
    private async Task<SystemAction?> ParseActionAsync(string userMessage, CancellationToken ct)
    {
        var platform = _system.Platform;
        var systemPrompt = BuildParserPrompt(platform);

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userMessage },
        };

        var req = new ChatRequest
        {
            Model = _getModel(),
            Messages = messages,
            Stream = false,
            Options = new Dictionary<string, object>
            {
                { "temperature", 0.1 },
                { "top_k", 10 },
                { "top_p", 0.8 },
                { "num_ctx", 4096 },
            },
        };

        var resp = await _ollama.ChatOnceAsync(req, ct);
        var raw = resp.Message?.Content ?? "";
        var json = ExtractJson(raw);
        if (string.IsNullOrEmpty(json)) return null;

        var action = JsonSerializer.Deserialize<SystemAction>(json, JsonOpts);
        return action;
    }

    private string BuildParserPrompt(string platform) =>
        "You are a system action parser. The user wants to perform an operation on their " + platform + " computer.\n" +
        "Analyze their request and determine the specific system action to perform.\n" +
        "Respond ONLY with a JSON object — no markdown, no explanation.\n\n" +
        "Available actions:\n" +
        "- \"list_dir\": List directory contents. Params: {\"path\": \"C:\\\\Users\\\\...\"}\n" +
        "- \"read_file\": Read a file's contents. Params: {\"path\": \"...\"}\n" +
        "- \"write_file\": Write content to a file (destructive). Params: {\"path\": \"...\", \"content\": \"...\"}\n" +
        "- \"delete_file\": Delete a file or directory (destructive). Params: {\"path\": \"...\"}\n" +
        "- \"create_dir\": Create a directory. Params: {\"path\": \"...\"}\n" +
        "- \"file_info\": Get file/directory info (size, dates, attributes). Params: {\"path\": \"...\"}\n" +
        "- \"run_command\": Execute a shell command (destructive). Params: {\"shell\": \"cmd|powershell|pwsh|bash\", \"command\": \"...\"}\n" +
        "- \"registry_read\": Read a registry value (Windows only). Params: {\"keyPath\": \"HKLM\\\\Software\\\\...\", \"valueName\": \"...\"}\n" +
        "- \"registry_write\": Write a registry value (destructive, Windows only). Params: {\"keyPath\": \"...\", \"valueName\": \"...\", \"value\": \"...\"}\n" +
        "- \"registry_delete\": Delete a registry value (destructive, Windows only). Params: {\"keyPath\": \"...\", \"valueName\": \"...\"}\n" +
        "- \"registry_list\": List registry subkeys and values. Params: {\"keyPath\": \"...\"}\n" +
        "- \"env_get\": Get an environment variable. Params: {\"name\": \"PATH\"}\n" +
        "- \"env_set\": Set an environment variable (destructive). Params: {\"name\": \"...\", \"value\": \"...\"}\n" +
        "- \"env_list\": List all environment variables. Params: {}\n" +
        "- \"path_get\": Get the PATH variable. Params: {}\n" +
        "- \"path_add\": Add a directory to PATH (destructive). Params: {\"directory\": \"...\"}\n" +
        "- \"system_info\": Get system information (OS, CPU, RAM, disk). Params: {}\n" +
        "- \"processes\": List running processes. Params: {}\n" +
        "- \"kill_process\": Kill a process (destructive). Params: {\"pid\": 1234}\n\n" +
        "Respond with this JSON structure:\n" +
        "{\"action\":\"read_file\",\"params\":{\"path\":\"C:\\\\Users\\\\example\\\\file.txt\"},\"isDestructive\":false,\"description\":\"Reading file.txt\"}\n\n" +
        "Rules:\n" +
        "- \"action\" must be one of the exact values listed above\n" +
        "- \"params\" contains the action-specific parameters\n" +
        "- \"isDestructive\" is true for: write_file, delete_file, run_command, registry_write, registry_delete, env_set, path_add, kill_process\n" +
        "- \"description\" is a short human-readable summary of what will happen\n" +
        "- Use absolute paths when possible. If the user gives a relative path, resolve it from the home directory.\n" +
        "- For shell commands, choose the appropriate shell (cmd for Windows batch, powershell for PowerShell, bash for Unix).\n" +
        "- Platform: " + platform + "\n";

    /// <summary>Execute the parsed action via ISystemAccess.</summary>
    private async Task<string> ExecuteActionAsync(SystemAction action, CancellationToken ct)
    {
        var p = action.Params ?? new();
        var sb = new StringBuilder();
        sb.AppendLine($"Action: {action.Action}");
        sb.AppendLine($"Description: {action.Description}");
        sb.AppendLine();

        switch (action.Action)
        {
            case "list_dir":
                sb.Append(await _system.ListDirectoryAsync(GetStr(p, "path"), ct));
                break;
            case "read_file":
                sb.Append(await _system.ReadFileAsync(GetStr(p, "path"), ct));
                break;
            case "write_file":
                sb.Append(await _system.WriteFileAsync(GetStr(p, "path"), GetStr(p, "content"), ct));
                break;
            case "delete_file":
                sb.Append(await _system.DeleteFileAsync(GetStr(p, "path"), ct));
                break;
            case "create_dir":
                sb.Append(await _system.CreateDirectoryAsync(GetStr(p, "path"), ct));
                break;
            case "file_info":
                sb.Append(await _system.GetFileInfoAsync(GetStr(p, "path"), ct));
                break;
            case "file_exists":
                sb.Append(await _system.FileExistsAsync(GetStr(p, "path"), ct));
                break;
            case "run_command":
                sb.Append(await _system.RunCommandAsync(GetStr(p, "shell", "cmd"), GetStr(p, "command"), ct));
                break;
            case "registry_read":
                sb.Append(await _system.RegistryReadAsync(GetStr(p, "keyPath"), GetStr(p, "valueName"), ct));
                break;
            case "registry_write":
                sb.Append(await _system.RegistryWriteAsync(GetStr(p, "keyPath"), GetStr(p, "valueName"), GetStr(p, "value"), ct));
                break;
            case "registry_delete":
                sb.Append(await _system.RegistryDeleteAsync(GetStr(p, "keyPath"), GetStr(p, "valueName"), ct));
                break;
            case "registry_list":
                sb.Append(await _system.RegistryListAsync(GetStr(p, "keyPath"), ct));
                break;
            case "env_get":
                sb.Append(await _system.GetEnvironmentVariableAsync(GetStr(p, "name"), ct));
                break;
            case "env_set":
                sb.Append(await _system.SetEnvironmentVariableAsync(GetStr(p, "name"), GetStr(p, "value"), ct));
                break;
            case "env_list":
                sb.Append(await _system.ListEnvironmentVariablesAsync(ct));
                break;
            case "path_get":
                sb.Append(await _system.GetPathAsync(ct));
                break;
            case "path_add":
                sb.Append(await _system.AddToPathAsync(GetStr(p, "directory"), ct));
                break;
            case "system_info":
                sb.Append(await _system.GetSystemInfoAsync(ct));
                break;
            case "processes":
                sb.Append(await _system.ListProcessesAsync(ct));
                break;
            case "kill_process":
                sb.Append(await _system.KillProcessAsync(GetInt(p, "pid"), ct));
                break;
            default:
                sb.AppendLine($"Unknown action: {action.Action}");
                break;
        }

        return sb.ToString();
    }

    /// <summary>Request user confirmation for a destructive operation.</summary>
    private bool RequestConfirmation(SystemAction action)
    {
        var args = new SystemConfirmationEventArgs
        {
            Action = action.Action,
            Description = action.Description,
            Params = action.Params ?? new(),
        };
        ConfirmationRequested?.Invoke(this, args);
        return args.Approved;
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var nl = trimmed.IndexOf('\n');
            if (nl >= 0) trimmed = trimmed[(nl + 1)..];
            var lf = trimmed.LastIndexOf("```");
            if (lf >= 0) trimmed = trimmed[..lf];
            trimmed = trimmed.Trim();
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed.Substring(start, end - start + 1) : trimmed;
    }

    private static string GetStr(Dictionary<string, object> p, string key, string def = "") =>
        p.TryGetValue(key, out var v) ? v?.ToString() ?? def : def;

    private static int GetInt(Dictionary<string, object> p, string key) =>
        p.TryGetValue(key, out var v) && int.TryParse(v?.ToString(), out var i) ? i : 0;

    public static ToolDefinition Definition => new()
    {
        Name = "system",
        Description = "Full system access: filesystem (read/write/delete files and directories), shell (run cmd/powershell/bash commands), registry (read/write/delete on Windows), environment variables, PATH management, process management, and system info. Use when the user wants to interact with their computer.",
        Category = "system",
        Triggers = new() { "file", "directory", "folder", "shell", "command", "registry", "environment", "path", "process", "system", "cmd", "powershell", "bash", "delete", "create", "read", "write", "run", "execute", "install", "kill", "tasklist", "taskkill" },
        RequiresConfirmation = true,
        Enabled = true,
    };
}

/// <summary>Parsed system action from the LLM.</summary>
public class SystemAction
{
    public string Action { get; set; } = "";
    public Dictionary<string, object> Params { get; set; } = new();
    public bool IsDestructive { get; set; } = false;
    public string Description { get; set; } = "";
}

/// <summary>Event args for confirmation requests.</summary>
public class SystemConfirmationEventArgs : EventArgs
{
    public string Action { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, object> Params { get; set; } = new();
    public bool Approved { get; set; } = false;
}
