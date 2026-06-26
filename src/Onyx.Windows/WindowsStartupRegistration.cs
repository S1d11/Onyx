using Microsoft.Win32;

namespace Ollama2;

/// <summary>
/// Windows implementation of <see cref="IStartupRegistration"/> using the
/// HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run registry key.
/// </summary>
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Onyx";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
        if (key == null) return;

        if (enabled)
        {
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            if (key.GetValue(AppName) != null)
                key.DeleteValue(AppName);
        }
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
        return key?.GetValue(AppName) != null;
    }
}
