using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ollama2.Orchestrator;

/// <summary>
/// The high-level category of what the user wants.
/// Used by the orchestrator to decide which tools/agents to invoke.
/// </summary>
public enum IntentType
{
    /// <summary>General conversation, Q&A, explanations.</summary>
    Chat,
    /// <summary>Writing, refactoring, or debugging code.</summary>
    Code,
    /// <summary>Needs up-to-date information from the web.</summary>
    WebSearch,
    /// <summary>Mathematical or logical computation.</summary>
    Reasoning,
    /// <summary>Creative writing, brainstorming, ideation.</summary>
    Creative,
    /// <summary>Summarizing documents or long text.</summary>
    Summarize,
    /// <summary>Translating between languages.</summary>
    Translate,
    /// <summary>A specific tool should be invoked (future: agents, APIs, etc).</summary>
    ToolUse,
}

/// <summary>
/// The extracted intent from a user message, produced by <see cref="IntentExtractor"/>.
/// </summary>
public class Intent
{
    /// <summary>The primary intent category.</summary>
    public IntentType Type { get; set; } = IntentType.Chat;

    /// <summary>Confidence score 0.0–1.0 from the classifier.</summary>
    public double Confidence { get; set; } = 1.0;

    /// <summary>Human-readable summary of what the user wants.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Specific entities, topics, or keywords detected (e.g. "python", "react", "weather").</summary>
    public List<string> Entities { get; set; } = new();

    /// <summary>The detected language (e.g. "en", "es", "fr").</summary>
    public string Language { get; set; } = "en";

    /// <summary>If ToolUse, which tool name was detected.</summary>
    public string? TargetTool { get; set; }

    /// <summary>Suggested routing decision — which tools/agents to invoke.</summary>
    public List<string> SuggestedTools { get; set; } = new();

    /// <summary>Whether the orchestrator should auto-execute the suggested tools.</summary>
    public bool ShouldExecuteTools { get; set; } = false;

    public override string ToString() => $"Intent: {Type} (conf={Confidence:F2}) — {Summary}";
}

/// <summary>
/// Metadata describing a tool or agent that the orchestrator can dispatch to.
/// </summary>
public class ToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "general";

    /// <summary>Keywords/patterns that trigger this tool during intent extraction.</summary>
    public List<string> Triggers { get; set; } = new();

    /// <summary>Whether this tool requires user confirmation before executing.</summary>
    public bool RequiresConfirmation { get; set; } = false;

    /// <summary>Whether the tool is currently enabled.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Result of running a tool through the orchestrator.
/// </summary>
public class ToolResult
{
    public string ToolName { get; set; } = "";
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// The full orchestrator decision for a user message.
/// </summary>
public class OrchestratorPlan
{
    public Intent Intent { get; set; } = new();
    public List<string> ToolsToRun { get; set; } = new();
    public string SystemPromptOverride { get; set; } = "";

    /// <summary>Extra context to inject into the LLM conversation (from tool results, search, etc).</summary>
    public List<string> ContextBlocks { get; set; } = new();

    /// <summary>Whether the orchestrator modified the message flow (vs. plain chat).</summary>
    public bool HasModifications => ToolsToRun.Count > 0 || !string.IsNullOrEmpty(SystemPromptOverride) || ContextBlocks.Count > 0;
}
