using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ollama2.Orchestrator;

namespace Onyx.Mac;

/// <summary>
/// macOS implementation of ISystemAccess.
/// Provides access to: filesystem, shell (bash, zsh, sh), environment variables,
/// PATH, processes, and system info.
/// Registry operations return "not supported" (macOS uses plist/defaults, not a registry).
/// </summary>
public class MacSystemAccess : ISystemAccess
{
    public string Platform => "macOS";

    // ==================== FILESYSTEM ====================

    public Task<string> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var di = new DirectoryInfo(path);
            if (!di.Exists) return Task.FromResult($"Directory not found: {path}");

            var sb = new StringBuilder();
            sb.AppendLine($"Directory: {di.FullName}");
            sb.AppendLine($"Created: {di.CreationTime}");
            sb.AppendLine();
            sb.AppendLine("Directories:");
            foreach (var d in di.GetDirectories().OrderBy(x => x.Name))
                sb.AppendLine($"  [DIR]  {d.Name,-40} {d.LastWriteTime:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine("Files:");
            foreach (var f in di.GetFiles().OrderBy(x => x.Name))
                sb.AppendLine($"  {FormatSize(f.Length),12}  {f.Name,-40} {f.LastWriteTime:yyyy-MM-dd HH:mm}");
            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path)) return Task.FromResult($"File not found: {path}");
            var info = new FileInfo(path);
            if (info.Length > 5_000_000)
                return Task.FromResult($"File is too large to read ({FormatSize(info.Length)}). Use run_command with 'head' to read parts.");
            var content = File.ReadAllText(path);
            return Task.FromResult(content);
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> WriteFileAsync(string path, string content, CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
            return Task.FromResult($"Wrote {content.Length} characters to {path}");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> DeleteFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return Task.FromResult($"Deleted directory: {path}");
            }
            if (File.Exists(path))
            {
                File.Delete(path);
                return Task.FromResult($"Deleted file: {path}");
            }
            return Task.FromResult($"Not found: {path}");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(path);
            return Task.FromResult($"Created directory: {path}");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> FileExistsAsync(string path, CancellationToken ct = default)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        var type = Directory.Exists(path) ? "directory" : File.Exists(path) ? "file" : "not found";
        return Task.FromResult($"{path}: {type} ({(exists ? "exists" : "does not exist")})");
    }

    public Task<string> GetFileInfoAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                return Task.FromResult(
                    $"Path: {di.FullName}\nType: Directory\nCreated: {di.CreationTime}\nModified: {di.LastWriteTime}\nAccessed: {di.LastAccessTime}\nAttributes: {di.Attributes}\nSubdirectories: {di.GetDirectories().Length}\nFiles: {di.GetFiles().Length}");
            }
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return Task.FromResult(
                    $"Path: {fi.FullName}\nType: File\nSize: {FormatSize(fi.Length)} ({fi.Length} bytes)\nCreated: {fi.CreationTime}\nModified: {fi.LastWriteTime}\nAccessed: {fi.LastAccessTime}\nAttributes: {fi.Attributes}\nExtension: {fi.Extension}");
            }
            return Task.FromResult($"Not found: {path}");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    // ==================== SHELL ====================

    public async Task<string> RunCommandAsync(string shell, string command, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            switch (shell.ToLowerInvariant())
            {
                case "bash":
                    psi.FileName = "/bin/bash";
                    psi.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
                    break;
                case "zsh":
                    psi.FileName = "/bin/zsh";
                    psi.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
                    break;
                case "sh":
                    psi.FileName = "/bin/sh";
                    psi.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
                    break;
                case "defaults":
                    // macOS defaults command (plist management, the closest thing to registry)
                    psi.FileName = "/usr/bin/defaults";
                    psi.Arguments = command;
                    break;
                default:
                    psi.FileName = shell;
                    psi.Arguments = command;
                    break;
            }

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var reg = ct.Register(() => { try { proc.Kill(); } catch { } });
            await proc.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var sb = new StringBuilder();
            sb.AppendLine($"Shell: {shell}");
            sb.AppendLine($"Command: {command}");
            sb.AppendLine($"Exit code: {proc.ExitCode}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(stdout)) { sb.AppendLine("stdout:"); sb.AppendLine(stdout); }
            if (!string.IsNullOrEmpty(stderr)) { sb.AppendLine("stderr:"); sb.AppendLine(stderr); }
            return sb.ToString();
        }
        catch (OperationCanceledException) { return "Command was cancelled."; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ==================== REGISTRY (not supported on macOS) ====================
    // macOS uses plist files managed via `defaults` command — use run_command with shell="defaults"

    public Task<string> RegistryReadAsync(string keyPath, string valueName, CancellationToken ct = default)
        => Task.FromResult("Registry is not available on macOS. Use 'defaults read' via run_command (shell=defaults) to read plist settings.");

    public Task<string> RegistryWriteAsync(string keyPath, string valueName, string value, CancellationToken ct = default)
        => Task.FromResult("Registry is not available on macOS. Use 'defaults write' via run_command (shell=defaults) to modify plist settings.");

    public Task<string> RegistryDeleteAsync(string keyPath, string valueName, CancellationToken ct = default)
        => Task.FromResult("Registry is not available on macOS. Use 'defaults delete' via run_command (shell=defaults) to delete plist settings.");

    public Task<string> RegistryListAsync(string keyPath, CancellationToken ct = default)
        => Task.FromResult("Registry is not available on macOS. Use 'defaults read' via run_command (shell=defaults) to list plist settings.");

    // ==================== ENVIRONMENT ====================

    public Task<string> GetEnvironmentVariableAsync(string name, CancellationToken ct = default)
    {
        var val = Environment.GetEnvironmentVariable(name);
        return Task.FromResult(val == null ? $"Environment variable '{name}' is not set." : $"{name} = {val}");
    }

    public Task<string> SetEnvironmentVariableAsync(string name, string value, CancellationToken ct = default)
    {
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            return Task.FromResult($"Set {name} = {value} (Process scope). Note: to persist on macOS, add to ~/.zshrc or ~/.bash_profile.");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> ListEnvironmentVariablesAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Environment Variables:");
        sb.AppendLine();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            sb.AppendLine($"  {e.Key} = {Truncate(e.Value?.ToString() ?? "", 100)}");
        return Task.FromResult(sb.ToString());
    }

    public Task<string> GetPathAsync(CancellationToken ct = default)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("PATH:");
        foreach (var p in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            sb.AppendLine($"  {p}");
        return Task.FromResult(sb.ToString());
    }

    public async Task<string> AddToPathAsync(string directory, CancellationToken ct = default)
    {
        try
        {
            // On macOS, persist to ~/.zshrc (default shell since Catalina)
            var zshrc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zshrc");
            var line = $"export PATH=\"$PATH:{directory}\"";

            if (File.Exists(zshrc))
            {
                var existing = await File.ReadAllTextAsync(zshrc, ct);
                if (existing.Contains(directory))
                    return $"{directory} is already in PATH (found in ~/.zshrc).";
            }

            await File.AppendAllTextAsync(zshrc, "\n" + line + "\n", ct);
            // Also update current process
            var current = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", current + ":" + directory);
            return $"Added {directory} to PATH (persisted to ~/.zshrc).";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ==================== SYSTEM INFO ====================

    public async Task<string> GetSystemInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== System Information ===");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($".NET Runtime: {Environment.Version}");
            sb.AppendLine($"Machine Name: {Environment.MachineName}");
            sb.AppendLine($"User: {Environment.UserName}");
            sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");
            sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"Current Directory: {Environment.CurrentDirectory}");
            sb.AppendLine($"Temp Directory: {Path.GetTempPath()}");

            // Drives
            sb.AppendLine();
            sb.AppendLine("Drives:");
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                sb.AppendLine($"  {d.Name} {d.DriveFormat,-10} {FormatSize(d.TotalSize),12} total  {FormatSize(d.AvailableFreeSpace),12} free");

            // macOS-specific info via sw_vers and sysctl
            try
            {
                var swVers = await RunCommandAsync("bash", "sw_vers", ct);
                sb.AppendLine();
                sb.AppendLine("macOS Version:");
                sb.AppendLine(swVers);
            }
            catch { }

            try
            {
                var memInfo = await RunCommandAsync("bash", "sysctl -n hw.memsize", ct);
                if (long.TryParse(memInfo.Split('\n').FirstOrDefault(x => x.Trim().Length > 0)?.Trim(), out var memBytes))
                {
                    sb.AppendLine($"Total Memory: {FormatSize(memBytes)}");
                }
            }
            catch { }

            return sb.ToString();
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}").Result; }
    }

    public Task<string> ListProcessesAsync(CancellationToken ct = default)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Running Processes:");
            sb.AppendLine();
            sb.AppendLine($"{"PID",8}  {"Name",-40} {"Memory",12}");
            sb.AppendLine(new string('-', 65));
            foreach (var p in Process.GetProcesses().OrderBy(x => x.ProcessName))
            {
                try
                {
                    sb.AppendLine($"{p.Id,8}  {p.ProcessName,-40} {FormatSize(p.WorkingSet64),12}");
                }
                catch { }
            }
            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> KillProcessAsync(int pid, CancellationToken ct = default)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            return Task.FromResult($"Killed process: {name} (PID: {pid})");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    // ==================== HELPERS ====================

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);
}
