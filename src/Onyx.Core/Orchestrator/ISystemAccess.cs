using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ollama2.Orchestrator;

/// <summary>
/// Platform-agnostic interface for system-level access.
/// Windows implements all of it (filesystem, shell, registry, env).
/// macOS implements everything except registry (not applicable).
/// </summary>
public interface ISystemAccess
{
    string Platform { get; }

    // ---- Filesystem ----
    Task<string> ListDirectoryAsync(string path, CancellationToken ct = default);
    Task<string> ReadFileAsync(string path, CancellationToken ct = default);
    Task<string> WriteFileAsync(string path, string content, CancellationToken ct = default);
    Task<string> DeleteFileAsync(string path, CancellationToken ct = default);
    Task<string> CreateDirectoryAsync(string path, CancellationToken ct = default);
    Task<string> FileExistsAsync(string path, CancellationToken ct = default);
    Task<string> GetFileInfoAsync(string path, CancellationToken ct = default);

    // ---- Shell ----
    /// <summary>Execute a shell command. shell = "cmd", "powershell", "pwsh", "bash", etc.</summary>
    Task<string> RunCommandAsync(string shell, string command, CancellationToken ct = default);

    // ---- Registry (Windows only; macOS returns "not supported") ----
    Task<string> RegistryReadAsync(string keyPath, string valueName, CancellationToken ct = default);
    Task<string> RegistryWriteAsync(string keyPath, string valueName, string value, CancellationToken ct = default);
    Task<string> RegistryDeleteAsync(string keyPath, string valueName, CancellationToken ct = default);
    Task<string> RegistryListAsync(string keyPath, CancellationToken ct = default);

    // ---- Environment ----
    Task<string> GetEnvironmentVariableAsync(string name, CancellationToken ct = default);
    Task<string> SetEnvironmentVariableAsync(string name, string value, CancellationToken ct = default);
    Task<string> ListEnvironmentVariablesAsync(CancellationToken ct = default);
    Task<string> GetPathAsync(CancellationToken ct = default);
    Task<string> AddToPathAsync(string directory, CancellationToken ct = default);

    // ---- System info ----
    Task<string> GetSystemInfoAsync(CancellationToken ct = default);
    Task<string> ListProcessesAsync(CancellationToken ct = default);
    Task<string> KillProcessAsync(int pid, CancellationToken ct = default);
}

/// <summary>
/// Result of a system action, with success/failure and output.
/// </summary>
public class SystemActionResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public bool IsDestructive { get; set; }
}
