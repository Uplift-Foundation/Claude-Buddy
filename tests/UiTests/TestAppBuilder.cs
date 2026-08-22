using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

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
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .AfterSetup(WarmUpFontManager);

    // Every real test failure this suite hit in CI ((KeyNotFoundException:
    // "fonts:SystemFonts")) traced back to Avalonia.Media.FontManager's
    // system-font cache — a lazily-populated dictionary — not finishing its
    // first-ever population cleanly. Once that first population is broken,
    // it stays broken: every later Window construction in the same process
    // throws the same way, which is why one bad run failed as few as 1 of
    // 28 tests and as many as 27. Neither removing the one place a test
    // closed a window, nor disabling xUnit's cross-collection parallelism,
    // stopped it recurring on a real CI runner — so this forces the exact
    // path that throws (building a real Window, which is what populates the
    // cache) to run exactly once, synchronously, here, before AppBuilder
    // hands control back to the test host and before any test can race it.
    // A short retry loop covers the case where the first attempt itself
    // lands mid-race: once one attempt succeeds, the cache is warm for
    // the rest of the process.
    private static void WarmUpFontManager(AppBuilder builder)
    {
        Exception? last = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // Constructing a Window is enough on its own — Window's base
                // constructor chain eagerly builds a CompositingRenderer,
                // which is what forces FontManager's first population — so
                // nothing further needs to be done with it. Deliberately
                // never closed: closing a headless Window is a separate,
                // already-confirmed way to corrupt this same cache (see
                // SettingsWindowSmokeTest's own comment), and this window is
                // never shown, so leaking it costs nothing.
                Dispatcher.UIThread.Invoke(() => { new Window(); });
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            "FontManager warm-up never succeeded after 5 attempts.", last);
    }
}
