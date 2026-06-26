using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Services;

namespace Ollama2.Orchestrator;

/// <summary>
/// Google Drive MCP connector. Provides the LLM with access to the user's
/// Google Drive: list, search, read, upload, and download files.
/// </summary>
public class GoogleDriveConnector : ITool
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly GoogleOAuthService _oauth;

    public string Name => "gdrive";

    public GoogleDriveConnector(GoogleOAuthService oauth)
    {
        _oauth = oauth;
    }

    public async Task<ToolResult> ExecuteAsync(string input, Intent intent, CancellationToken ct = default)
    {
        if (!_oauth.IsConnected)
            return new ToolResult { ToolName = Name, Success = false, Output = "Google Drive is not connected. Please connect your Google account in Connections → Google Drive." };

        var token = await _oauth.GetValidAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            return new ToolResult { ToolName = Name, Success = false, Output = "Failed to get Google access token. Please reconnect your Google account." };

        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var action = DetermineAction(input);
            return action.Type switch
            {
                "list" => await ListFilesAsync(action.Query, action.Max, ct),
                "search" => await SearchFilesAsync(action.Query, action.Max, ct),
                "read" => await ReadFileAsync(action.FileId, action.Query, ct),
                "info" => await GetFileInfoAsync(action.FileId, action.Query, ct),
                "upload" => await UploadFileAsync(action.FileName, action.Content, ct),
                _ => new ToolResult { ToolName = Name, Success = false, Output = "Could not determine what Google Drive action to perform. Try: 'list my drive files', 'search for X in drive', 'read file X'." },
            };
        }
        catch (Exception ex)
        {
            return new ToolResult { ToolName = Name, Success = false, Output = $"Google Drive API error: {ex.Message}" };
        }
    }

    private DriveAction DetermineAction(string input)
    {
        var lower = input.ToLowerInvariant();
        var action = new DriveAction { Max = 10 };

        if (lower.Contains("upload") || lower.Contains("save to drive") || lower.Contains("create file in drive"))
        {
            action.Type = "upload";
            // Extract filename after "called" or "named"
            if (lower.Contains("called"))
            {
                var idx = lower.IndexOf("called");
                action.FileName = input[(idx + 7)..].Split('\n', ' ', '.')[0].Trim() + ".txt";
            }
            else if (lower.Contains("named"))
            {
                var idx = lower.IndexOf("named");
                action.FileName = input[(idx + 6)..].Split('\n', ' ', '.')[0].Trim() + ".txt";
            }
            else
            {
                action.FileName = "file.txt";
            }
            // Extract content after "with" or "containing"
            if (lower.Contains("with content"))
            {
                var idx = lower.IndexOf("with content");
                action.Content = input[(idx + 13)..].Trim();
            }
            else if (lower.Contains("containing"))
            {
                var idx = lower.IndexOf("containing");
                action.Content = input[(idx + 10)..].Trim();
            }
        }
        else if (lower.Contains("search") || lower.Contains("find file"))
        {
            action.Type = "search";
            action.Query = ExtractQuery(input, new[] { "search", "find file", "find", "for", "named", "called" });
        }
        else if (lower.Contains("read") || lower.Contains("open file") || lower.Contains("get content") || lower.Contains("show file"))
        {
            action.Type = "read";
            action.Query = ExtractQuery(input, new[] { "read", "open file", "open", "get content", "get", "show file", "show", "file" });
        }
        else if (lower.Contains("info") || lower.Contains("details about"))
        {
            action.Type = "info";
            action.Query = ExtractQuery(input, new[] { "info", "details about", "details", "about" });
        }
        else
        {
            action.Type = "list";
        }

        return action;
    }

    private static string ExtractQuery(string input, string[] keywords)
    {
        var lower = input.ToLowerInvariant();
        foreach (var kw in keywords)
        {
            var idx = lower.IndexOf(kw);
            if (idx >= 0)
                return input[(idx + kw.Length)..].Trim();
        }
        return input;
    }

    private async Task<ToolResult> ListFilesAsync(string? query, int max, CancellationToken ct)
    {
        var q = string.IsNullOrEmpty(query) ? "" : $" and name contains '{query.Replace("'", "\\'")}'";
        var url = $"https://www.googleapis.com/drive/v3/files?pageSize={max}&fields=files(id,name,mimeType,size,modifiedTime)&q=trashed=false{q}&orderBy=modifiedTime desc";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);

        var sb = new StringBuilder();
        sb.AppendLine("Google Drive files:");
        sb.AppendLine();

        if (doc.RootElement.TryGetProperty("files", out var files))
        {
            foreach (var f in files.EnumerateArray())
            {
                var name = f.GetProperty("name").GetString();
                var mime = f.GetProperty("mimeType").GetString();
                var size = f.TryGetProperty("size", out var s) ? long.Parse(s.GetString() ?? "0") : 0;
                var modified = f.TryGetProperty("modifiedTime", out var m) ? m.GetString() : "";
                var type = mime == "application/vnd.google-apps.folder" ? "📁" : mime?.StartsWith("application/vnd.google-apps.document") == true ? "📄" : "📎";
                sb.AppendLine($"  {type} {name} ({FormatSize(size)}) — Modified: {modified?.Split('T')[0]}");
            }
        }

        if (sb.Length < 30) sb.AppendLine("  No files found.");
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> SearchFilesAsync(string query, int max, CancellationToken ct)
    {
        return await ListFilesAsync(query, max, ct);
    }

    private async Task<ToolResult> ReadFileAsync(string? fileId, string? query, CancellationToken ct)
    {
        // If no file ID, search by name first
        if (string.IsNullOrEmpty(fileId) && !string.IsNullOrEmpty(query))
        {
            fileId = await FindFileIdAsync(query, ct);
            if (string.IsNullOrEmpty(fileId))
                return new ToolResult { ToolName = Name, Success = false, Output = $"No file found matching '{query}'." };
        }

        // Get file metadata
        var metaUrl = $"https://www.googleapis.com/drive/v3/files/{fileId}?fields=name,mimeType";
        var metaJson = await Http.GetStringAsync(metaUrl, ct);
        var meta = JsonDocument.Parse(metaJson);
        var name = meta.RootElement.GetProperty("name").GetString();
        var mime = meta.RootElement.GetProperty("mimeType").GetString();

        // For Google Docs, export as text
        if (mime == "application/vnd.google-apps.document")
        {
            var exportUrl = $"https://www.googleapis.com/drive/v3/files/{fileId}/export?mimeType=text/plain";
            var content = await Http.GetStringAsync(exportUrl, ct);
            return new ToolResult { ToolName = Name, Success = true, Output = $"File: {name}\n\n{content}" };
        }

        // For regular files, download content
        var downloadUrl = $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media";
        var resp = await Http.GetAsync(downloadUrl, ct);
        var content2 = await resp.Content.ReadAsStringAsync(ct);
        return new ToolResult { ToolName = Name, Success = true, Output = $"File: {name}\n\n{content2}" };
    }

    private async Task<ToolResult> GetFileInfoAsync(string? fileId, string? query, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileId) && !string.IsNullOrEmpty(query))
        {
            fileId = await FindFileIdAsync(query, ct);
            if (string.IsNullOrEmpty(fileId))
                return new ToolResult { ToolName = Name, Success = false, Output = $"No file found matching '{query}'." };
        }

        var url = $"https://www.googleapis.com/drive/v3/files/{fileId}?fields=name,mimeType,size,createdTime,modifiedTime,owners(displayName,emailAddress)";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        var f = doc.RootElement;
        var sb = new StringBuilder();
        sb.AppendLine($"File: {f.GetProperty("name").GetString()}");
        sb.AppendLine($"Type: {f.GetProperty("mimeType").GetString()}");
        if (f.TryGetProperty("size", out var s)) sb.AppendLine($"Size: {FormatSize(long.Parse(s.GetString() ?? "0"))}");
        sb.AppendLine($"Created: {f.GetProperty("createdTime").GetString()}");
        sb.AppendLine($"Modified: {f.GetProperty("modifiedTime").GetString()}");
        if (f.TryGetProperty("owners", out var owners))
            foreach (var o in owners.EnumerateArray())
                sb.AppendLine($"Owner: {o.GetProperty("displayName").GetString()} ({o.GetProperty("emailAddress").GetString()})");
        return new ToolResult { ToolName = Name, Success = true, Output = sb.ToString() };
    }

    private async Task<ToolResult> UploadFileAsync(string fileName, string? content, CancellationToken ct)
    {
        content ??= "";
        var metadata = JsonSerializer.Serialize(new { name = fileName });
        var boundary = "----OnyxBoundary" + Guid.NewGuid().ToString("N");
        var multipart = new MultipartContent("related", boundary);

        multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
        multipart.Add(new StringContent(content, Encoding.UTF8, "text/plain"));

        var resp = await Http.PostAsync("https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart", multipart, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return new ToolResult { ToolName = Name, Success = false, Output = $"Google Drive API error: {resp.StatusCode}\n{json}" };

        var doc = JsonDocument.Parse(json);
        return new ToolResult { ToolName = Name, Success = true, Output = $"File '{fileName}' uploaded to Google Drive (ID: {doc.RootElement.GetProperty("id").GetString()})" };
    }

    private async Task<string?> FindFileIdAsync(string name, CancellationToken ct)
    {
        var url = $"https://www.googleapis.com/drive/v3/files?pageSize=1&q=name='{name.Replace("'", "\\'")}' and trashed=false&fields=files(id,name)";
        var json = await Http.GetStringAsync(url, ct);
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
            return files[0].GetProperty("id").GetString();
        return null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };

    public static ToolDefinition Definition => new()
    {
        Name = "gdrive",
        Description = "Google Drive connector. List, search, read, upload, and get info about files in Google Drive. Requires Google account connection.",
        Category = "integration",
        Triggers = new() { "drive", "google drive", "gdrive", "upload file", "cloud file", "document" },
        RequiresConfirmation = false,
        Enabled = true,
    };
}

class DriveAction
{
    public string Type { get; set; } = "";
    public string? Query { get; set; }
    public string? FileId { get; set; }
    public string? FileName { get; set; }
    public string? Content { get; set; }
    public int Max { get; set; } = 10;
}
