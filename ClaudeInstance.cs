namespace ClaudeBuddy
{
    // Shared shape between MacOSProcessScan and WindowsProcessScan, so
    // ClaudeDesktopManager.MapInstances works from either without caring which
    // platform produced the list. UserDataDir is null when the instance was
    // launched without the override — a Dock/shell launch, or our own launch of
    // the Default profile.
    internal readonly record struct ClaudeInstance(int Pid, string? UserDataDir);
}
