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
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = null };
    private CancellationTokenSource? _chatCts;
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

    public async void SendInitialState()
    {
        var cfg = App.Config.Current;
        var reachable = await App.Ollama.IsReachableAsync();
        PostToWeb(new
        {
            @event = "state",
            config = cfg,
            chats = App.Chats.Chats,
            serverReachable = reachable,
        });

        // If a background startup update check found a new version, notify the UI
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
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        _ = DispatchAsync(action, id, root);
    }

    private async Task DispatchAsync(string action, string id, JsonElement root)
    {
        try
        {
            object? data = action switch
            {
                "getInitialState" => await HandleInitialState(),
                "listModels" => await App.Ollama.ListModelsAsync(),
                "showModel" => await App.Ollama.ShowModelAsync(root.GetProperty("name").GetString()!),
                "newChat" => HandleNewChat(root),
                "loadChat" => App.Chats.Get(root.GetProperty("id").GetString()!),
                "deleteChat" => HandleDeleteChat(root),
                "renameChat" => HandleRenameChat(root),
                "sendMessage" => await HandleSendMessage(root),
                "stopGeneration" => HandleStop(),
                "pullModel" => await HandlePull(root),
                "stopPull" => HandleStopPull(),
                "deleteModel" => await App.Ollama.DeleteModelAsync(root.GetProperty("name").GetString()!),
                "saveConfig" => HandleSaveConfig(root),
                "testServer" => await App.Ollama.IsReachableAsync(),
                "fetchPage" => await App.WebSearch.FetchPageAsync(
                    root.GetProperty("url").GetString()!, 6000, default),
                "exportChat" => HandleExportChat(root),
                "checkForUpdates" => await _updater.CheckForUpdateAsync(),
                "downloadUpdate" => await _updater.DownloadUpdateAsync(
                    root.Deserialize<ReleaseInfo>(_json)!),
                "installUpdate" => HandleInstallUpdate(root),
                "browseFolder" => HandleBrowseFolder(),
                _ => null,
            };
            ReplyOk(id, data);
        }
        catch (Exception ex)
        {
            ReplyError(id, ex.Message);
        }
    }

    private async Task<object> HandleInitialState()
    {
        var cfg = App.Config.Current;
        var reachable = await App.Ollama.IsReachableAsync();
        return new
        {
            config = cfg,
            chats = App.Chats.Chats,
            serverReachable = reachable,
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

    private bool HandleStop()
    {
        _chatCts?.Cancel();
        return true;
    }

    private bool HandleStopPull()
    {
        _pullCts?.Cancel();
        return true;
    }

    private async Task<bool> HandlePull(JsonElement root)
    {
        var name = root.GetProperty("name").GetString()!;
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
        var webSearch = root.TryGetProperty("webSearch", out var ws) && ws.GetBoolean();
        var cfg = App.Config.Current;

        var chat = App.Chats.Get(chatId) ?? App.Chats.Create(model);
        chat.Model = model;

        // Title from first user message
        if ((chat.Title == "New Chat" || string.IsNullOrEmpty(chat.Title)) && messages.FirstOrDefault(x => x.Role == "user")?.Content is { Length: > 0 } firstUser)
            chat.Title = firstUser.Length > 48 ? firstUser.Substring(0, 48) + "…" : firstUser;

        _chatCts?.Cancel();
        _chatCts = new CancellationTokenSource();
        var ct = _chatCts.Token;

        var sw = Stopwatch.StartNew();
        List<SearchSource>? sources = null;

        try
        {
            // Build the message list we'll send to the model.
            var sendMessages = new List<ChatMessage>(messages);

            // Optional system prompt from settings.
            if (!string.IsNullOrWhiteSpace(cfg.SystemPrompt))
                sendMessages.Insert(0, new ChatMessage { Role = "system", Content = cfg.SystemPrompt });

            // ---- Web search (replicates Ollama's built-in web search UX) ----
            if (webSearch && cfg.WebSearchEnabled)
            {
                var lastUser = messages.LastOrDefault(x => x.Role == "user")?.Content ?? "";
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
                    { "temperature", cfg.Temperature },
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
            var stored = new ChatStoredMessage
            {
                Role = "assistant",
                Content = finalText,
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
        return true;
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

    private void ReplyOk(string id, object? data)
    {
        if (string.IsNullOrEmpty(id)) return;
        PostToWeb(new { id, ok = true, data });
    }

    private void ReplyError(string id, string message)
    {
        if (string.IsNullOrEmpty(id)) return;
        PostToWeb(new { id, ok = false, error = message });
    }
}
