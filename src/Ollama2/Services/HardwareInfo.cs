using System;
using System.Linq;
using System.Management;

namespace Ollama2.Services;

public record HardwareInfo(
    string CpuName,
    ulong TotalRamBytes,
    string? GpuName,
    ulong? GpuVramBytes
)
{
    public double TotalRamGb => Math.Round(TotalRamBytes / (1024.0 * 1024.0 * 1024.0), 1);
    public double? GpuVramGb => GpuVramBytes.HasValue ? Math.Round(GpuVramBytes.Value / (1024.0 * 1024.0 * 1024.0), 1) : null;
}

public static class HardwareDetector
{
    public static HardwareInfo Detect()
    {
        string cpuName = "Unknown";
        ulong totalRam = 0;
        string? gpuName = null;
        ulong? gpuVram = null;

        try
        {
            using var cpuSearch = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject obj in cpuSearch.Get())
            {
                cpuName = obj["Name"]?.ToString()?.Trim() ?? "Unknown";
                break;
            }
        }
        catch { }

        try
        {
            using var ramSearch = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in ramSearch.Get())
            {
                totalRam = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                break;
            }
        }
        catch { }

        try
        {
            using var gpuSearch = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController WHERE AdapterRAM IS NOT NULL");
            foreach (ManagementObject obj in gpuSearch.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                var vram = obj["AdapterRAM"] as uint?;
                if (!string.IsNullOrEmpty(name) && vram.HasValue && vram.Value > 0)
                {
                    // Skip generic/basic display adapters
                    var lower = name.ToLowerInvariant();
                    if (lower.Contains("basic") || lower.Contains("standard vga") || lower.Contains("microsoft basic"))
                        continue;

                    gpuName = name;
                    gpuVram = vram.Value;
                    break; // Use the first valid dedicated GPU
                }
            }
        }
        catch { }

        return new HardwareInfo(cpuName, totalRam, gpuName, gpuVram);
    }
}
