using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Orchestrator;
using Ollama2.Services;

namespace Ollama2;

/// <summary>
/// Marshals JSON messages between the web UI and the C# backend services.
/// Protocol (web -> C#):  { "id": "<rpcId>", "action": "...", ...payload }
/// Protocol (C# -> web):  { "event": "...", ... }  for push events
///                         { "id": "<rpcId>", "ok": bool, "data"|"error": ... } for RPC replies
/// </summary>
public sealed class Bridge
{
    private readonly IBridgeHost _host;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Dictionary<string, CancellationTokenSource> _chatCtsByChatId = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _confirmationResults = new();
    private CancellationTokenSource? _pullCts;
    private readonly UpdateService _updater = new();

    public Bridge(IBridgeHost host)
    {
        _host = host;
        _updater.StatusChanged += (_, e) => PostToWeb(new { @event = "updateStatus", status = e.Status, progress = e.ProgressPercent, url = e.DownloadUrl });
    }

    public void PostToWeb(object payload)
        => _host.PostMessage(JsonSerializer.Serialize(payload, _json));

    public void NotifyUpdateReady()
    {
        if (!string.IsNullOrEmpty(AppContext.PendingUpdatePath))
        {
            PostToWeb(new
            {
                @event = "updateReady",
                version = AppContext.PendingUpdateVersion,
                path = AppContext.PendingUpdatePath,
            });
        }
    }

    public async Task RefreshModelsAsync()
    {
        try
        {
            var models = await AppContext.Current.Ollama.ListModelsAsync();
            PostToWeb(new { @event = "models", models });
        }
        catch (Exception ex)
        {
            PostToWeb(new { @event = "error", message = "Cannot reach Ollama server: " + ex.Message });
        }
    }

    public void HandleMessageFromWeb(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("action", out var actEl)) return;
        var action = actEl.GetString() ?? "";
        var rpcId = root.TryGetProperty("_rpcId", out var rpcEl) ? rpcEl.GetString() ?? "" : "";
        var payload = root.TryGetProperty("payload", out var payEl) ? payEl : root;
        _ = DispatchAsync(action, rpcId, payload);
    }

    private async Task DispatchAsync(string action, string rpcId, JsonElement payload)
    {
        try
        {
            object? data = action switch
            {
                "getInitialState" => await HandleInitialState(),
                "listModels" => await AppContext.Current.Ollama.ListModelsAsync(),
                "showModel" => await AppContext.Current.Ollama.ShowModelAsync(payload.GetProperty("name").GetString()!),
                "newChat" => HandleNewChat(payload),
                "loadChat" => AppContext.Current.Chats.Get(payload.GetProperty("id").GetString()!),
                "deleteChat" => HandleDeleteChat(payload),
                "renameChat" => HandleRenameChat(payload),
                "sendMessage" => await HandleSendMessage(payload),
                "stopGeneration" => HandleStop(payload),
                "pullModel" => await HandlePull(payload),
                "stopPull" => HandleStopPull(),
                "deleteModel" => await AppContext.Current.Ollama.DeleteModelAsync(payload.GetProperty("name").GetString()!),
                "saveConfig" => HandleSaveConfig(payload),
                "testServer" => await AppContext.Current.Ollama.IsReachableAsync(),
                "fetchPage" => await AppContext.Current.WebSearch.FetchPageAsync(
                    payload.GetProperty("url").GetString()!, 6000, default),
                "exportChat" => HandleExportChat(payload),
                "checkForUpdates" => await _updater.CheckForUpdateAsync(),
                "downloadUpdate" => await _updater.DownloadUpdateAsync(
                    payload.GetProperty("release").Deserialize<ReleaseInfo>(_json)!),
                "installUpdate" => HandleInstallUpdate(payload),
                "getReleaseNotes" => await _updater.GetRecentReleasesAsync(),
                "setLaunchOnStartup" => HandleSetLaunchOnStartup(payload),
                "browseFolder" => _host.BrowseFolder(),
                "confirmSystemAction" => HandleConfirmSystemAction(payload),
                "connectGoogle" => await HandleConnectGoogle(payload),
                "disconnectGoogle" => HandleDisconnectGoogle(),
                _ => null,
            };
            ReplyOk(rpcId, data);
        }
        catch (Exception ex)
        {
            ReplyError(rpcId, ex.Message);
        }
    }

    private async Task<object> HandleInitialState()
    {
        var cfg = AppContext.Current.Config.Current;

        // Auto-launch Ollama if enabled and using localhost
        if (cfg.AutoLaunchOllama && OllamaLauncher.IsLocalhost(cfg.ServerUrl))
        {
            try
            {
                var reachable = await AppContext.Current.Ollama.IsReachableAsync();
                if (!reachable)
                {
                    PostToWeb(new { @event = "ollamaStarting" });
                    await OllamaLauncher.EnsureRunningAsync(cfg.ServerUrl);
                }
            }
            catch (Exception ex)
            {
                PostToWeb(new { @event = "error", message = "Failed to start Ollama: " + ex.Message });
            }
        }

        var reachableFinal = await AppContext.Current.Ollama.IsReachableAsync();
        var hw = HardwareDetector.Detect();

        // If a background startup update check found a new version, notify the UI
        NotifyUpdateReady();

        return new
        {
            config = cfg,
            chats = AppContext.Current.Chats.Chats,
            connectors = GetConnectorStatuses(),
            serverReachable = reachableFinal,
            appVersion = UpdateService.CurrentVersion.ToString(3),
            hardware = new
            {
                cpu = hw.CpuName,
                ramGb = hw.TotalRamGb,
                gpu = hw.GpuName,
                gpuVramGb = hw.GpuVramGb,
            },
        };
    }

    private static List<object> GetConnectorStatuses()
    {
        var cfg = AppContext.Current.Config.Current;
        var googleConnected = !string.IsNullOrEmpty(cfg.GoogleRefreshToken);
        return new()
        {
            new { id = "filesystem", name = "Filesystem", description = "Read, write, delete, and list files and directories on your computer.", color = "#6366f1", connected = true },
            new { id = "system", name = "System", description = "Run shell commands, manage registry, environment variables, PATH, and processes.", color = "#f59e0b", connected = true },
            new { id = "github", name = "GitHub", description = "Access your repositories, issues, pull requests, and code. Search repos, create issues, read files, and more.", color = "#181717", connected = !string.IsNullOrEmpty(cfg.GitHubToken) },
            new { id = "gmail", name = "Gmail", description = "List, search, read, and send emails from your Gmail account.", color = "#ea4335", connected = googleConnected },
            new { id = "gdrive", name = "Google Drive", description = "List, search, read, upload, and manage files in Google Drive.", color = "#0f9d58", connected = googleConnected },
        };
    }

    private StoredChat HandleNewChat(JsonElement root)
    {
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? AppContext.Current.Config.Current.DefaultModel : AppContext.Current.Config.Current.DefaultModel;
        var chat = AppContext.Current.Chats.Create(model);
        AppContext.Current.Chats.Save();
        return chat;
    }

    private bool HandleDeleteChat(JsonElement root)
    {
        AppContext.Current.Chats.Delete(root.GetProperty("id").GetString()!);
        AppContext.Current.Chats.Save();
        return true;
    }

    private bool HandleRenameChat(JsonElement root)
    {
        var c = AppContext.Current.Chats.Get(root.GetProperty("id").GetString()!);
        if (c != null)
        {
            c.Title = root.GetProperty("title").GetString()!;
            AppContext.Current.Chats.Save();
        }
        return true;
    }

    private bool HandleStop(JsonElement root)
    {
        // Stop a specific chat's generation, or all if no chatId given
        if (root.TryGetProperty("chatId", out var cidEl) && cidEl.ValueKind == JsonValueKind.String)
        {
            var chatId = cidEl.GetString()!;
            if (_chatCtsByChatId.TryGetValue(chatId, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }
        else
        {
            // Stop all
            foreach (var cts in _chatCtsByChatId.Values)
            {
                try { cts.Cancel(); } catch { }
            }
        }
        return true;
    }

    private bool HandleStopPull()
    {
        try { _pullCts?.Cancel(); } catch { }
        return true;
    }

    private async Task<bool> HandlePull(JsonElement root)
    {
        var name = root.GetProperty("name").GetString()!;
        try { _pullCts?.Dispose(); } catch { }
        _pullCts = new CancellationTokenSource();
        try
        {
            await foreach (var p in AppContext.Current.Ollama.PullAsync(name, _pullCts.Token))
            {
                PostToWeb(new { @event = "pullProgress", name, status = p.Status, percent = Math.Round(p.Percent, 1), completed = p.Completed, total = p.Total });
            }
            PostToWeb(new { @event = "pullDone", name });
            await RefreshModelsAsync();
        }
        catch (OperationCanceledException)
        {
            PostToWeb(new { @event = "pullCancelled", name });
        }
        catch (Exception ex)
        {
            PostToWeb(new { @event = "pullError", name, message = ex.Message });
        }
        finally
        {
            try { _pullCts?.Dispose(); } catch { }
            _pullCts = null;
        }
        return true;
    }

    private bool HandleSaveConfig(JsonElement root)
    {
        var cfg = root.GetProperty("config").Deserialize<AppConfig>(_json)!;
        AppContext.Current.Config.Update(cfg);
        return true;
    }

    private bool HandleSetLaunchOnStartup(JsonElement root)
    {
        var enabled = root.GetProperty("enabled").GetBoolean();
        StartupRegistration.Instance?.SetEnabled(enabled);
        return true;
    }

    private bool HandleConfirmSystemAction(JsonElement root)
    {
        var confirmId = root.GetProperty("confirmId").GetString()!;
        var approved = root.GetProperty("approved").GetBoolean();
        _confirmationResults[confirmId] = approved;
        return true;
    }

    private async Task<object> HandleConnectGoogle(JsonElement root)
    {
        var clientId = root.GetProperty("clientId").GetString()!;
        var clientSecret = root.GetProperty("clientSecret").GetString()!;
        var oauth = AppContext.Current.GoogleOAuth;
        var refreshToken = await oauth.StartOAuthFlowAsync(clientId, clientSecret, default);
        return new { success = !string.IsNullOrEmpty(refreshToken), connected = !string.IsNullOrEmpty(refreshToken) };
    }

    private bool HandleDisconnectGoogle()
    {
        AppContext.Current.GoogleOAuth.Disconnect();
        return true;
    }

    private bool HandleInstallUpdate(JsonElement root)
    {
        var path = root.GetProperty("path").GetString()!;
        _updater.InstallUpdate(path);
        return true;
    }

    private string HandleExportChat(JsonElement root)
    {
        var chat = AppContext.Current.Chats.Get(root.GetProperty("id").GetString()!);
        if (chat == null) return "";
        var sb = new StringBuilder();
        foreach (var m in chat.Messages)
        {
            sb.AppendLine($"# {m.Role.ToUpperInvariant()}");
            sb.AppendLine(m.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<bool> HandleSendMessage(JsonElement payload)
    {
        var chatId = payload.GetProperty("chatId").GetString()!;
        var model = payload.GetProperty("model").GetString()!;
        var messages = payload.GetProperty("messages").Deserialize<List<ChatMessage>>(_json) ?? new List<ChatMessage>();
        var cfg = AppContext.Current.Config.Current;
        var webSearchMode = payload.TryGetProperty("webSearchMode", out var wsm) ? wsm.GetString() ?? "off" : "off";
        var chat = AppContext.Current.Chats.Get(chatId);
        if (chat == null) return false;

        // Title from first user message (temporary — will be refined by LLM after response)
        var needsTitle = (chat.Title == "New Chat" || string.IsNullOrEmpty(chat.Title));
        if (needsTitle && messages.FirstOrDefault(x => x.Role == "user")?.Content is { Length: > 0 } firstUser)
        {
            chat.Title = firstUser.Length > 48 ? firstUser.Substring(0, 48) + "…" : firstUser;
            // Notify the client immediately so the sidebar updates
            PostToWeb(new { @event = "chatTitle", chatId, title = chat.Title });
        }

        // Store the latest user message (with images if any) so it persists across reloads
        var lastUserMsg = messages.LastOrDefault(x => x.Role == "user");
        var lastUser = lastUserMsg?.Content ?? "";
        if (lastUserMsg != null)
        {
            chat.Messages.Add(new ChatStoredMessage
            {
                Role = "user",
                Content = lastUserMsg.Content,
                Images = lastUserMsg.Images,
            });
        }

        // Cancel any previous generation for THIS chat only (other chats keep running)
        if (_chatCtsByChatId.TryGetValue(chatId, out var existingCts))
        {
            try { existingCts.Cancel(); existingCts.Dispose(); } catch { }
            _chatCtsByChatId.Remove(chatId);
        }
        var chatCts = new CancellationTokenSource();
        _chatCtsByChatId[chatId] = chatCts;
        var ct = chatCts.Token;

        var sw = Stopwatch.StartNew();
        List<SearchSource>? sources = null;

        try
        {
            // Build the message list we'll send to the model.
            var sendMessages = new List<ChatMessage>(messages);

            // Optional system prompt from settings.
            if (!string.IsNullOrWhiteSpace(cfg.SystemPrompt))
                sendMessages.Insert(0, new ChatMessage { Role = "system", Content = cfg.SystemPrompt });

            // Thinking mode: add a reasoning system prompt.
            if (cfg.ThinkingEnabled)
                sendMessages.Insert(0, new ChatMessage { Role = "system", Content = "Think step-by-step. Break down complex problems, consider multiple angles, and explain your reasoning clearly before giving the final answer." });

            // ---- Orchestrator: extract intent and run tools ----
            // The orchestrator uses the LLM to semantically understand the user's message,
            // then decides which tools (web search, code exec, etc) to invoke.
            var orchestrator = AppContext.Current.Orchestrator;

            // Subscribe to orchestrator stage events for UI feedback
            EventHandler<OrchestratorStageEventArgs>? stageHandler = (_, e) =>
                PostToWeb(new { @event = "orchestratorStage", chatId, stage = e.Stage, message = e.Message });
            EventHandler<Intent>? intentHandler = (_, intent) =>
                PostToWeb(new { @event = "intent", chatId, intentType = intent.Type.ToString(), confidence = intent.Confidence, summary = intent.Summary, entities = intent.Entities, suggestedTools = intent.SuggestedTools });
            EventHandler<ToolExecutionEventArgs>? toolExecHandler = (_, e) =>
                PostToWeb(new { @event = "toolExecuting", chatId, tool = e.ToolName });
            EventHandler<ToolResult>? toolResultHandler = (_, r) =>
                PostToWeb(new { @event = "toolExecuted", chatId, tool = r.ToolName, success = r.Success });

            // System tool confirmation handler — posts a confirmation dialog to the UI
            // and waits for the user to approve/deny
            EventHandler<SystemConfirmationEventArgs>? confirmHandler = null;
            var systemTool = AppContext.Current.SystemTool;
            if (systemTool != null)
            {
                confirmHandler = (_, e) =>
                {
                    // Post confirmation request to UI
                    var confirmId = Guid.NewGuid().ToString("N");
                    PostToWeb(new
                    {
                        @event = "systemConfirmation",
                        chatId,
                        confirmId,
                        action = e.Action,
                        description = e.Description,
                        @params = e.Params,
                    });
                    // Wait for user response (with timeout)
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline && !e.Approved)
                    {
                        if (_confirmationResults.TryRemove(confirmId, out var approved))
                        {
                            e.Approved = approved;
                            break;
                        }
                        Thread.Sleep(200);
                        if (ct.IsCancellationRequested) break;
                    }
                };
                systemTool.ConfirmationRequested += confirmHandler;
            }

            orchestrator.StageChanged += stageHandler;
            orchestrator.IntentExtracted += intentHandler;
            orchestrator.ToolExecuting += toolExecHandler;
            orchestrator.ToolExecuted += toolResultHandler;

            OrchestratorPlan? plan = null;
            try
            {
                // If web search is explicitly on, skip orchestrator intent extraction and search directly
                if (webSearchMode == "on" && cfg.WebSearchEnabled)
                {
                    PostToWeb(new { @event = "searching", chatId, query = lastUser });
                    var search = await AppContext.Current.WebSearch.SearchAsync(lastUser, cfg.MaxSearchResults, ct);
                    sources = search.Results;
                    PostToWeb(new { @event = "searchResults", chatId, query = search.Query, results = search.Results });
                    sendMessages.Add(new ChatMessage { Role = "system", Content = BuildSearchContext(search) });
                }
                else
                {
                    // Run the orchestrator: extract intent + execute tools
                    (plan, var contextBlocks) = await orchestrator.OrchestrateAsync(lastUser, sendMessages, ct);

                    // If the web search tool ran, extract sources for the UI
                    if (plan.ToolsToRun.Contains("webSearch"))
                    {
                        foreach (var block in contextBlocks)
                        {
                            if (block.StartsWith("[webSearch result]"))
                                sendMessages.Add(new ChatMessage { Role = "system", Content = block });
                        }
                        // Also do a direct search to get structured sources for the UI cards
                        if (cfg.WebSearchEnabled)
                        {
                            PostToWeb(new { @event = "searching", chatId, query = lastUser });
                            var search = await AppContext.Current.WebSearch.SearchAsync(lastUser, cfg.MaxSearchResults, ct);
                            sources = search.Results;
                            PostToWeb(new { @event = "searchResults", chatId, query = search.Query, results = search.Results });
                        }
                    }
                    else
                    {
                        // Inject any other tool context blocks
                        foreach (var block in contextBlocks)
                            sendMessages.Add(new ChatMessage { Role = "system", Content = block });
                    }

                    // Apply intent-specific system prompt if the orchestrator provided one
                    if (!string.IsNullOrEmpty(plan.SystemPromptOverride))
                        sendMessages.Insert(0, new ChatMessage { Role = "system", Content = plan.SystemPromptOverride });
                }
            }
            finally
            {
                orchestrator.StageChanged -= stageHandler;
                orchestrator.IntentExtracted -= intentHandler;
                orchestrator.ToolExecuting -= toolExecHandler;
                orchestrator.ToolExecuted -= toolResultHandler;
                if (systemTool != null && confirmHandler != null)
                    systemTool.ConfirmationRequested -= confirmHandler;
            }

            var req = new ChatRequest
            {
                Model = model,
                Messages = sendMessages,
                Stream = true,
                Options = new Dictionary<string, object>
                {
                    { "temperature", EffortToTemperature(cfg.Effort) },
                    { "top_k", cfg.TopK },
                    { "top_p", cfg.TopP },
                    { "num_ctx", cfg.NumCtx },
                },
            };

            var assistant = new StringBuilder();
            long evalCount = 0;
            await foreach (var chunk in AppContext.Current.Ollama.ChatStreamAsync(req, ct))
            {
                if (ct.IsCancellationRequested) break;
                var piece = chunk.Message?.Content;
                if (!string.IsNullOrEmpty(piece))
                {
                    assistant.Append(piece);
                    PostToWeb(new { @event = "chatChunk", chatId, content = piece });
                }
                if (chunk.Done)
                {
                    evalCount = chunk.EvalCount;
                }
            }

            var finalText = assistant.ToString();
            var (mainText, thinkingText) = ExtractThinking(finalText);
            var stored = new ChatStoredMessage
            {
                Role = "assistant",
                Content = mainText,
                Thinking = thinkingText,
                Sources = sources,
                EvalCount = evalCount,
                TotalMs = sw.ElapsedMilliseconds,
            };
            chat.Messages.Add(stored);
            AppContext.Current.Chats.Touch(chatId);
            AppContext.Current.Chats.Save();

            PostToWeb(new
            {
                @event = "chatDone",
                chatId,
                sources,
                evalCount,
                totalMs = sw.ElapsedMilliseconds,
                cancelled = ct.IsCancellationRequested,
            });

            // Generate a proper title via LLM if this was the first message
            if (needsTitle && !ct.IsCancellationRequested)
            {
                _ = GenerateTitleAsync(chatId, lastUser, mainText, ct);
            }
        }
        catch (OperationCanceledException)
        {
            PostToWeb(new { @event = "chatDone", chatId, cancelled = true });
        }
        catch (OllamaException ex)
        {
            chat.Messages.Add(new ChatStoredMessage { Role = "assistant", Content = "", Error = ex.Message });
            AppContext.Current.Chats.Save();
            PostToWeb(new { @event = "chatError", chatId, message = ex.Message });
        }
        catch (Exception ex)
        {
            PostToWeb(new { @event = "chatError", chatId, message = ex.Message });
        }
        finally
        {
            // Clean up this chat's CTS from the concurrent map
            _chatCtsByChatId.Remove(chatId);
            if (chatCts != null)
            {
                try { chatCts.Dispose(); } catch { }
            }
        }
        return true;
    }

    private static double EffortToTemperature(string effort)
        => effort?.ToLowerInvariant() switch
        {
            "low" => 0.3,
            "medium" => 0.7,
            "high" => 0.95,
            "max" => 1.2,
            _ => 0.7,
        };

    /// <summary>Generate a concise chat title from the first user message + assistant response using the LLM.</summary>
    private async Task GenerateTitleAsync(string chatId, string userMessage, string assistantResponse, CancellationToken ct)
    {
        try
        {
            // Truncate the assistant response to keep the title generation fast
            var truncatedResponse = assistantResponse.Length > 500 ? assistantResponse[..500] + "…" : assistantResponse;

            var titleReq = new ChatRequest
            {
                Model = AppContext.Current.Config.Current.DefaultModel,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = "Generate a short, descriptive title (max 5 words) for this conversation. Respond with ONLY the title — no quotes, no explanation, no punctuation at the end." },
                    new() { Role = "user", Content = $"User: {userMessage}\n\nAssistant: {truncatedResponse}" },
                },
                Stream = false,
                Options = new Dictionary<string, object>
                {
                    { "temperature", 0.3 },
                    { "top_k", 10 },
                    { "top_p", 0.8 },
                    { "num_ctx", 1024 },
                    { "num_predict", 20 },
                },
            };

            var titleResp = await AppContext.Current.Ollama.ChatOnceAsync(titleReq, ct);
            var title = titleResp.Message?.Content?.Trim().Trim('"', '\'', '.', ':', '-') ?? "";
            if (string.IsNullOrEmpty(title)) return;
            if (title.Length > 60) title = title.Substring(0, 60) + "…";

            var chat = AppContext.Current.Chats.Get(chatId);
            if (chat == null) return;
            chat.Title = title;
            AppContext.Current.Chats.Save();

            PostToWeb(new { @event = "chatTitle", chatId, title });
        }
        catch
        {
            // Title generation failure is non-critical — keep the truncated first-message title
        }
    }

    /// <summary>Extract  ...  or <thinking>...</thinking> content, returning (main, thinking).</summary>
    private static (string main, string? thinking) ExtractThinking(string text)
    {
        if (string.IsNullOrEmpty(text)) return (text, null);
        var openTags = new[] { "", "<thinking>" };
        var closeTags = new[] { "", "</thinking>" };
        int openIdx = -1, closeIdx = -1;
        string openTag = "", closeTag = "";
        foreach (var tag in openTags)
        {
            var idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (idx != -1 && (openIdx == -1 || idx < openIdx)) { openIdx = idx; openTag = tag; }
        }
        if (openIdx == -1) return (text, null);
        foreach (var tag in closeTags)
        {
            var idx = text.IndexOf(tag, openIdx + openTag.Length, StringComparison.OrdinalIgnoreCase);
            if (idx != -1 && (closeIdx == -1 || idx < closeIdx)) { closeIdx = idx; closeTag = tag; }
        }
        if (closeIdx == -1) return (text, null); // unclosed tag: keep raw
        var before = text.Substring(0, openIdx);
        var thinking = text.Substring(openIdx + openTag.Length, closeIdx - openIdx - openTag.Length);
        var after = text.Substring(closeIdx + closeTag.Length);
        var main = before + after;
        return (main.TrimStart(), thinking.Trim());
    }

    private static string BuildSearchContext(WebSearchResult search)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You have access to up-to-date web search results. Use them to answer the user's question.");
        sb.AppendLine("Cite sources inline as [1], [2], ... matching the numbered list below, and prefer the snippets.");
        sb.AppendLine($"Search query: {search.Query}");
        sb.AppendLine();
        for (int i = 0; i < search.Results.Count; i++)
        {
            var r = search.Results[i];
            sb.AppendLine($"[{i + 1}] {r.Title}");
            sb.AppendLine($"    URL: {r.Url}");
            sb.AppendLine($"    {r.Snippet}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static bool NeedsWebSearch(string query)
    {
        var q = query.ToLowerInvariant();
        var triggers = new[] { "weather", "news", "score", "latest", "today", "current", "price", "stock", "who won", "election", "update on" };
        return triggers.Any(t => q.Contains(t));
    }

    private void ReplyOk(string rpcId, object? data)
    {
        if (string.IsNullOrEmpty(rpcId)) return;
        PostToWeb(new { id = rpcId, ok = true, data });
    }

    private void ReplyError(string rpcId, string message)
    {
        if (string.IsNullOrEmpty(rpcId))
        {
            PostToWeb(new { @event = "error", message });
            return;
        }
        PostToWeb(new { id = rpcId, ok = false, error = message });
    }
}
