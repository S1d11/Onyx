using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// Extracts user intent by sending a classification prompt to the LLM and parsing
/// the structured JSON response. This is semantic understanding, not keyword matching.
///
/// The LLM is asked to analyze the user's message and return a JSON object with:
///   - intent: one of the known intent types
///   - confidence: 0.0–1.0
///   - summary: what the user wants
///   - entities: key topics/keywords
///   - language: detected language code
///   - suggestedTools: which tools would help answer this
///   - shouldExecuteTools: whether tools should auto-run
/// </summary>
public class IntentExtractor
{
    private readonly OllamaClient _ollama;
    private readonly Func<string> _getModel;
    private readonly ToolRegistry _tools;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Small/fast model used for intent classification. Falls back to the main model.</summary>
    private string ClassifierModel => _getModel();

    public IntentExtractor(OllamaClient ollama, Func<string> getModel, ToolRegistry tools)
    {
        _ollama = ollama;
        _getModel = getModel;
        _tools = tools;
    }

    /// <summary>
    /// Extract intent from the user's latest message, using conversation context.
    /// Returns null if extraction fails (caller should fall back to plain chat).
    /// </summary>
    public async Task<Intent?> ExtractAsync(
        string userMessage,
        List<ChatMessage>? conversationHistory = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return null;

        var toolList = BuildToolList();
        var systemPrompt = BuildSystemPrompt(toolList);

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = systemPrompt },
        };

        // Include recent conversation context (last 4 messages) for disambiguation
        if (conversationHistory != null)
        {
            var recent = conversationHistory.TakeLast(4).Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = Truncate(m.Content, 200),
            }).ToList();
            messages.AddRange(recent);
        }

        // The actual message to classify
        messages.Add(new ChatMessage { Role = "user", Content = userMessage });

        var req = new ChatRequest
        {
            Model = ClassifierModel,
            Messages = messages,
            Stream = false,
            Options = new Dictionary<string, object>
            {
                { "temperature", 0.1 },     // Low temperature for consistent classification
                { "top_k", 10 },
                { "top_p", 0.8 },
                { "num_ctx", 2048 },        // Small context — classification doesn't need much
            },
        };

        try
        {
            var resp = await _ollama.ChatOnceAsync(req, ct);
            var raw = resp.Message?.Content ?? "";
            var json = ExtractJson(raw);
            if (string.IsNullOrEmpty(json)) return FallbackIntent(userMessage);

            var intent = JsonSerializer.Deserialize<Intent>(json, JsonOpts);
            if (intent == null) return FallbackIntent(userMessage);

            // Validate and clamp
            intent.Confidence = Math.Clamp(intent.Confidence, 0.0, 1.0);
            if (string.IsNullOrEmpty(intent.Summary))
                intent.Summary = Truncate(userMessage, 100);

            return intent;
        }
        catch
        {
            return FallbackIntent(userMessage);
        }
    }

    /// <summary>The system prompt that instructs the LLM to act as an intent classifier.</summary>
    private string BuildSystemPrompt(string toolList)
    {
        return "You are an intent extraction system for a desktop AI assistant that HAS FULL ACCESS to the user's computer.\n" +
               "Analyze the user's message and determine their intent.\n" +
               "Respond ONLY with a JSON object — no markdown, no explanation, no code fences.\n\n" +
               "Available intent types:\n" +
               "- \"chat\": General conversation, Q&A, explanations, opinions. The user wants YOU to talk or explain.\n" +
               "- \"code\": Writing, debugging, refactoring, or explaining code. The user wants code snippets or programming help.\n" +
               "- \"webSearch\": Needs current/real-time information from the web.\n" +
               "- \"reasoning\": Mathematical, logical, or analytical problem-solving.\n" +
               "- \"creative\": Creative writing, brainstorming, ideation, stories.\n" +
               "- \"summarize\": Summarizing or condensing text.\n" +
               "- \"translate\": Translating between languages.\n" +
               "- \"toolUse\": The user wants to DO something on their computer — not just talk about it.\n\n" +
               "Available tools:\n" + toolList + "\n\n" +
               "CRITICAL RULES FOR CLASSIFICATION:\n" +
               "- If the user wants to PERFORM an action on their computer (create/read/write/delete files, run commands, check system info, manage processes, modify registry/env, browse directories), classify as \"toolUse\" with targetTool \"system\" and shouldExecuteTools=true.\n" +
               "- If the user says \"make\", \"create\", \"delete\", \"list\", \"show me\", \"find\", \"run\", \"execute\", \"check\", \"read\", \"write\" followed by a file/directory/program/system reference, this is \"toolUse\" with targetTool \"system\".\n" +
               "- If the user is asking a QUESTION that could be answered with general knowledge, classify as \"chat\".\n" +
               "- If the user wants code to COPY and run themselves, classify as \"code\".\n" +
               "- If the user wants YOU to run something on their computer, classify as \"toolUse\" with targetTool \"system\".\n" +
               "- When in doubt about whether to use the system tool, prefer \"toolUse\" with targetTool \"system\" — the user will see a confirmation dialog for destructive actions.\n\n" +
               "Analyze the SEMANTIC MEANING of the message — what the user is trying to accomplish — not just keywords.\n" +
               "Consider the full context of the conversation if provided.\n\n" +
               "Respond with this exact JSON structure:\n" +
               "{\"intent\":\"toolUse\",\"confidence\":0.95,\"summary\":\"Create empty file named hello in Downloads\",\"entities\":[\"file\",\"hello\",\"Downloads\"],\"language\":\"en\",\"targetTool\":\"system\",\"suggestedTools\":[\"system\"],\"shouldExecuteTools\":true}\n\n" +
               "Rules:\n" +
               "- \"intent\" must be one of the exact values listed above\n" +
               "- \"confidence\" is 0.0 to 1.0 — how sure you are\n" +
               "- \"summary\" is a short description of the user's goal\n" +
               "- \"entities\" are key topics, technologies, or nouns\n" +
               "- \"language\" is the ISO code of the detected language\n" +
               "- \"targetTool\" is the tool name if intent is \"toolUse\", otherwise null\n" +
               "- \"suggestedTools\" lists tools that would help answer this (can be empty)\n" +
               "- \"shouldExecuteTools\" is true if tools should run automatically before generating the answer\n" +
               "- For system actions, ALWAYS set shouldExecuteTools to true so the action actually runs\n";
    }

    /// <summary>Build a description of available tools for the classifier prompt.</summary>
    private string BuildToolList()
    {
        var sb = new StringBuilder();
        foreach (var def in _tools.Definitions)
        {
            var status = def.Enabled ? "available" : "disabled";
            sb.AppendLine($"- {def.Name}: {def.Description} [{status}]");
        }
        if (sb.Length == 0)
            sb.AppendLine("- (no tools registered yet)");
        return sb.ToString();
    }

    /// <summary>Extract JSON from a model response that might have surrounding text.</summary>
    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var trimmed = raw.Trim();

        // Strip markdown code fences if present
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0) trimmed = trimmed[(firstNewline + 1)..];
            var lastFence = trimmed.LastIndexOf("```");
            if (lastFence >= 0) trimmed = trimmed[..lastFence];
            trimmed = trimmed.Trim();
        }

        // Find the JSON object boundaries
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed.Substring(start, end - start + 1);

        return trimmed;
    }

    /// <summary>Fallback intent when LLM classification fails — basic heuristic.</summary>
    private static Intent FallbackIntent(string message)
    {
        var lower = message.ToLowerInvariant();
        var intent = new Intent
        {
            Type = IntentType.Chat,
            Confidence = 0.5,
            Summary = Truncate(message, 100),
            Language = "en",
        };

        // Very basic fallback heuristics (only used if LLM is unreachable)
        if (lower.Contains("code") || lower.Contains("function") || lower.Contains("bug") || lower.Contains("error"))
            intent.Type = IntentType.Code;
        else if (lower.Contains("search") || lower.Contains("latest") || lower.Contains("news") || lower.Contains("today"))
            intent.Type = IntentType.WebSearch;
        else if (lower.Contains("calculate") || lower.Contains("math") || lower.Contains("solve"))
            intent.Type = IntentType.Reasoning;
        else if (lower.Contains("write") || lower.Contains("story") || lower.Contains("poem") || lower.Contains("creative"))
            intent.Type = IntentType.Creative;
        else if (lower.Contains("summarize") || lower.Contains("summary") || lower.Contains("tldr"))
            intent.Type = IntentType.Summarize;
        else if (lower.Contains("translate") || lower.Contains("translation"))
            intent.Type = IntentType.Translate;

        return intent;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);
}
