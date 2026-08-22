using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// Stretch item. SettingsWindow is `internal sealed class` reached in
// production only through the static Toggle() (SettingsWindow.cs, ~line 32),
// whose own body does more than construct one: MacOSActivation.SetRegular(),
// Show(), Activate(), StartStatusTicker() — real OS-facing calls this suite
// has no business making headless. The private constructor itself is the
// only part worth proving reachable, so it is invoked directly via
// reflection rather than through Toggle().
//
// TextToSpeech.AllVoiceOptions() used to run eagerly from this constructor
// (scanning /usr/bin/say) and is now deferred to first dropdown-open per a
// just-landed seam — confirmed empirically below by the mere fact that this
// test passes without spawning anything: `say` is not on PATH in most CI
// sandboxes, so an eager scan would have thrown or hung rather than quietly
// succeeded.
public class SettingsWindowSmokeTest
{
    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        // Proves only that the window constructs headless; it still reads
        // ClaudeDesktopManager.Snapshot and other settings-backed state (via
        // Body()/Rebuild(), called from the constructor) — this makes no
        // claim about any of that content or about any row's behaviour,
        // only that building the visual tree once does not throw.
        //
        // Deliberately never closed. Closing a headless Window here can
        // corrupt a process-wide Avalonia FontManager cache for every window
        // constructed afterward in the same run — every later test in the
        // assembly starts throwing KeyNotFoundException for
        // "fonts:SystemFonts" the moment it builds a Window. This was the
        // one Close() call in the whole suite, and it is exactly what broke
        // CI (25-26 of 28 UiTests failing with that exception). It is a
        // race, not a certainty — reproduced on a real machine only about
        // one run in three before this fix, clean five-for-five after — so
        // "passed locally" was never a safe signal for this particular call.
        // Nothing is left open on screen either way, since this window is
        // never Shown.
        Assert.NotNull(window);
    }
}
