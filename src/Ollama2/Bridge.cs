using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ollama2.Services;

namespace Ollama2;

/// <summary>
/// Marshals JSON messages between the WebView2 UI and the C# backend services.
/// Protocol (web -> C#):  { "id": "<rpcId>", "action": "...", ...payload }
/// Protocol (C# -> web):  { "event": "...", ... }  for push events
///                         { "id": "<rpcId>", "ok": bool, "data"|"error": ... } for RPC replies
/// </summary>
internal sealed class Bridge
{
    private readonly MainWindow _win;
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Dictionary<string, CancellationTokenSource> _chatCtsByChatId = new();
    private CancellationTokenSource? _pullCts;
    private readonly UpdateService _updater = new();

    public Bridge(MainWindow win)
    {
        _win = win;
        _updater.StatusChanged += (_, e) => PostToWeb(new { @event = "updateStatus", status = e.Status, progress = e.ProgressPercent, url = e.DownloadUrl });
    }

    private App App => (App)Application.Current;

    public void PostToWeb(object payload)
        => _win.PostWebMessageAsJson(JsonSerializer.Serialize(payload, _json));

    public void NotifyUpdateReady()
    {
        if (!string.IsNullOrEmpty(App.PendingUpdatePath))
        {
            PostToWeb(new
            {
                @event = "updateReady",
                version = App.PendingUpdateVersion,
                path = App.PendingUpdatePath,
            });
        }
    }

    public async Task RefreshModelsAsync()
    {
        try
        {
            var models = await App.Ollama.ListModelsAsync();
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
                "listModels" => await App.Ollama.ListModelsAsync(),
                "showModel" => await App.Ollama.ShowModelAsync(payload.GetProperty("name").GetString()!),
                "newChat" => HandleNewChat(payload),
                "loadChat" => App.Chats.Get(payload.GetProperty("id").GetString()!),
                "deleteChat" => HandleDeleteChat(payload),
                "renameChat" => HandleRenameChat(payload),
                "sendMessage" => await HandleSendMessage(payload),
                "stopGeneration" => HandleStop(payload),
                "pullModel" => await HandlePull(payload),
                "stopPull" => HandleStopPull(),
                "deleteModel" => await App.Ollama.DeleteModelAsync(payload.GetProperty("name").GetString()!),
                "saveConfig" => HandleSaveConfig(payload),
                "testServer" => await App.Ollama.IsReachableAsync(),
                "fetchPage" => await App.WebSearch.FetchPageAsync(
                    payload.GetProperty("url").GetString()!, 6000, default),
                "exportChat" => HandleExportChat(payload),
                "checkForUpdates" => await _updater.CheckForUpdateAsync(),
                "downloadUpdate" => await _updater.DownloadUpdateAsync(
                    payload.GetProperty("release").Deserialize<ReleaseInfo>(_json)!),
                "installUpdate" => HandleInstallUpdate(payload),
                "getReleaseNotes" => await _updater.GetRecentReleasesAsync(),
                "browseFolder" => HandleBrowseFolder(),
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
        var cfg = App.Config.Current;

        // Auto-launch Ollama if enabled and using localhost
        if (cfg.AutoLaunchOllama && OllamaLauncher.IsLocalhost(cfg.ServerUrl))
        {
            try
            {
                var reachable = await App.Ollama.IsReachableAsync();
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

        var reachableFinal = await App.Ollama.IsReachableAsync();
        var hw = HardwareDetector.Detect();

        // If a background startup update check found a new version, notify the UI
        NotifyUpdateReady();

        return new
        {
            config = cfg,
            chats = App.Chats.Chats,
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

    private StoredChat HandleNewChat(JsonElement root)
    {
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? App.Config.Current.DefaultModel : App.Config.Current.DefaultModel;
        var chat = App.Chats.Create(model);
        App.Chats.Save();
        return chat;
    }

    private bool HandleDeleteChat(JsonElement root)
    {
        App.Chats.Delete(root.GetProperty("id").GetString()!);
        App.Chats.Save();
        return true;
    }

    private bool HandleRenameChat(JsonElement root)
    {
        var c = App.Chats.Get(root.GetProperty("id").GetString()!);
        if (c != null)
        {
            c.Title = root.GetProperty("title").GetString()!;
            App.Chats.Save();
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
            await foreach (var p in App.Ollama.PullAsync(name, _pullCts.Token))
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
        return true;
    }

    private bool HandleSaveConfig(JsonElement root)
    {
        var cfg = root.GetProperty("config").Deserialize<AppConfig>(_json) ?? new AppConfig();
        App.Config.Update(cfg);
        App.WebSearch.Provider = cfg.WebSearchProvider;
        App.WebSearch.ApiKey = cfg.WebSearchApiKey;
        return true;
    }

    private string HandleExportChat(JsonElement root)
    {
        var c = App.Chats.Get(root.GetProperty("id").GetString()!);
        return c == null ? "" : JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true });
    }

    private bool HandleInstallUpdate(JsonElement root)
    {
        var path = root.GetProperty("path").GetString()!;
        _updater.InstallUpdate(path);
        return true;
    }

    private string HandleBrowseFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog();
        dlg.Description = "Select model storage location";
        var result = dlg.ShowDialog();
        return result == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : "";
    }

    // ---- The core chat + web-search flow ----
    private async Task<bool> HandleSendMessage(JsonElement root)
    {
        var chatId = root.GetProperty("chatId").GetString()!;
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? App.Config.Current.DefaultModel : App.Config.Current.DefaultModel;
        var messages = root.GetProperty("messages").Deserialize<List<ChatMessage>>(_json) ?? new();
        var webSearchMode = root.TryGetProperty("webSearchMode", out var wsm) ? wsm.GetString() ?? "auto" : "auto";
        if (root.TryGetProperty("webSearch", out var wsLegacy) && wsLegacy.GetBoolean())
            webSearchMode = "on"; // backward compat
        var cfg = App.Config.Current;

        var chat = App.Chats.Get(chatId) ?? App.Chats.Create(model);
        chat.Model = model;

        // Title from first user message
        if ((chat.Title == "New Chat" || string.IsNullOrEmpty(chat.Title)) && messages.FirstOrDefault(x => x.Role == "user")?.Content is { Length: > 0 } firstUser)
            chat.Title = firstUser.Length > 48 ? firstUser.Substring(0, 48) + "…" : firstUser;

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

            // ---- Web search (replicates Ollama's built-in web search UX) ----
            var shouldSearch = cfg.WebSearchEnabled && (webSearchMode == "on" || (webSearchMode == "auto" && NeedsWebSearch(lastUser)));
            if (shouldSearch)
            {
                PostToWeb(new { @event = "searching", chatId, query = lastUser });
                var search = await App.WebSearch.SearchAsync(lastUser, cfg.MaxSearchResults, ct);
                sources = search.Results;

                // Show source cards in the UI as they arrive (like Ollama).
                PostToWeb(new { @event = "searchResults", chatId, query = search.Query, results = search.Results });

                // Inject the search results as a tool/system message so the model
                // can ground its answer and cite the sources inline.
                var ctx = BuildSearchContext(search);
                sendMessages.Add(new ChatMessage
                {
                    Role = "system",
                    Content = ctx,
                });
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
            await foreach (var chunk in App.Ollama.ChatStreamAsync(req, ct))
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
            App.Chats.Touch(chatId);
            App.Chats.Save();

            PostToWeb(new
            {
                @event = "chatDone",
                chatId,
                sources,
                evalCount,
                totalMs = sw.ElapsedMilliseconds,
                cancelled = ct.IsCancellationRequested,
            });
        }
        catch (OperationCanceledException)
        {
            PostToWeb(new { @event = "chatDone", chatId, cancelled = true });
        }
        catch (OllamaException ex)
        {
            chat.Messages.Add(new ChatStoredMessage { Role = "assistant", Content = "", Error = ex.Message });
            App.Chats.Save();
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
        var sb = new System.Text.StringBuilder();
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
        if (string.IsNullOrWhiteSpace(query)) return false;
        var q = query.ToLowerInvariant().Trim();

        // Short / trivial queries rarely need search
        if (q.Length < 20) return false;

        // Common greetings / social phrases — skip only if short and not a real question
        var greetings = new[] {
            "hello", "hi", "hey", "how are you", "what's up", "good morning",
            "good evening", "good night", "thank you", "thanks", "nice to meet"
        };
        if (greetings.Any(g => q.StartsWith(g)) && q.Length < 35 && !q.Contains("?")) return false;

        // Strong signals that current / external info is required
        var signals = new[] {
            "today", "now", "current", "latest", "recent", "news", "weather", "forecast",
            "stock", "price", "score", "update", " happening", "this week", "this month",
            "2024", "2025", "2026", "election", "market", "release date", "upcoming",
            "live", "breaking", "trending", "who won", "what happened", "did ", "will ",
            "nba", "nfl", "game", "match", "vs", "bitcoin", "crypto"
        };
        if (signals.Any(s => q.Contains(s))) return true;

        // Default: rely on model knowledge
        return false;
    }

    private void ReplyOk(string rpcId, object? data)
    {
        if (string.IsNullOrEmpty(rpcId)) return;
        PostToWeb(new { _rpcId = rpcId, ok = true, data });
    }

    private void ReplyError(string rpcId, string message)
    {
        if (string.IsNullOrEmpty(rpcId)) return;
        PostToWeb(new { _rpcId = rpcId, ok = false, error = message });
    }
}
