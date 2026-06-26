using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ollama2.Services;

/// <summary>
/// Replicates the built-in web search behavior of Ollama's chat:
///   1. The model decides it needs fresh info and emits a web_search tool call.
///   2. The app runs the search and returns concise results + sources.
///   3. The model summarizes the findings and cites the sources inline.
///
/// Default backend is DuckDuckGo HTML (no API key required). The result
/// provider is pluggable: set <see cref="Provider"/> to "brave" or "tavily"
/// and supply an API key to use a richer backend.
/// </summary>
public class WebSearchService
{
    public string Provider { get; set; } = "duckduckgo";
    public string? ApiKey { get; set; }

    private static readonly HttpClient Http = new HttpClient();
    static WebSearchService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    }

    public async Task<WebSearchResult> SearchAsync(string query, int maxResults, CancellationToken ct = default)
    {
        return Provider switch
        {
            "duckduckgo" => await DuckDuckGoAsync(query, maxResults, ct),
            "brave" => await BraveAsync(query, maxResults, ct),
            "tavily" => await TavilyAsync(query, maxResults, ct),
            _ => await DuckDuckGoAsync(query, maxResults, ct),
        };
    }

    /// <summary>Fetch a URL and return a cleaned, length-limited text excerpt.</summary>
    public async Task<string> FetchPageAsync(string url, int maxChars, CancellationToken ct = default)
    {
        try
        {
            using var r = await Http.GetAsync(url, ct);
            if (!r.IsSuccessStatusCode) return "";
            var html = await r.Content.ReadAsStringAsync(ct);
            return CleanHtml(html, maxChars);
        }
        catch { return ""; }
    }

    // ---- DuckDuckGo HTML (no key) ----
    private async Task<WebSearchResult> DuckDuckGoAsync(string query, int max, CancellationToken ct)
    {
        var result = new WebSearchResult { Query = query };
        try
        {
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            using var r = await Http.GetAsync(url, ct);
            var html = await r.Content.ReadAsStringAsync(ct);

            // Each result is a <a class="result__a" href="...">title</a> with a
            // <a class="result__snippet">snippet</a> sibling block.
            var linkRx = new Regex(
                @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
                RegexOptions.Singleline);
            var snipRx = new Regex(
                @"<a[^>]*class=""result__snippet""[^>]*>(.*?)</a>",
                RegexOptions.Singleline);

            var links = linkRx.Matches(html);
            var snips = snipRx.Matches(html);
            for (int i = 0; i < links.Count && result.Results.Count < max; i++)
            {
                var href = links[i].Groups[1].Value;
                var title = StripTags(links[i].Groups[2].Value);
                var snippet = i < snips.Count ? StripTags(snips[i].Groups[1].Value) : "";
                href = DecodeDdgRedirect(href);
                if (string.IsNullOrEmpty(title)) continue;
                result.Results.Add(new SearchSource
                {
                    Title = System.Net.WebUtility.HtmlDecode(title).Trim(),
                    Url = System.Net.WebUtility.HtmlDecode(href).Trim(),
                    Snippet = System.Net.WebUtility.HtmlDecode(snippet).Trim(),
                });
            }
        }
        catch { /* swallow: return what we have */ }
        return result;
    }

    private static string DecodeDdgRedirect(string href)
    {
        // DuckDuckGo wraps links as //duckduckgo.com/l/?uddg=<encoded url>&rut=...
        var m = Regex.Match(href, @"uddg=([^&]+)");
        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        if (href.StartsWith("//")) return "https:" + href;
        return href;
    }

    // ---- Brave Search API (key required) ----
    private async Task<WebSearchResult> BraveAsync(string query, int max, CancellationToken ct)
    {
        var result = new WebSearchResult { Query = query };
        if (string.IsNullOrEmpty(ApiKey)) return result;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={max}");
            req.Headers.Add("X-Subscription-Token", ApiKey);
            req.Headers.Add("Accept", "application/json");
            using var r = await Http.SendAsync(req, ct);
            var json = await r.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("web", out var web) &&
                web.TryGetProperty("results", out var results))
            {
                foreach (var item in results.EnumerateArray())
                {
                    result.Results.Add(new SearchSource
                    {
                        Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                        Snippet = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    });
                    if (result.Results.Count >= max) break;
                }
            }
        }
        catch { }
        return result;
    }

    // ---- Tavily API (key required) ----
    private async Task<WebSearchResult> TavilyAsync(string query, int max, CancellationToken ct)
    {
        var result = new WebSearchResult { Query = query };
        if (string.IsNullOrEmpty(ApiKey)) return result;
        try
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                api_key = ApiKey,
                query,
                max_results = max,
                include_answer = true,
            });
            using var c = new StringContent(body, Encoding.UTF8, "application/json");
            using var r = await Http.PostAsync("https://api.tavily.com/search", c, ct);
            var json = await r.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results))
            {
                foreach (var item in results.EnumerateArray())
                {
                    result.Results.Add(new SearchSource
                    {
                        Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                        Snippet = item.TryGetProperty("content", out var d) ? d.GetString() ?? "" : "",
                    });
                    if (result.Results.Count >= max) break;
                }
            }
        }
        catch { }
        return result;
    }

    private static string StripTags(string s)
        => Regex.Replace(s, "<[^>]+>", " ");

    private static string CleanHtml(string html, int maxChars)
    {
        // Drop scripts/styles, then tags, collapse whitespace.
        html = Regex.Replace(html, @"(?is)<(script|style)[^>]*>.*?</\1>", " ");
        html = Regex.Replace(html, @"(?is)<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"\s+", " ").Trim();
        if (html.Length > maxChars) html = html.Substring(0, maxChars) + "…";
        return html;
    }
}
