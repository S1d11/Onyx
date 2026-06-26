namespace Ollama2;

/// <summary>
/// Platform-specific startup registration abstraction.
/// Enables or disables the app launching automatically on system login.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>Enable or disable auto-launch on system login.</summary>
    void SetEnabled(bool enabled);

    /// <summary>Check whether auto-launch is currently enabled.</summary>
    bool IsEnabled();
}
