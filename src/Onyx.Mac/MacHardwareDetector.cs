using System.Diagnostics;
using Ollama2.Services;

namespace Onyx.Mac;

/// <summary>
/// macOS-specific hardware detection using system_profiler and sysctl.
/// </summary>
public sealed class MacHardwareDetector : IHardwareDetector
{
    public HardwareInfo Detect()
    {
        string cpuName = Run("sysctl", "-n machdep.cpu.brand_string").Trim();
        if (string.IsNullOrEmpty(cpuName)) cpuName = Run("sysctl", "-n hw.model").Trim();

        ulong ramBytes = 0;
        if (ulong.TryParse(Run("sysctl", "-n hw.memsize").Trim(), out var mem)) ramBytes = mem;

        // Try to get GPU info from system_profiler
        string? gpuName = null;
        try
        {
            var gfx = Run("system_profiler", "SPDisplaysDataType");
            var match = System.Text.RegularExpressions.Regex.Match(gfx, @"Chipset Model:\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (match.Success) gpuName = match.Groups[1].Value.Trim();
        }
        catch { }

        return new HardwareInfo(
            string.IsNullOrEmpty(cpuName) ? "Apple Silicon / Intel Mac" : cpuName,
            ramBytes,
            gpuName,
            null
        );
    }

    private static string Run(string command, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/env",
                Arguments = $"{command} {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            proc.WaitForExit(5000);
            return proc.StandardOutput.ReadToEnd();
        }
        catch { return ""; }
    }
}
