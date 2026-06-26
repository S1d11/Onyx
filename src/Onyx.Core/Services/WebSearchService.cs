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
/// Web search service with multiple backends and fallbacks.
/// Default: DuckDuckGo (no API key). Supports Brave and Tavily with keys.
/// Also fetches and reads page content for direct answers.
/// </summary>
public class WebSearchService
{
    public string Provider { get; set; } = "duckduckgo";
    public string? ApiKey { get; set; }

    private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    });
    static WebSearchService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        Http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        Http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<WebSearchResult> SearchAsync(string query, int maxResults, CancellationToken ct = default)
    {
        // Try the configured provider first
        var result = Provider switch
        {
            "brave" => await BraveAsync(query, maxResults, ct),
            "tavily" => await TavilyAsync(query, maxResults, ct),
            _ => await DuckDuckGoAsync(query, maxResults, ct),
        };

        // If the primary provider returned no results, try fallbacks
        if (result.Results.Count == 0)
        {
            // Try DuckDuckGo Lite (different endpoint, less likely to be blocked)
            result = await DuckDuckGoLiteAsync(query, maxResults, ct);
        }

        if (result.Results.Count == 0)
        {
            // Last resort: try Bing
            result = await BingAsync(query, maxResults, ct);
        }

        return result;
    }

    /// <summary>Fetch a URL and return a cleaned, length-limited text excerpt.</summary>
    public async Task<string> FetchPageAsync(string url, int maxChars, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "text/html,application/xhtml+xml");
            using var r = await Http.SendAsync(req, ct);
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
            // Use POST to avoid URL encoding issues and get better results
            var url = "https://html.duckduckgo.com/html/";
            var formData = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = query,
                ["b"] = "1",  // reduced layout
            });
            using var r = await Http.PostAsync(url, formData, ct);
            var html = await r.Content.ReadAsStringAsync(ct);
            ParseDuckDuckGoHtml(html, max, result);
        }
        catch { /* swallow */ }
        return result;
    }

    // ---- DuckDuckGo Lite (fallback, no key) ----
    private async Task<WebSearchResult> DuckDuckGoLiteAsync(string query, int max, CancellationToken ct)
    {
        var result = new WebSearchResult { Query = query };
        try
        {
            var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
            using var r = await Http.GetAsync(url, ct);
            var html = await r.Content.ReadAsStringAsync(ct);

            // Lite version uses different HTML structure — results are in <a class="result-link">
            var linkRx = new Regex(
                @"<a[^>]*class=""result-link""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
                RegexOptions.Singleline);
            var links = linkRx.Matches(html);

            // Snippets in lite are in <td class="result-snippet">
            var snipRx = new Regex(
                @"<td[^>]*class=""result-snippet""[^>]*>(.*?)</td>",
                RegexOptions.Singleline);
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
        catch { }
        return result;
    }

    private void ParseDuckDuckGoHtml(string html, int max, WebSearchResult result)
    {
        // Try multiple patterns — DDG changes their HTML sometimes
        var patterns = new[]
        {
            // Standard: <a class="result__a" href="...">
            new Regex(@"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline),
            // Alternative: <a class="result__a" ... href='...'>
            new Regex(@"<a[^>]*class=""result__a""[^>]*href='([^']+)'[^>]*>(.*?)</a>", RegexOptions.Singleline),
            // Fallback: any link with result__a class
            new Regex(@"<a[^>]*result__a[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline),
        };

        var snipRx = new Regex(
            @"<a[^>]*class=""result__snippet""[^>]*>(.*?)</a>",
            RegexOptions.Singleline);

        MatchCollection? links = null;
        foreach (var rx in patterns)
        {
            links = rx.Matches(html);
            if (links.Count > 0) break;
        }
        if (links == null || links.Count == 0) return;

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

    // ---- Bing (fallback, no key) ----
    private async Task<WebSearchResult> BingAsync(string query, int max, CancellationToken ct)
    {
        var result = new WebSearchResult { Query = query };
        try
        {
            var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&count={max}";
            using var r = await Http.GetAsync(url, ct);
            var html = await r.Content.ReadAsStringAsync(ct);

            // Bing results: <li class="b_algo"><h2><a href="...">title</a></h2><p>snippet</p>
            var blockRx = new Regex(
                @"<li[^>]*class=""b_algo""[^>]*>.*?<a[^>]*href=""([^""]+)""[^>]*>(.*?)</a>.*?(?:<p[^>]*>(.*?)</p>|<div[^>]*class=""b_caption""[^>]*>.*?<p[^>]*>(.*?)</p>)",
                RegexOptions.Singleline);
            var blocks = blockRx.Matches(html);
            for (int i = 0; i < blocks.Count && result.Results.Count < max; i++)
            {
                var href = blocks[i].Groups[1].Value;
                var title = StripTags(blocks[i].Groups[2].Value);
                var snippet = StripTags(blocks[i].Groups[3].Success ? blocks[i].Groups[3].Value : blocks[i].Groups[4].Value);
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(href)) continue;
                result.Results.Add(new SearchSource
                {
                    Title = System.Net.WebUtility.HtmlDecode(title).Trim(),
                    Url = System.Net.WebUtility.HtmlDecode(href).Trim(),
                    Snippet = System.Net.WebUtility.HtmlDecode(snippet).Trim(),
                });
            }
        }
        catch { }
        return result;
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

    private static string DecodeDdgRedirect(string href)
    {
        // DuckDuckGo wraps links as //duckduckgo.com/l/?uddg=<encoded url>&rut=...
        var m = Regex.Match(href, @"uddg=([^&]+)");
        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        if (href.StartsWith("//")) return "https:" + href;
        return href;
    }

    private static string StripTags(string s)
        => Regex.Replace(s, "<[^>]+>", " ");

    private static string CleanHtml(string html, int maxChars)
    {
        // Drop scripts/styles/noscript, then tags, collapse whitespace.
        html = Regex.Replace(html, @"(?is)<(script|style|noscript|nav|footer|header|aside)[^>]*>.*?</\1>", " ");
        // Keep content of these tags but remove the tags themselves
        html = Regex.Replace(html, @"(?is)<(article|main|section|div|p|span|h[1-6]|li|td|tr|br)[^>]*>", " ");
        html = Regex.Replace(html, @"(?is)</(article|main|section|div|p|span|h[1-6]|li|td|tr)>", " ");
        // Remove all remaining tags
        html = Regex.Replace(html, @"(?is)<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        // Collapse whitespace
        html = Regex.Replace(html, @"\s+", " ").Trim();
        if (html.Length > maxChars) html = html.Substring(0, maxChars) + "…";
        return html;
    }
}
