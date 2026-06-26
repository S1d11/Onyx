using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// The orchestrator is the brain that sits between the user's message and the LLM response.
///
/// Flow:
///   1. User sends a message
///   2. Orchestrator extracts intent (semantic, via LLM)
///   3. Based on intent, it decides which tools to run (if any)
///   4. Tools execute and produce context
///   5. Context is injected into the LLM conversation
///   6. The LLM generates the final response with all the gathered context
///
/// When tools/agents are implemented later, they register here and the orchestrator
/// will automatically route to them based on the extracted intent.
/// </summary>
public class OrchestratorService
{
    private readonly IntentExtractor _extractor;
    private readonly ToolRegistry _tools;
    private readonly OllamaClient _ollama;
    private readonly SemanticRouter _router;

    /// <summary>Raised when the orchestrator starts extracting intent. UI can show "Analyzing...".</summary>
    public event EventHandler<OrchestratorStageEventArgs>? StageChanged;

    /// <summary>Raised when intent extraction completes. UI can show the detected intent.</summary>
    public event EventHandler<Intent>? IntentExtracted;

    /// <summary>Raised when a tool starts executing.</summary>
    public event EventHandler<ToolExecutionEventArgs>? ToolExecuting;

    /// <summary>Raised when a tool finishes executing.</summary>
    public event EventHandler<ToolResult>? ToolExecuted;

    public ToolRegistry Tools => _tools;
    public SemanticRouter Router => _router;

    public OrchestratorService(OllamaClient ollama, Func<string> getModel)
    {
        _ollama = ollama;
        _tools = new ToolRegistry();
        _router = new SemanticRouter(ollama, getModel, _tools);
        _extractor = new IntentExtractor(ollama, getModel, _tools);
    }

    /// <summary>
    /// Analyze a user message and produce an execution plan.
    /// This is the first phase — it extracts intent and decides what tools to run.
    /// </summary>
    public async Task<OrchestratorPlan> PlanAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken ct = default)
    {
        OnStage("extracting", "Analyzing your request...");

        var intent = await _extractor.ExtractAsync(userMessage, conversationHistory, ct);
        if (intent == null)
        {
            return new OrchestratorPlan
            {
                Intent = new Intent { Type = IntentType.Chat, Confidence = 0, Summary = "fallback" },
            };
        }

        IntentExtracted?.Invoke(this, intent);
        OnStage("planning", $"Intent: {intent.Type} — {intent.Summary}");

        var plan = new OrchestratorPlan { Intent = intent };

        // Determine which tools to run based on intent + semantic routing
        plan.ToolsToRun = await DetermineToolsAsync(intent, userMessage, ct);

        // Intent-specific system prompt overrides
        plan.SystemPromptOverride = GetSystemPromptForIntent(intent);

        return plan;
    }

    /// <summary>
    /// Execute the tools in the plan and collect their results as context blocks.
    /// This is the second phase — runs before the LLM generates its response.
    /// </summary>
    public async Task<List<string>> ExecuteToolsAsync(
        OrchestratorPlan plan,
        string userMessage,
        CancellationToken ct = default)
    {
        var contextBlocks = new List<string>();

        if (plan.ToolsToRun.Count == 0)
            return contextBlocks;

        foreach (var toolName in plan.ToolsToRun)
        {
            var tool = _tools.GetTool(toolName);
            var def = _tools.GetDefinition(toolName);
            if (tool == null || def == null || !def.Enabled) continue;

            // Check if the tool is connected before attempting execution
            if (!tool.IsConnected)
            {
                var connectMsg = toolName switch
                {
                    "gmail" => "Gmail is not connected. Tell the user to go to the Connections tab and connect their Google account to use Gmail features.",
                    "gdrive" => "Google Drive is not connected. Tell the user to go to the Connections tab and connect their Google account to use Google Drive features.",
                    "github" => "GitHub is not connected. Tell the user to go to the Connections tab and add their GitHub personal access token to use GitHub features.",
                    _ => $"The {toolName} tool is not connected. Tell the user to go to the Connections tab to connect it."
                };
                contextBlocks.Add($"[{toolName} — not connected]\n{connectMsg}");
                ToolExecuted?.Invoke(this, new ToolResult { ToolName = toolName, Success = false, Output = connectMsg });
                continue;
            }

            OnStage("executing", $"Running {toolName}...");
            ToolExecuting?.Invoke(this, new ToolExecutionEventArgs { ToolName = toolName, Input = userMessage });

            try
            {
                var result = await tool.ExecuteAsync(userMessage, plan.Intent, ct);
                ToolExecuted?.Invoke(this, result);

                if (!string.IsNullOrEmpty(result.Output))
                {
                    // Include both successful and failed results so the LLM can inform the user
                    var prefix = result.Success ? $"[{toolName} result]" : $"[{toolName} error]";
                    contextBlocks.Add($"{prefix}\n{result.Output}");
                }
            }
            catch (Exception ex)
            {
                ToolExecuted?.Invoke(this, new ToolResult
                {
                    ToolName = toolName,
                    Success = false,
                    Output = ex.Message,
                });
                contextBlocks.Add($"[{toolName} error]\n{ex.Message}");
            }
        }

        OnStage("generating", "Generating response...");
        return contextBlocks;
    }

    /// <summary>
    /// Full orchestration: plan + execute tools + return context for the LLM.
    /// Convenience method that combines PlanAsync and ExecuteToolsAsync.
    /// </summary>
    public async Task<(OrchestratorPlan plan, List<string> context)> OrchestrateAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken ct = default)
    {
        var plan = await PlanAsync(userMessage, conversationHistory, ct);
        var context = await ExecuteToolsAsync(plan, userMessage, ct);
        plan.ContextBlocks = context;
        return (plan, context);
    }

    /// <summary>
    /// Decide which tools to run based on the extracted intent + semantic routing.
    /// Uses embeddings (cosine similarity) to match the user's message to the best tool —
    /// no keyword matching involved.
    /// </summary>
    private async Task<List<string>> DetermineToolsAsync(Intent intent, string userMessage, CancellationToken ct)
    {
        var tools = new List<string>();

        // Use tools suggested by the LLM if they're available
        foreach (var suggested in intent.SuggestedTools)
        {
            if (_tools.IsAvailable(suggested) && !tools.Contains(suggested))
                tools.Add(suggested);
        }

        // Intent-type-based routing (built-in logic)
        if (intent.Type == IntentType.WebSearch && _tools.IsAvailable("webSearch"))
            tools.AddIfMissing("webSearch");

        if (intent.Type == IntentType.Code && _tools.IsAvailable("codeExecutor"))
            tools.AddIfMissing("codeExecutor");

        // ToolUse intent: use semantic routing to find the best tool
        if (intent.Type == IntentType.ToolUse)
        {
            // If the LLM already named a specific valid tool, use it
            if (!string.IsNullOrEmpty(intent.TargetTool) && _tools.IsAvailable(intent.TargetTool!))
            {
                tools.AddIfMissing(intent.TargetTool!);
            }
            else
            {
                // Semantic search: embed the user message and match to tool embeddings
                OnStage("routing", "Finding the right tool...");
                var semanticMatch = await _router.MatchToolAsync(userMessage, threshold: 0.30, ct: ct);
                if (!string.IsNullOrEmpty(semanticMatch))
                {
                    tools.AddIfMissing(semanticMatch);
                }
                else
                {
                    // Last resort: try semantic match on the summary instead of full message
                    var summaryMatch = await _router.MatchToolAsync(intent.Summary ?? userMessage, threshold: 0.25, ct: ct);
                    if (!string.IsNullOrEmpty(summaryMatch))
                        tools.AddIfMissing(summaryMatch);
                }
            }
        }

        // ALWAYS run semantic routing as a fallback, even for Chat/Code/etc.
        // The LLM classifier often misclassifies tool requests as "chat" (e.g. "make a folder"
        // gets classified as Chat 99%). The semantic router catches these by matching the
        // user's message against tool descriptions. If it finds a strong match, we run the tool
        // regardless of what the classifier said.
        if (tools.Count == 0)
        {
            OnStage("routing", "Finding the right tool...");
            var fallbackMatch = await _router.MatchToolAsync(userMessage, threshold: 0.30, ct: ct);
            if (!string.IsNullOrEmpty(fallbackMatch))
            {
                tools.AddIfMissing(fallbackMatch);
                // Force the intent to ToolUse so the system prompt guidance applies
                if (intent.Type != IntentType.ToolUse)
                {
                    intent.Type = IntentType.ToolUse;
                    intent.ShouldExecuteTools = true;
                }
            }
        }

        // Only auto-execute if the intent says so.
        // BUT: if the intent is explicitly toolUse with a target tool, always execute
        // (the LLM was confident enough to name a specific tool)
        if (!intent.ShouldExecuteTools && intent.Type != IntentType.ToolUse)
            tools.Clear();

        return tools;
    }

    /// <summary>Get an intent-specific system prompt to guide the LLM's response style.</summary>
    private static string GetSystemPromptForIntent(Intent intent) => intent.Type switch
    {
        IntentType.Code => "You are an expert programmer. Provide clear, well-structured code with explanations. Use proper syntax highlighting and include relevant imports.",
        IntentType.Reasoning => "You are a careful analytical thinker. Break down problems step by step. Show your work and verify your conclusions.",
        IntentType.Creative => "You are a creative writer. Be imaginative, vivid, and original. Adapt your tone to the user's request.",
        IntentType.Summarize => "You are a summarization expert. Capture the key points concisely. Preserve the essential meaning while reducing length.",
        IntentType.Translate => "You are a professional translator. Translate accurately while preserving tone, context, and cultural nuances.",
        IntentType.WebSearch => "You have access to web search results. Use them to provide accurate, up-to-date information. Cite sources inline as [1], [2], etc.",
        IntentType.ToolUse => "You are a desktop AI assistant. The user asked you to perform an action, and a tool has been executed on your behalf. " +
            "The tool result is provided as a system message. Tell the user what was done concisely. " +
            "If the tool succeeded, confirm the action was completed. If it failed, explain the error and suggest a fix. " +
            "Do NOT give the user instructions on how to do it themselves — you already did it for them.",
        _ => "",
    };

    private void OnStage(string stage, string message) =>
        StageChanged?.Invoke(this, new OrchestratorStageEventArgs { Stage = stage, Message = message });
}

public class OrchestratorStageEventArgs : EventArgs
{
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
}

public class ToolExecutionEventArgs : EventArgs
{
    public string ToolName { get; set; } = "";
    public string Input { get; set; } = "";
}

/// <summary>List extension to add an item only if it's not already present.</summary>
internal static class ListExtensions
{
    public static void AddIfMissing(this List<string> list, string item)
    {
        if (!list.Contains(item)) list.Add(item);
    }
}
