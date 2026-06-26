using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Ollama2.Orchestrator;

namespace Onyx.Windows;

/// <summary>
/// Windows implementation of ISystemAccess.
/// Provides full access to: filesystem, shell (cmd, powershell, pwsh),
/// registry (HKCR/HKCU/HKLM/HKU/HKCC), environment variables, PATH,
/// processes, and system info.
/// </summary>
public class WindowsSystemAccess : ISystemAccess
{
    public string Platform => "Windows";

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
                return Task.FromResult($"File is too large to read ({FormatSize(info.Length)}). Use run_command with 'more' or 'head' to read parts.");
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
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            switch (shell.ToLowerInvariant())
            {
                case "cmd":
                    psi.FileName = "cmd.exe";
                    psi.Arguments = $"/c {command}";
                    break;
                case "powershell":
                case "ps":
                    psi.FileName = "powershell.exe";
                    psi.Arguments = $"-NoProfile -Command {command}";
                    break;
                case "pwsh":
                    psi.FileName = "pwsh.exe";
                    psi.Arguments = $"-NoProfile -Command {command}";
                    break;
                case "bash":
                case "sh":
                    psi.FileName = "bash";
                    psi.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
                    break;
                case "wsl":
                    psi.FileName = "wsl.exe";
                    psi.Arguments = command;
                    break;
                default:
                    // Try to run it directly
                    psi.FileName = shell;
                    psi.Arguments = command;
                    break;
            }

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            // Wait with cancellation support
            using var reg = ct.Register(() => { try { proc.Kill(); } catch { } });
            await proc.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var sb = new StringBuilder();
            sb.AppendLine($"Shell: {shell}");
            sb.AppendLine($"Command: {command}");
            sb.AppendLine($"Exit code: {proc.ExitCode}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(stdout))
            {
                sb.AppendLine("stdout:");
                sb.AppendLine(stdout);
            }
            if (!string.IsNullOrEmpty(stderr))
            {
                sb.AppendLine("stderr:");
                sb.AppendLine(stderr);
            }
            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            return "Command was cancelled.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // ==================== REGISTRY ====================

    public Task<string> RegistryReadAsync(string keyPath, string valueName, CancellationToken ct = default)
    {
        try
        {
            using var key = ParseRegistryKey(keyPath, writable: false);
            if (key == null) return Task.FromResult($"Registry key not found: {keyPath}");
            var val = key.GetValue(valueName);
            if (val == null) return Task.FromResult($"Value '{valueName}' not found in {keyPath}");
            var kind = key.GetValueKind(valueName);
            return Task.FromResult($"{keyPath}\\{valueName} = {val} (type: {kind})");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> RegistryWriteAsync(string keyPath, string valueName, string value, CancellationToken ct = default)
    {
        try
        {
            using var key = ParseRegistryKey(keyPath, writable: true);
            if (key == null) return Task.FromResult($"Could not open/create registry key: {keyPath}");
            key.SetValue(valueName, value, RegistryValueKind.String);
            return Task.FromResult($"Wrote {valueName} = {value} to {keyPath}");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> RegistryDeleteAsync(string keyPath, string valueName, CancellationToken ct = default)
    {
        try
        {
            using var key = ParseRegistryKey(keyPath, writable: true);
            if (key == null) return Task.FromResult($"Registry key not found: {keyPath}");
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return Task.FromResult($"Deleted value '{valueName}' from {keyPath}");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> RegistryListAsync(string keyPath, CancellationToken ct = default)
    {
        try
        {
            using var key = ParseRegistryKey(keyPath, writable: false);
            if (key == null) return Task.FromResult($"Registry key not found: {keyPath}");

            var sb = new StringBuilder();
            sb.AppendLine($"Registry key: {keyPath}");
            sb.AppendLine();
            sb.AppendLine("Subkeys:");
            foreach (var sub in key.GetSubKeyNames())
                sb.AppendLine($"  [KEY]  {sub}");
            sb.AppendLine();
            sb.AppendLine("Values:");
            foreach (var name in key.GetValueNames())
            {
                var val = key.GetValue(name);
                var kind = key.GetValueKind(name);
                sb.AppendLine($"  {name,-40} = {val} ({kind})");
            }
            if (key.GetValueNames().Length == 0) sb.AppendLine("  (no values)");
            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    private static RegistryKey? ParseRegistryKey(string path, bool writable)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Split(new[] { '\\', '/' }, 2);
        if (parts.Length < 1) return null;

        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKU" or "HKEY_USERS" => Registry.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
            _ => null,
        };
        if (hive == null) return null;

        if (parts.Length < 2 || string.IsNullOrEmpty(parts[1]))
            return writable ? hive : hive;

        return writable ? hive.CreateSubKey(parts[1]) : hive.OpenSubKey(parts[1]);
    }

    // ==================== ENVIRONMENT ====================

    public Task<string> GetEnvironmentVariableAsync(string name, CancellationToken ct = default)
    {
        var val = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
                  ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                  ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        return Task.FromResult(val == null ? $"Environment variable '{name}' is not set." : $"{name} = {val}");
    }

    public Task<string> SetEnvironmentVariableAsync(string name, string value, CancellationToken ct = default)
    {
        try
        {
            // Set at both User and Process level (Machine requires admin)
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
            return Task.FromResult($"Set {name} = {value} (User + Process scope)");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> ListEnvironmentVariablesAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Environment Variables:");
        sb.AppendLine();
        var seen = new HashSet<string>();
        // Process-level (includes inherited)
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process))
        {
            if (seen.Add(e.Key?.ToString() ?? "")) sb.AppendLine($"  {e.Key} = {Truncate(e.Value?.ToString() ?? "", 100)}");
        }
        // User-level
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User))
        {
            if (seen.Add(e.Key?.ToString() ?? "")) sb.AppendLine($"  [USER] {e.Key} = {Truncate(e.Value?.ToString() ?? "", 100)}");
        }
        // Machine-level
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine))
        {
            if (seen.Add(e.Key?.ToString() ?? "")) sb.AppendLine($"  [MACHINE] {e.Key} = {Truncate(e.Value?.ToString() ?? "", 100)}");
        }
        return Task.FromResult(sb.ToString());
    }

    public Task<string> GetPathAsync(CancellationToken ct = default)
    {
        var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("PATH (User):");
        foreach (var p in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            sb.AppendLine($"  {p}");
        sb.AppendLine();
        sb.AppendLine("PATH (Machine):");
        foreach (var p in machinePath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            sb.AppendLine($"  {p}");
        return Task.FromResult(sb.ToString());
    }

    public Task<string> AddToPathAsync(string directory, CancellationToken ct = default)
    {
        try
        {
            var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            if (current.Contains(directory, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult($"{directory} is already in PATH (User).");
            var newPath = current.TrimEnd(';') + ";" + directory;
            Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.User);
            // Also update current process
            var procPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? "";
            Environment.SetEnvironmentVariable("PATH", procPath.TrimEnd(';') + ";" + directory, EnvironmentVariableTarget.Process);
            return Task.FromResult($"Added {directory} to PATH (User scope).");
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    // ==================== SYSTEM INFO ====================

    public Task<string> GetSystemInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== System Information ===");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"Platform: {Environment.OSVersion.Platform}");
            sb.AppendLine($".NET Runtime: {Environment.Version}");
            sb.AppendLine($"Machine Name: {Environment.MachineName}");
            sb.AppendLine($"User: {Environment.UserName}");
            sb.AppendLine($"User Domain: {Environment.UserDomainName}");
            sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");
            sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
            sb.AppendLine($"System Directory: {Environment.SystemDirectory}");
            sb.AppendLine($"Current Directory: {Environment.CurrentDirectory}");
            sb.AppendLine($"Temp Directory: {Path.GetTempPath()}");

            // Drives
            sb.AppendLine();
            sb.AppendLine("Drives:");
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                sb.AppendLine($"  {d.Name} {d.DriveFormat,-10} {FormatSize(d.TotalSize),12} total  {FormatSize(d.AvailableFreeSpace),12} free");

            // Memory (via GlobalMemoryStatusEx)
            var mem = GetMemoryStatus();
            if (mem != null)
            {
                sb.AppendLine();
                sb.AppendLine("Memory:");
                sb.AppendLine($"  Total: {FormatSize((long)mem.TotalPhysical)}");
                sb.AppendLine($"  Available: {FormatSize((long)mem.AvailablePhysical)}");
                sb.AppendLine($"  Total Virtual: {FormatSize((long)mem.TotalVirtual)}");
                sb.AppendLine($"  Available Virtual: {FormatSize((long)mem.AvailableVirtual)}");
            }

            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex) { return Task.FromResult($"Error: {ex.Message}"); }
    }

    public Task<string> ListProcessesAsync(CancellationToken ct = default)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Running Processes:");
            sb.AppendLine();
            sb.AppendLine($"{"PID",8}  {"Name",-40} {"Memory",12} {"CPU(s)",10}");
            sb.AppendLine(new string('-', 75));
            foreach (var p in Process.GetProcesses().OrderBy(x => x.ProcessName))
            {
                try
                {
                    var mem = FormatSize(p.WorkingSet64);
                    var cpu = p.TotalProcessorTime.TotalSeconds.ToString("F1");
                    sb.AppendLine($"{p.Id,8}  {p.ProcessName,-40} {mem,12} {cpu,10}s");
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

    // P/Invoke for memory info
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private record MemInfo(ulong TotalPhysical, ulong AvailablePhysical, ulong TotalVirtual, ulong AvailableVirtual);

    private static MemInfo? GetMemoryStatus()
    {
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
                return new MemInfo(mem.ullTotalPhys, mem.ullAvailPhys, mem.ullTotalVirtual, mem.ullAvailVirtual);
        }
        catch { }
        return null;
    }
}
