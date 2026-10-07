namespace ClaudeBuddy
{
    // Every name this app goes by, in one place.
    //
    // **This is the first step of renaming the app, and it renames nothing.**
    // "Claude" is somebody else's trademark, so the app is getting a new name —
    // but the old one is not just a label. It is a folder on every user's disk,
    // a hook path written into their Claude Code and Codex settings, a mutex
    // another copy of the app checks for, a bundle id macOS ties permissions
    // to. Each of those needs a migration, and a migration needs to know both
    // the old name and the new one. Scattered across sixty literals, nobody can
    // tell which is which; collected here, the rename is one diff against this
    // file plus the migrations it calls for.
    //
    // So every value below is the shipped string, byte for byte, and
    // BrandTests pins each one. A test failing here means a user-visible name,
    // a path or an identifier changed — which is exactly the change that has
    // to come with a migration, not slip in with a refactor.
    //
    // What deliberately is *not* here:
    //
    // - MachineNames' relay prefix. Nothing creates a relay any more; the
    //   prefix only recognises leftovers from builds that did, so it has to
    //   stay the old string forever, whatever this file says.
    // - The hook scripts, installers and build scripts. They are shell,
    //   PowerShell and Inno, and can't read a C# constant; they carry their
    //   own copies of the names below and are renamed alongside them.
    // - The CLAUDE_BUDDY_* environment variables. Almost all are test seams,
    //   and renaming one breaks every script that sets it — that is its own
    //   decision, not part of moving literals.
    // - The CLAUDEBUDDY_* ones (no underscore) that ChimePlayer and TextToSpeech
    //   hand to a user's own chime and speak commands. Those are a contract
    //   with scripts the user wrote, so they keep their names on purpose.
    public static class Brand
    {
        // What a person reads: window titles, the tray, menus, log prefixes.
        // Public, as is the class, only because App.axaml and OrbWindow.axaml
        // read it with x:Static, and Avalonia's XAML compiler resolves nothing
        // less than public there. The rest stays internal.
        public const string DisplayName = "Claude Buddy";

        // The short form the app uses for itself mid-sentence ("isn't a terminal
        // Buddy can type into"). A rename has to pick one too, or those sentences
        // keep the old name after everything around them has moved on.
        internal const string ShortName = "Buddy";

        // The folder under the per-user app data directory (settings, logs, the
        // bundle cache) on both platforms. Also the stem of temp folders.
        internal const string DataDirName = "ClaudeBuddy";

        // The folder under the temp directory the hooks write status files
        // into. Shared with the hook scripts, which spell it out themselves.
        internal const string StatusFolderName = "claude_buddy";

        // The folder the hook scripts are installed into under ~/.claude and
        // ~/.codex, and the app's short machine-readable name.
        internal const string Slug = "claude-buddy";

        // The hook script, per platform. Its full path is written into the
        // user's Claude Code and Codex settings, so a rename has to rewrite
        // those entries rather than leave them pointing at nothing.
        internal const string HookScriptPowerShell = "ClaudeBuddyHook.ps1";
        internal const string HookScriptShell = "ClaudeBuddyHook.sh";

        // One per user across sessions (CB-206). A new name here would let an
        // old build and a new one run side by side during an upgrade.
        internal const string SingleInstanceMutexName = "ClaudeBuddy_SingleInstance_Mutex";

        // The neural speech engine's executable and release-asset stem.
        internal const string SpeechEngineName = "ClaudeBuddySpeech";

        // The macOS bundle id. Automation and Accessibility consent are tied to
        // it, so it is never renamed casually — see tools/build-macos-app.sh.
        // Here only so the tccutil hints quote the same string the bundle has.
        internal const string MacBundleId = "io.github.wtvamp.claudebuddy";

        // The Win32 window class the global hotkey hook registers.
        internal const string HotkeyWindowClass = "ClaudeBuddyGlobalHotkeyWindow";

        // The assembly name, which every avares:// resource URI starts with.
        // Not a free choice: it follows <AssemblyName> in the csproj, and
        // BrandTests checks the two agree so a rename can't break the tray icon
        // by changing one and not the other.
        internal const string AssemblyName = "ClaudeBuddy";
    }
}
