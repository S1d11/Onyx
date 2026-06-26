using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ollama2.Orchestrator;

/// <summary>
/// GitHub MCP connector. Provides the LLM with access to GitHub repositories,
/// issues, pull requests, and code via the GitHub REST API.
/// </summary>
public class GitHubConnector : ITool
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Func<string?> _getToken;

    public string Name => "github";

    public GitHubConnector(Func<string?> getToken)
    {
        _getToken = getToken;
    }

    public async Task<ToolResult> ExecuteAsync(string input, Intent intent, CancellationToken ct = default)
    {
        var token = _getToken();
        if (string.IsNullOrEmpty(token))
            return new ToolResult { ToolName = Name, Success = false, Output = "GitHub is not connected. Please add a personal access token in Connections → GitHub." };

        try
        {
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Http.DefaultRequestHeaders.UserAgent.Clear();
            Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Onyx", "1.0"));

            // Parse the user's request to determine what GitHub action to perform
            var action = DetermineGitHubAction(input, intent);

            switch (action.Type)
            {
                case "search_repos":
                    return await SearchReposAsync(action.Query, ct);
                case "get_repo":
                    return await GetRepoAsync(action.Owner!, action.Repo!, ct);
                case "list_issues":
                    return await ListIssuesAsync(action.Owner!, action.Repo!, ct);
                case "get_issue":
                    return await GetIssueAsync(action.Owner!, action.Repo!, action.Number, ct);
                case "create_issue":
                    return await CreateIssueAsync(action.Owner!, action.Repo!, action.Title!, action.Body, ct);
                case "list_prs":
                    return await ListPullRequestsAsync(action.Owner!, action.Repo!, ct);
                case "get_file":
                    return await GetFileAsync(action.Owner!, action.Repo!, action.Path!, action.Ref, ct);
                case "search_code":
                    return await SearchCodeAsync(action.Query, ct);
                case "get_user":
                    return await GetUserAsync(action.Query, ct);
                case "list_commits":
                    return await ListCommitsAsync(action.Owner!, action.Repo!, action.Ref, ct);
                default:
                    return new ToolResult { ToolName = Name, Success = false, Output = "Could not determine what GitHub action to perform. Try: 'search repos for X', 'list issues in owner/repo', 'get file owner/repo/path', etc." };
            }
        }
        catch (Exception ex)
        {
            return new ToolResult { ToolName = Name, Success = false, Output = $"GitHub API error: {ex.Message}" };
        }
    }

    private GitHubAction DetermineGitHubAction(string input, Intent intent)
    {
        var lower = input.ToLowerInvariant();
        var action = new GitHubAction();

        // Extract owner/repo patterns like "owner/repo" or "owner/repo/path"
        var parts = input.Split(new[] { ' ', '/', '\n', '\r', '\t', ':', '"', '\'', '(', ')', '[', ']', ',', ';', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var p in parts)
        {
            if (p.Contains('/'))
            {
                var sub = p.Split('/', 3);
                if (sub.Length >= 2)
                {
                    action.Owner = sub[0];
                    action.Repo = sub[1];
                    if (sub.Length >= 3) action.Path = sub[2];
                }
            }
        }

        if (lower.Contains("search repo") || lower.Contains("find repo") || lower.Contains("search for repo"))
        {
            action.Type = "search_repos";
            action.Query = input;
        }
        else if (lower.Contains("issue") && (lower.Contains("create") || lower.Contains("make") || lower.Contains("open")))
        {
            action.Type = "create_issue";
            action.Title = input.Replace("create issue", "").Replace("make issue", "").Replace("open issue", "").Trim();
            if (string.IsNullOrEmpty(action.Title)) action.Title = "New issue";
        }
        else if (lower.Contains("issue"))
        {
            if (int.TryParse(parts.FirstOrDefault(x => int.TryParse(x, out _)) ?? "", out var num))
            {
                action.Type = "get_issue";
                action.Number = num;
            }
            else
            {
                action.Type = "list_issues";
            }
        }
        else if (lower.Contains("pull request") || lower.Contains("pr ") || lower.Contains("prs"))
        {
            action.Type = "list_prs";
        }
        else if (lower.Contains("file") || lower.Contains("readme") || lower.Contains("code") || lower.Contains("content"))
        {
            action.Type = "get_file";
            if (string.IsNullOrEmpty(action.Path)) action.Path = "README.md";
        }
        else if (lower.Contains("search code") || lower.Contains("find code"))
        {
            action.Type = "search_code";
            action.Query = input;
        }
        else if (lower.Contains("commit"))
        {
            action.Type = "list_commits";
        }
        else if (lower.Contains("user") || lower.Contains("profile"))
        {
            action.Type = "get_user";
            action.Query = action.Owner ?? input;
        }
        else if (!string.IsNullOrEmpty(action.Owner) && !string.IsNullOrEmpty(action.Repo))
        {
            action.Type = "get_repo";
        }
        else
        {
            action.Type = "search_repos";
            action.Query = input;
        }

        return action;
    }

    private async Task<ToolResult> SearchReposAsync(string query, CancellationToken ct)
    {
        var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}&per_page=10";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        sb.AppendLine("GitHub Repository Search Results:");
        sb.AppendLine();
        var items = doc.RootElement.GetProperty("items");
        foreach (var item in items.EnumerateArray())
        {
            sb.AppendLine($"  {item.GetProperty("full_name").GetString()}");
            sb.AppendLine($"    ⭐ {item.GetProperty("stargazers_count").GetInt32():N0} stars | {item.GetProperty("language").GetString() ?? "?"} | {item.GetProperty("description").GetString() ?? "No description"}");
            sb.AppendLine($"    {item.GetProperty("html_url").GetString()}");
            sb.AppendLine();
        }
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> GetRepoAsync(string owner, string repo, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var sb = new StringBuilder();
        sb.AppendLine($"Repository: {r.GetProperty("full_name").GetString()}");
        sb.AppendLine($"Description: {r.GetProperty("description").GetString() ?? "N/A"}");
        sb.AppendLine($"Stars: {r.GetProperty("stargazers_count").GetInt32():N0} | Forks: {r.GetProperty("forks_count").GetInt32():N0} | Open Issues: {r.GetProperty("open_issues_count").GetInt32():N0}");
        sb.AppendLine($"Language: {r.GetProperty("language").GetString() ?? "N/A"} | License: {(r.GetProperty("license").ValueKind == JsonValueKind.Null ? "N/A" : r.GetProperty("license").GetProperty("spdx_id").GetString())}");
        sb.AppendLine($"Default Branch: {r.GetProperty("default_branch").GetString()}");
        sb.AppendLine($"URL: {r.GetProperty("html_url").GetString()}");
        sb.AppendLine($"Updated: {r.GetProperty("updated_at").GetString()}");
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> ListIssuesAsync(string owner, string repo, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues?state=open&per_page=10";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        sb.AppendLine($"Open Issues in {owner}/{repo}:");
        sb.AppendLine();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("pull_request", out _)) continue; // skip PRs
            sb.AppendLine($"  #{item.GetProperty("number").GetInt32()}: {item.GetProperty("title").GetString()}");
            sb.AppendLine($"    Labels: {string.Join(", ", item.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()))}");
            sb.AppendLine($"    {item.GetProperty("html_url").GetString()}");
            sb.AppendLine();
        }
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> GetIssueAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/issues/{number}";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var i = doc.RootElement;
        var sb = new StringBuilder();
        sb.AppendLine($"Issue #{i.GetProperty("number").GetInt32()}: {i.GetProperty("title").GetString()}");
        sb.AppendLine($"State: {i.GetProperty("state").GetString()} | Author: {i.GetProperty("user").GetProperty("login").GetString()} | Comments: {i.GetProperty("comments").GetInt32()}");
        sb.AppendLine($"Labels: {string.Join(", ", i.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()))}");
        sb.AppendLine($"URL: {i.GetProperty("html_url").GetString()}");
        sb.AppendLine();
        sb.AppendLine(i.GetProperty("body").GetString() ?? "No description");
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> CreateIssueAsync(string owner, string repo, string title, string? body, CancellationToken ct)
    {
        var payload = new { title, body };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync($"https://api.github.com/repos/{owner}/{repo}/issues", content, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new ToolResult { ToolName = Name, Success = false, Output = $"GitHub API error: {resp.StatusCode}\n{json}" };
        var doc = JsonDocument.Parse(json);
        return new ToolResult
        {
            ToolName = Name,
            Success = true,
            Output = $"Created issue #{doc.RootElement.GetProperty("number").GetInt32()}: {doc.RootElement.GetProperty("title").GetString()}\n{doc.RootElement.GetProperty("html_url").GetString()}"
        };
    }

    private async Task<ToolResult> ListPullRequestsAsync(string owner, string repo, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/pulls?state=open&per_page=10";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        sb.AppendLine($"Open Pull Requests in {owner}/{repo}:");
        sb.AppendLine();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            sb.AppendLine($"  #{item.GetProperty("number").GetInt32()}: {item.GetProperty("title").GetString()}");
            sb.AppendLine($"    By: {item.GetProperty("user").GetProperty("login").GetString()} | Branch: {item.GetProperty("head").GetProperty("ref").GetString()} → {item.GetProperty("base").GetProperty("ref").GetString()}");
            sb.AppendLine($"    {item.GetProperty("html_url").GetString()}");
            sb.AppendLine();
        }
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> GetFileAsync(string owner, string repo, string path, string? @ref, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
        if (!string.IsNullOrEmpty(@ref)) url += $"?ref={@ref}";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            // Directory listing
            var sb = new StringBuilder();
            sb.AppendLine($"Contents of {owner}/{repo}/{path}:");
            sb.AppendLine();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.GetProperty("type").GetString();
                sb.AppendLine($"  [{type?.ToUpperInvariant()}] {item.GetProperty("name").GetString()} ({item.GetProperty("size").GetInt64()} bytes)");
            }
            return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
        }
        else
        {
            var content = doc.RootElement.GetProperty("content").GetString() ?? "";
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "")));
            return new ToolResult { ToolName = Name, Success = true, Output = $"File: {owner}/{repo}/{path}\n```\n{decoded}\n```" };
        }
    }

    private async Task<ToolResult> SearchCodeAsync(string query, CancellationToken ct)
    {
        var url = $"https://api.github.com/search/code?q={Uri.EscapeDataString(query)}&per_page=10";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        sb.AppendLine("GitHub Code Search Results:");
        sb.AppendLine();
        var items = doc.RootElement.GetProperty("items");
        foreach (var item in items.EnumerateArray())
        {
            sb.AppendLine($"  {item.GetProperty("repository").GetProperty("full_name").GetString()}/{item.GetProperty("path").GetString()}");
            sb.AppendLine($"    {item.GetProperty("html_url").GetString()}");
            sb.AppendLine();
        }
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> GetUserAsync(string username, CancellationToken ct)
    {
        var url = $"https://api.github.com/users/{Uri.EscapeDataString(username)}";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var u = doc.RootElement;
        var sb = new StringBuilder();
        sb.AppendLine($"User: {u.GetProperty("login").GetString()}");
        sb.AppendLine($"Name: {u.GetProperty("name").GetString() ?? "N/A"} | Bio: {u.GetProperty("bio").GetString() ?? "N/A"}");
        sb.AppendLine($"Public Repos: {u.GetProperty("public_repos").GetInt32()} | Followers: {u.GetProperty("followers").GetInt32()} | Following: {u.GetProperty("following").GetInt32()}");
        sb.AppendLine($"URL: {u.GetProperty("html_url").GetString()}");
        if (u.TryGetProperty("company", out var company) && company.ValueKind != JsonValueKind.Null)
            sb.AppendLine($"Company: {company.GetString()}");
        if (u.TryGetProperty("location", out var loc) && loc.ValueKind != JsonValueKind.Null)
            sb.AppendLine($"Location: {loc.GetString()}");
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> ListCommitsAsync(string owner, string repo, string? @ref, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/commits?per_page=10";
        if (!string.IsNullOrEmpty(@ref)) url += $"&sha={@ref}";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        sb.AppendLine($"Recent Commits in {owner}/{repo}:");
        sb.AppendLine();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var commit = item.GetProperty("commit");
            var msg = commit.GetProperty("message").GetString()?.Split('\n')[0] ?? "";
            sb.AppendLine($"  {item.GetProperty("sha").GetString()?[..7]}: {msg}");
            sb.AppendLine($"    By: {commit.GetProperty("author").GetProperty("name").GetString()} | {commit.GetProperty("committer").GetProperty("date").GetString()}");
            sb.AppendLine();
        }
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    public static ToolDefinition Definition => new()
    {
        Name = "github",
        Description = "GitHub connector. Search repositories, read issues/PRs, get file contents, list commits, create issues, and search code across all of GitHub. Requires a GitHub personal access token.",
        Category = "integration",
        Triggers = new() { "github", "repo", "repository", "issue", "pr", "pull request", "commit", "code search", "readme", "file content" },
        RequiresConfirmation = false,
        Enabled = true,
    };
}

class GitHubAction
{
    public string Type { get; set; } = "";
    public string? Owner { get; set; }
    public string? Repo { get; set; }
    public string? Path { get; set; }
    public string? Query { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? Ref { get; set; }
    public int Number { get; set; }
}
