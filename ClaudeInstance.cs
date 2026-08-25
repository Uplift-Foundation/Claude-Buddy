namespace ClaudeBuddy
{
    // Shared shape between MacOSProcessScan and WindowsProcessScan, so
    // ClaudeDesktopManager.MapInstances works from either without caring which
    // platform produced the list. UserDataDir is null when the instance was
    // launched without the override — a Dock/shell launch, or our own launch of
    // the Default profile.
    //
    // BundlePath is the .app this instance is actually running from — its
    // profile's tinted clone, or the installed /Applications/Claude.app. It is
    // the only thing that distinguishes one running instance's *app* from
    // another's, because every clone deliberately keeps Claude Desktop's bundle
    // id (see ClaudeDesktopBundles), and it is what a forwarded URL has to be
    // addressed to. Null on Windows, which has no clones and no per-instance
    // bundle to speak of.
    internal readonly record struct ClaudeInstance(int Pid, string? UserDataDir, string? BundlePath = null);
}
