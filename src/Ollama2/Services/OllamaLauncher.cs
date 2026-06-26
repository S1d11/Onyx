using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ollama2.Services;

/// <summary>
/// Detects whether the local Ollama server is running and auto-launches
/// <c>ollama serve</c> if it isn't.  Only acts when the configured
/// server URL points to localhost (default http://localhost:11434).
/// </summary>
public static class OllamaLauncher
{
    /// <summary>
    /// Try to ensure Ollama is running.  Returns <c>true</c> if the server
    /// is reachable (either it was already running or we started it).
    /// </summary>
    public static async Task<bool> EnsureRunningAsync(string serverUrl, CancellationToken ct = default)
    {
        // Only auto-launch for localhost URLs
        if (!IsLocalhost(serverUrl))
            return false;

        // Check if already reachable
        var client = new OllamaClient(() => serverUrl);
        if (await client.IsReachableAsync())
            return true;

        // Check if ollama.exe process is already running
        if (IsOllamaProcessRunning())
        {
            // Process exists but not responding yet — give it a moment
            if (await PollReachableAsync(client, maxWaitMs: 8000, ct))
                return true;
        }

        // Try to start ollama serve
        var exePath = FindOllamaExe();
        if (string.IsNullOrEmpty(exePath))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(psi);
        }
        catch
        {
            return false;
        }

        // Poll until the server is reachable
        return await PollReachableAsync(client, maxWaitMs: 30_000, ct);
    }

    public static bool IsLocalhost(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var u = url.ToLowerInvariant();
        return u.Contains("localhost") || u.Contains("127.0.0.1") || u.Contains("[::1]");
    }

    private static bool IsOllamaProcessRunning()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("ollama"))
            {
                try { proc.Dispose(); } catch { }
                return true;
            }
        }
        catch { /* best effort */ }
        return false;
    }

    private static string? FindOllamaExe()
    {
        // 1. Check PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir.Trim(), "ollama.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        // 2. Common install locations
        var commonPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ollama", "ollama.exe"),
        };
        foreach (var p in commonPaths)
        {
            if (File.Exists(p))
                return p;
        }

        return null;
    }

    private static async Task<bool> PollReachableAsync(OllamaClient client, int maxWaitMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            if (ct.IsCancellationRequested)
                return false;
            try
            {
                if (await client.IsReachableAsync())
                    return true;
            }
            catch { /* ignore */ }
            await Task.Delay(500, ct);
        }
        return false;
    }
}
