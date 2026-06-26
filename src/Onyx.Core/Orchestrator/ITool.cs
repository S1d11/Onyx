using System.Threading;
using System.Threading.Tasks;

namespace Ollama2.Orchestrator;

/// <summary>
/// Interface for a tool or agent that the orchestrator can dispatch to.
/// Implement this for each capability (web search, code execution, file ops, API calls, etc).
/// </summary>
public interface ITool
{
    /// <summary>Unique name matching a <see cref="ToolDefinition.Name"/>.</summary>
    string Name { get; }

    /// <summary>Execute the tool with the given input and return a result.</summary>
    Task<ToolResult> ExecuteAsync(string input, Intent intent, CancellationToken ct = default);

    /// <summary>Whether this tool is connected and ready to use. If false, the orchestrator will tell the user to connect it rather than attempting execution.</summary>
    bool IsConnected { get; }
}

/// <summary>
/// Registry of available tools. The orchestrator queries this to find tools
/// matching the extracted intent.
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly Dictionary<string, ToolDefinition> _definitions = new();

    /// <summary>Register a tool implementation with its metadata.</summary>
    public void Register(ToolDefinition definition, ITool implementation)
    {
        _definitions[definition.Name] = definition;
        _tools[definition.Name] = implementation;
    }

    /// <summary>Register only metadata (tool implementation added later).</summary>
    public void RegisterDefinition(ToolDefinition definition)
    {
        _definitions[definition.Name] = definition;
    }

    /// <summary>Get a tool implementation by name, or null.</summary>
    public ITool? GetTool(string name) =>
        _tools.TryGetValue(name, out var t) ? t : null;

    /// <summary>Get tool metadata by name.</summary>
    public ToolDefinition? GetDefinition(string name) =>
        _definitions.TryGetValue(name, out var d) ? d : null;

    /// <summary>All registered tool definitions.</summary>
    public IReadOnlyCollection<ToolDefinition> Definitions => _definitions.Values;

    /// <summary>All registered tool implementations.</summary>
    public IReadOnlyCollection<ITool> Tools => _tools.Values;

    /// <summary>Check if a tool is registered and enabled.</summary>
    public bool IsAvailable(string name) =>
        _definitions.TryGetValue(name, out var d) && d.Enabled && _tools.ContainsKey(name);
}
