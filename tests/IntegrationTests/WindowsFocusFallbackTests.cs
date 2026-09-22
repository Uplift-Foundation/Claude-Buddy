using System.Reflection;
using Xunit;

namespace ClaudeBuddy.Tests;

// This has to run on Windows: FocusCore deliberately selects its platform
// boundary from OperatingSystem rather than from an injected test double. An
// unknown terminal is harmless there, but it distinguishes a focus attempt
// that failed from the old unconditional success which suppressed the
// background-session attach fallback.
public class WindowsFocusFallbackTests
{
    [WindowsFact]
    public void AnUnknownTerminalDoesNotClaimThatItWasFocused()
    {
        var focusCore = typeof(TerminalFocuser).GetMethod(
            "FocusCore", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: new[] { typeof(SessionStatus) }, modifiers: null);

        Assert.NotNull(focusCore);
        var focused = focusCore.Invoke(null, new object[]
        {
            new SessionStatus { TermProgram = "unrecognised-terminal" }
        });

        Assert.False(Assert.IsType<bool>(focused));
    }
}
