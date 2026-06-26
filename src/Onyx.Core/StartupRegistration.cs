namespace Ollama2;

/// <summary>
/// Global access point for the platform-specific startup registration.
/// Each platform shell sets <see cref="Instance"/> during app startup.
/// </summary>
public static class StartupRegistration
{
    public static IStartupRegistration? Instance { get; set; }
}
