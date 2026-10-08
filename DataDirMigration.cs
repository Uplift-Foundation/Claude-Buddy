namespace ClaudeBuddy
{
    // Moves the per-user data and log folders from Brand.Legacy.DataDirName to
    // Brand.DataDirName on the first launch after the rename (CB-255 §2).
    //
    // **STUB.** This file is a placeholder so Startup.Run's `migrateUserData`
    // step has something to call while the rest of phase 2 is built; unit B
    // replaces the whole file with the real Plan/Run. The contract it has to
    // keep is fixed now, because Program.cs already depends on it: Run is
    // static, takes nothing, never throws, and does nothing at all while any
    // of CLAUDE_BUDDY_SETTINGS_DIR, CLAUDE_BUDDY_LOG_DIR or
    // CLAUDE_BUDDY_BUNDLE_ROOT is set — which every test suite does, so a test
    // can never move a developer's real folder.
    internal static class DataDirMigration
    {
        // Stub: intentionally empty until unit B lands. No-throw by construction.
        internal static void Run()
        {
        }
    }
}
