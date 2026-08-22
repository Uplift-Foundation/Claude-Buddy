using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ClaudeBuddy.Tests.TestAppBuilder))]

namespace ClaudeBuddy.Tests;

// The real App, not a stand-in built for tests. Confirmed by spike: under
// HeadlessUnitTestSession, Application.Current.ApplicationLifetime is null,
// so the guard in App.axaml.cs's OnFrameworkInitializationCompleted —
// `if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)`
// — short-circuits the whole body (mutex, SessionManager.Start(), tray icon,
// speech engine). Nothing in App ever runs beyond styles being composed.
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ClaudeBuddy.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
