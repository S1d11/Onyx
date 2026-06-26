using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// Gmail MCP connector. Provides the LLM with access to the user's Gmail:
/// list, read, search, and send emails via the Gmail REST API.
/// </summary>
public class GmailConnector : ITool
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly GoogleOAuthService _oauth;

    public string Name => "gmail";

    public GmailConnector(GoogleOAuthService oauth)
    {
        _oauth = oauth;
    }

    public async Task<ToolResult> ExecuteAsync(string input, Intent intent, CancellationToken ct = default)
    {
        if (!_oauth.IsConnected)
            return new ToolResult { ToolName = Name, Success = false, Output = "Gmail is not connected. Please connect your Google account in Connections → Gmail." };

        var token = await _oauth.GetValidAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            return new ToolResult { ToolName = Name, Success = false, Output = "Failed to get Google access token. Please reconnect your Google account." };

        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var action = DetermineAction(input);
            return action.Type switch
            {
                "list" => await ListEmailsAsync(action.Query, action.Max, ct),
                "search" => await SearchEmailsAsync(action.Query, action.Max, ct),
                "read" => await ReadEmailAsync(action.Id, ct),
                "send" => await SendEmailAsync(action.To, action.Subject, action.Body, ct),
                "unread" => await ListUnreadAsync(action.Max, ct),
                _ => new ToolResult { ToolName = Name, Success = false, Output = "Could not determine what Gmail action to perform. Try: 'list recent emails', 'search for emails about X', 'read email with subject X', 'send email to X'." },
            };
        }
        catch (Exception ex)
        {
            return new ToolResult { ToolName = Name, Success = false, Output = $"Gmail API error: {ex.Message}" };
        }
    }

    private GmailAction DetermineAction(string input)
    {
        var lower = input.ToLowerInvariant();
        var action = new GmailAction { Max = 10 };

        if (lower.Contains("send") || lower.Contains("compose") || lower.Contains("write email") || lower.Contains("reply"))
        {
            action.Type = "send";
            // Try to extract recipient
            var emailMatch = System.Text.RegularExpressions.Regex.Match(input, @"[\w.+-]+@[\w-]+\.[\w.-]+");
            if (emailMatch.Success) action.To = emailMatch.Value;
            // Extract subject after "subject" or "about"
            if (lower.Contains("subject"))
            {
                var idx = lower.IndexOf("subject");
                action.Subject = input[(idx + 8)..].Split('\n')[0].Trim();
            }
            else if (lower.Contains("about"))
            {
                var idx = lower.IndexOf("about");
                action.Subject = input[(idx + 6)..].Split('\n')[0].Trim();
            }
            // Body is everything after "saying" or "with"
            if (lower.Contains("saying"))
            {
                var idx = lower.IndexOf("saying");
                action.Body = input[(idx + 7)..].Trim();
            }
            else if (lower.Contains("with body"))
            {
                var idx = lower.IndexOf("with body");
                action.Body = input[(idx + 9)..].Trim();
            }
        }
        else if (lower.Contains("unread"))
        {
            action.Type = "unread";
        }
        else if (lower.Contains("search") || lower.Contains("find email"))
        {
            action.Type = "search";
            action.Query = ExtractSearchQuery(input);
        }
        else if (lower.Contains("read") || lower.Contains("show email") || lower.Contains("open email"))
        {
            action.Type = "read";
            action.Id = ExtractSearchQuery(input);
        }
        else
        {
            action.Type = "list";
            action.Query = "in:inbox";
        }

        return action;
    }

    private static string ExtractSearchQuery(string input)
    {
        var lower = input.ToLowerInvariant();
        foreach (var keyword in new[] { "search", "find", "about", "for", "with subject", "from", "to" })
        {
            var idx = lower.IndexOf(keyword);
            if (idx >= 0)
                return input[(idx + keyword.Length)..].Trim();
        }
        return input;
    }

    private async Task<ToolResult> ListEmailsAsync(string query, int max, CancellationToken ct)
    {
        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults={max}&q={Uri.EscapeDataString(query ?? "in:inbox")}";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.GetArrayLength() == 0)
            return new ToolResult { ToolName = Name, Success = true, Output = "No emails found." };

        var sb = new StringBuilder();
        sb.AppendLine($"Recent emails ({messages.GetArrayLength()}):");
        sb.AppendLine();

        foreach (var msg in messages.EnumerateArray())
        {
            var id = msg.GetProperty("id").GetString();
            var detail = await GetEmailHeaderAsync(id!, ct);
            sb.AppendLine(detail);
        }

        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> SearchEmailsAsync(string query, int max, CancellationToken ct)
    {
        return await ListEmailsAsync(query, max, ct);
    }

    private async Task<ToolResult> ListUnreadAsync(int max, CancellationToken ct)
    {
        return await ListEmailsAsync("is:unread", max, ct);
    }

    private async Task<string> GetEmailHeaderAsync(string id, CancellationToken ct)
    {
        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{id}?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var headers = doc.RootElement.GetProperty("payload").GetProperty("headers");
        string from = "", subject = "", date = "";
        foreach (var h in headers.EnumerateArray())
        {
            var name = h.GetProperty("name").GetString()?.ToLowerInvariant() ?? "";
            var value = h.GetProperty("value").GetString() ?? "";
            if (name == "from") from = value;
            else if (name == "subject") subject = value;
            else if (name == "date") date = value;
        }
        return $"  📧 {subject}\n    From: {from}\n    Date: {date}\n    ID: {id}\n";
    }

    private async Task<ToolResult> ReadEmailAsync(string id, CancellationToken ct)
    {
        // If the "id" is actually a search term, search for it first
        if (!string.IsNullOrEmpty(id) && id.Length < 50)
        {
            var searchUrl = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults=1&q={Uri.EscapeDataString(id)}";
            var searchJson = await Http.GetStringAsync(searchUrl, ct);
            var searchDoc = JsonDocument.Parse(searchJson);
            if (searchDoc.RootElement.TryGetProperty("messages", out var msgs) && msgs.GetArrayLength() > 0)
                id = msgs[0].GetProperty("id").GetString()!;
            else
                return new ToolResult { ToolName = Name, Success = false, Output = "No email found matching that query." };
        }

        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{id}?format=full";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var headers = doc.RootElement.GetProperty("payload").GetProperty("headers");
        string from = "", subject = "", date = "", to = "";
        foreach (var h in headers.EnumerateArray())
        {
            var name = h.GetProperty("name").GetString()?.ToLowerInvariant() ?? "";
            var value = h.GetProperty("value").GetString() ?? "";
            if (name == "from") from = value;
            else if (name == "subject") subject = value;
            else if (name == "date") date = value;
            else if (name == "to") to = value;
        }

        var body = ExtractBody(doc.RootElement.GetProperty("payload"));

        var sb = new StringBuilder();
        sb.AppendLine($"Subject: {subject}");
        sb.AppendLine($"From: {from}");
        sb.AppendLine($"To: {to}");
        sb.AppendLine($"Date: {date}");
        sb.AppendLine();
        sb.AppendLine(body);

        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private static string ExtractBody(JsonElement payload)
    {
        // Try to find the text/plain part
        if (payload.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("mimeType", out var mime) && mime.GetString() == "text/plain")
                {
                    var data = part.GetProperty("body").TryGetProperty("data", out var d) ? d.GetString() : null;
                    if (data != null) return DecodeBase64Url(data);
                }
            }
            // Fallback to first part
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("body", out var body) && body.TryGetProperty("data", out var d))
                {
                    var data = d.GetString();
                    if (data != null) return DecodeBase64Url(data);
                }
            }
        }
        else if (payload.TryGetProperty("body", out var body) && body.TryGetProperty("data", out var d))
        {
            var data = d.GetString();
            if (data != null) return DecodeBase64Url(data);
        }
        return "(no readable body)";
    }

    private static string DecodeBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        var bytes = Convert.FromBase64String(s);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<ToolResult> SendEmailAsync(string? to, string? subject, string? body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(to))
            return new ToolResult { ToolName = Name, Success = false, Output = "No recipient specified. Please provide an email address." };

        subject ??= "(No subject)";
        body ??= "";

        var rawEmail = $"To: {to}\r\nSubject: {subject}\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n{body}";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawEmail)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var payload = JsonSerializer.Serialize(new { raw = base64 });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync("https://gmail.googleapis.com/gmail/v1/users/me/messages/send", content, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return new ToolResult { ToolName = Name, Success = false, Output = $"Gmail API error: {resp.StatusCode}\n{json}" };

        var doc = JsonDocument.Parse(json);
        return new ToolResult { ToolName = Name, Success = true, Output = $"Email sent to {to} (ID: {doc.RootElement.GetProperty("id").GetString()})" };
    }

    public static ToolDefinition Definition => new()
    {
        Name = "gmail",
        Description = "Gmail connector. List, search, read, and send emails. Requires Google account connection.",
        Category = "integration",
        Triggers = new() { "email", "gmail", "inbox", "send email", "read email", "mail", "message" },
        RequiresConfirmation = false,
        Enabled = true,
    };
}

class GmailAction
{
    public string Type { get; set; } = "";
    public string? Query { get; set; }
    public string? Id { get; set; }
    public string? To { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public int Max { get; set; } = 10;
}
