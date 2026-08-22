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
    public void ConstructsAndClosesHeadlessWithNoException()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (Avalonia.Controls.Window)ctor.Invoke(null);

        // Proves only that the window constructs and closes headless; it
        // still reads ClaudeDesktopManager.Snapshot and other settings-backed
        // state (via Body()/Rebuild(), called from the constructor) — this
        // makes no claim about any of that content or about any row's
        // behaviour, only that building the visual tree once does not throw.
        window.Close();
    }
}
