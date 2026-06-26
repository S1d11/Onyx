using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// Built-in tool that wraps the existing WebSearchService.
/// Registered automatically so the orchestrator can route web search intents to it.
/// </summary>
public class WebSearchTool : ITool
{
    private readonly WebSearchService _search;
    private readonly int _maxResults;

    public string Name => "webSearch";

    public WebSearchTool(WebSearchService search, int maxResults = 5)
    {
        _search = search;
        _maxResults = maxResults;
    }

    public async Task<ToolResult> ExecuteAsync(string input, Intent intent, CancellationToken ct = default)
    {
        try
        {
            var result = await _search.SearchAsync(input, _maxResults, ct);

            if (result.Results.Count == 0)
                return new ToolResult { ToolName = Name, Success = true, Output = "No results found." };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Search query: {result.Query}");
            sb.AppendLine();
            for (int i = 0; i < result.Results.Count; i++)
            {
                var r = result.Results[i];
                sb.AppendLine($"[{i + 1}] {r.Title}");
                sb.AppendLine($"    URL: {r.Url}");
                sb.AppendLine($"    {r.Snippet}");
                sb.AppendLine();
            }

            return new ToolResult
            {
                ToolName = Name,
                Success = true,
                Output = sb.ToString(),
                Metadata = new() { ["sources"] = result.Results },
            };
        }
        catch (System.Exception ex)
        {
            return new ToolResult { ToolName = Name, Success = false, Output = ex.Message };
        }
    }

    public static ToolDefinition Definition => new()
    {
        Name = "webSearch",
        Description = "Search the web for current information. Use when the user asks about recent events, news, weather, prices, or anything requiring up-to-date data.",
        Category = "information",
        Triggers = new() { "search", "latest", "news", "today", "current", "weather", "price", "update" },
        RequiresConfirmation = false,
        Enabled = true,
    };
}
