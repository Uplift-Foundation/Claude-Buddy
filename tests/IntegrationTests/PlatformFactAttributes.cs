using Xunit;

namespace ClaudeBuddy.Tests;

// ClaudeBuddyHook.sh and ClaudeBuddyHook.ps1 are twins of each other, one per
// platform, and only one of the two interpreters exists on any given CI
// runner or dev machine. These skip rather than fail to compile/run on the
// wrong OS, so a `dotnet test` run reports "skipped" for the twin that
// couldn't have run here instead of silently never exercising it.
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            Skip = "bash hook only runs on macOS/Linux";
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "ps1 hook only runs on Windows";
    }
}
