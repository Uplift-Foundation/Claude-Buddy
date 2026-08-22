using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ClaudeBuddy.Tests.TestAppBuilder))]

namespace ClaudeBuddy.Tests;

// Unlike tests/UiTests, this project turns real rendering ON
// (UseHeadlessDrawing = false, plus .UseSkia()) — the whole point of this
// suite is producing an actual bitmap of what a control looks like, which
// the null headless renderer tests/UiTests uses never draws at all.
//
// A separate assembly, a separate AppBuilder, deliberately: switching the
// existing 28-test suite to real rendering would risk reopening the
// Avalonia.Headless font-manager/dispatcher race that suite's tests fought
// hard to stabilize (see tests/UiTests/TestAppBuilder.cs's own comment) —
// under a different, heavier rendering path that hasn't been stress-tested
// against it at all. Keeping this isolated means a screenshot-capture flake
// can only ever affect screenshot capture.
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ClaudeBuddy.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
