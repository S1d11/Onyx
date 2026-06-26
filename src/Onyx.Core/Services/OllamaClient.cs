using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ollama2.Services;

/// <summary>
/// Thin REST client for the local Ollama server (default http://localhost:11434).
/// Mirrors the endpoints the official Ollama desktop app uses:
///   GET  /api/tags            -> list installed models
///   POST /api/show            -> model details
///   POST /api/pull            -> pull a model (streamed progress)
///   DELETE /api/delete        -> remove a model
///   POST /api/chat            -> chat completion (streamed NDJSON)
///   POST /api/generate        -> one-shot generation (streamed NDJSON)
/// </summary>
public class OllamaClient
{
    private readonly Func<string> _getBaseUrl;
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        // The local server may use a self-signed cert in some setups.
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { Timeout = TimeSpan.FromMinutes(30) };

    public OllamaClient(Func<string> getBaseUrl) => _getBaseUrl = getBaseUrl;

    private string Base => _getBaseUrl().TrimEnd('/');

    public async Task<bool> IsReachableAsync()
    {
        try
        {
            using var r = await Http.GetAsync($"{Base}/api/tags");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<ModelInfo>> ListModelsAsync()
    {
        using var r = await Http.GetAsync($"{Base}/api/tags");
        r.EnsureSuccessStatusCode();
        var json = await r.Content.ReadAsStringAsync();
        var tags = JsonSerializer.Deserialize<TagsResponse>(json);
        return tags?.Models ?? new();
    }

    public async Task<JsonElement> ShowModelAsync(string name)
    {
        var body = JsonSerializer.Serialize(new { name });
        using var c = new StringContent(body, Encoding.UTF8, "application/json");
        using var r = await Http.PostAsync($"{Base}/api/show", c);
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    }

    public async IAsyncEnumerable<PullProgress> PullAsync(string name,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { name, stream = true });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/api/pull")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var sr = new StreamReader(stream);
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var node = JsonDocument.Parse(line).RootElement;
            yield return new PullProgress
            {
                Status = node.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                Total = node.TryGetProperty("total", out var t) ? t.GetInt64() : 0,
                Completed = node.TryGetProperty("completed", out var c) ? c.GetInt64() : 0,
                Digest = node.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "",
            };
        }
    }

    public async Task<bool> DeleteModelAsync(string name)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/api/delete")
        { Content = new StringContent(JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json") };
        using var r = await Http.SendAsync(req);
        return r.IsSuccessStatusCode;
    }

    /// <summary>
    /// Streams chat chunks. Each yielded chunk is one NDJSON line from /api/chat.
    /// </summary>
    public async IAsyncEnumerable<ChatChunk> ChatStreamAsync(ChatRequest req,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        req.Stream = true;
        var body = JsonSerializer.Serialize(req);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{Base}/api/chat")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await Http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new OllamaException($"Ollama server returned {(int)resp.StatusCode}: {err}");
        }
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var sr = new StreamReader(stream);
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var chunk = JsonSerializer.Deserialize<ChatChunk>(line);
            if (chunk != null) yield return chunk;
        }
    }

    public async Task<ChatChunk> ChatOnceAsync(ChatRequest req, CancellationToken ct = default)
    {
        req.Stream = false;
        var body = JsonSerializer.Serialize(req);
        using var c = new StringContent(body, Encoding.UTF8, "application/json");
        using var r = await Http.PostAsync($"{Base}/api/chat", c, ct);
        r.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<ChatChunk>(await r.Content.ReadAsStringAsync(ct)) ?? new();
    }
}

public class PullProgress
{
    public string Status { get; set; } = "";
    public long Total { get; set; }
    public long Completed { get; set; }
    public string Digest { get; set; } = "";
    public double Percent => Total > 0 ? Completed * 100.0 / Total : 0;
}

public class OllamaException : Exception
{
    public OllamaException(string msg) : base(msg) { }
}
