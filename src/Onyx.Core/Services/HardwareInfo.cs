using System;
using System.Runtime.InteropServices;

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

/// <summary>Platform-specific hardware detector interface.</summary>
public interface IHardwareDetector
{
    HardwareInfo Detect();
}

/// <summary>
/// Hardware detection. Falls back to basic info if no platform-specific detector is registered.
/// </summary>
public static class HardwareDetector
{
    /// <summary>Register a platform-specific detector (e.g., Windows WMI, macOS sysctl).</summary>
    public static IHardwareDetector? Instance { get; set; }

    public static HardwareInfo Detect()
    {
        if (Instance != null)
            return Instance.Detect();

        // Fallback: minimal info available on all platforms
        ulong ram = 0;
        try
        {
            ram = (ulong)GC.GetTotalMemory(false);
        }
        catch { }

        return new HardwareInfo(
            RuntimeInformation.ProcessArchitecture.ToString(),
            ram,
            null,
            null
        );
    }
}
