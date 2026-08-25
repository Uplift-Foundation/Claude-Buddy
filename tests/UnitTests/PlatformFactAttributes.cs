using Xunit;

namespace ClaudeBuddy.Tests;

// Same pattern as tests/IntegrationTests/PlatformFactAttributes.cs: skip rather
// than fail, so a `dotnet test` run says "not exercised here" instead of
// silently never running the case at all.
//
// This suite is otherwise pure and platform-free, and should stay that way —
// the one thing that genuinely cannot be expressed off macOS is a *file* whose
// name contains a carriage return. NTFS rejects the character outright, so a
// test that has to create one throws System.IOException on Windows rather than
// asserting anything. That is not a portability problem in the code under test:
// a custom Finder icon is a macOS concept, HasCustomIcon guards with try/catch,
// and every caller sits behind an OperatingSystem.IsMacOS() check already.
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "creates a file named with a carriage return, which only macOS permits";
    }
}
