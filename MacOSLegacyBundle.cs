namespace ClaudeBuddy
{
    // Moves a verified-ours `Claude Buddy.app` left beside `Orbweaver.app` to
    // the Trash, so a login item pointing at it cannot bring the old build
    // back (CB-255 §5).
    //
    // **STUB.** This file is a placeholder so Startup.Run's `retireLegacy`
    // step has something to call while the rest of phase 2 is built; unit F
    // replaces the whole file with the real Decide/Run. The contract it has to
    // keep is fixed now, because Program.cs already depends on it: Run is
    // static, takes nothing, never throws, does nothing off macOS, and is a
    // no-op under the test suites' environment overrides.
    internal static class MacOSLegacyBundle
    {
        // Stub: intentionally empty until unit F lands. No-throw by construction.
        internal static void Run()
        {
        }
    }
}
