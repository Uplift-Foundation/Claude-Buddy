namespace ClaudeBuddy
{
    // Retires the legacy hook-script folders (~/.claude/claude-buddy and its
    // Codex, Grok and Windows %LOCALAPPDATA% twins) once the installer has
    // marked them superseded long enough ago that no running session can
    // still be calling the old script (CB-255 §1).
    //
    // **STUB.** This file is a placeholder so Startup.Run's `retireLegacy`
    // step has something to call while the rest of phase 2 is built; unit C
    // replaces the whole file with the real Decide/Run. The contract it has to
    // keep is fixed now, because Program.cs already depends on it: Run is
    // static, takes nothing, never throws, and is a no-op under the test
    // suites' environment overrides.
    internal static class LegacyHookCleanup
    {
        // Stub: intentionally empty until unit C lands. No-throw by construction.
        internal static void Run()
        {
        }
    }
}
