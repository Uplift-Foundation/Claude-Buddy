namespace Orbweaver
{
    // Every name this app goes by, in one place.
    //
    // **Phase 2 of the rename flips the names a user's disk, config and OS
    // already hold, and this file is where it flips them.** "Claude" is
    // somebody else's trademark, so the app is now Orbweaver — but the old name
    // is not just a label. It is a folder on every user's disk, a hook path
    // written into their Claude Code and Codex settings, a mutex another copy
    // of the app checks for, a bundle id macOS ties permissions to. Phase 1
    // (CB-250) collected every one of those into this file without changing a
    // byte; phase 2 (CB-255) changes the values below and adds the migrations
    // each change calls for.
    //
    // A migration has to know what it is migrating *from*, which is what
    // Brand.Legacy is: the names that shipped before the flip, kept so every
    // compatibility arm — claim the old mutex too, move the old data folder,
    // watch the old status folder, strip the old hook entries — reads its
    // string from one place, and so the cleanup after phase 3 is one grep
    // (`Brand.Legacy.`) and one pass. Those values are history, not choices:
    // BrandTests pins them to the old strings, and a drift there means a
    // migration silently stops recognising the thing it exists to move.
    //
    // Every value is still pinned byte for byte by BrandTests. A test failing
    // there means a user-visible name, a path or an identifier changed — which
    // is exactly the change that has to come with a migration, not slip in
    // with a refactor.
    //
    // What deliberately is *not* flipped here, or not here at all:
    //
    // - SpeechEngineName and AssemblyName. Both are binary names rather than
    //   names a user's disk holds: the speech engine's is its <AssemblyName>,
    //   its release zip's stem and the exe inside every speech-engine folder
    //   already downloaded, and the app's follows the csproj. Flipping either
    //   constant without the project it names buys a 404 or a tray icon that
    //   resolves to nothing. They move with the binaries in phase 3.
    // - MacBundleId. Automation and Accessibility consent are tied to it, and
    //   it is never renamed, phase 3 included.
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
        public const string DisplayName = "Orbweaver";

        // The short form the app uses for itself mid-sentence ("isn't a terminal
        // Orbweaver can type into"). The same as DisplayName on purpose: there
        // is no natural short form of Orbweaver, and inventing one ("Weaver",
        // "Orb") would put a second new name in front of users who have only
        // just learned the first. Kept as its own constant so a short form, if
        // one ever earns its place, is still a one-line change.
        internal const string ShortName = "Orbweaver";

        // The folder under the per-user app data directory (settings, logs, the
        // bundle cache) on both platforms. Also the stem of temp folders.
        internal const string DataDirName = "Orbweaver";

        // The folder under the temp directory the hooks write status files
        // into. Shared with the hook scripts, which spell it out themselves.
        internal const string StatusFolderName = "orbweaver";

        // The folder the hook scripts are installed into under ~/.claude and
        // ~/.codex, and the app's short machine-readable name.
        internal const string Slug = "orbweaver";

        // The hook script, per platform. Its full path is written into the
        // user's Claude Code and Codex settings, so a rename has to rewrite
        // those entries rather than leave them pointing at nothing.
        internal const string HookScriptPowerShell = "OrbweaverHook.ps1";
        internal const string HookScriptShell = "OrbweaverHook.sh";

        // One per user across sessions (CB-206). The new name on its own would
        // let an old build and a new one run side by side during an upgrade —
        // the old one only knows Legacy.SingleInstanceMutexName — so the app
        // claims both and holds both (Program.cs, SingleInstance.ClaimAll).
        internal const string SingleInstanceMutexName = "Orbweaver_SingleInstance_Mutex";

        // The neural speech engine's executable and release-asset stem. Not
        // flipped in phase 2; see the header.
        internal const string SpeechEngineName = "ClaudeBuddySpeech";

        // The macOS bundle id. Automation and Accessibility consent are tied to
        // it, so it is never renamed casually — see tools/build-macos-app.sh.
        // Here only so the tccutil hints quote the same string the bundle has.
        internal const string MacBundleId = "io.github.wtvamp.claudebuddy";

        // The Win32 window class the global hotkey hook registers. Per process
        // and gone with the process, so it flips with no migration.
        internal const string HotkeyWindowClass = "OrbweaverGlobalHotkeyWindow";

        // The assembly name, which every avares:// resource URI starts with.
        // Not a free choice: it follows <AssemblyName> in the csproj, and
        // BrandTests checks the two agree so a rename can't break the tray icon
        // by changing one and not the other.
        internal const string AssemblyName = "Orbweaver";

        // The names that shipped before phase 2, for the migrations and
        // compatibility arms that have to recognise them. Pinned to the old
        // strings by BrandTests and never to be edited: they describe what is
        // already on users' disks, not a choice anybody gets to revisit. The
        // whole class goes in the cleanup after phase 3, once nothing still
        // needs to recognise an old install.
        internal static class Legacy
        {
            // Only for recognising text an old build wrote — the OpenClaw
            // mirror prefix in a room's history. Nothing displays it.
            internal const string DisplayName = "Claude Buddy";
            internal const string DataDirName = "ClaudeBuddy";
            internal const string StatusFolderName = "claude_buddy";
            internal const string Slug = "claude-buddy";
            internal const string HookScriptPowerShell = "ClaudeBuddyHook.ps1";
            internal const string HookScriptShell = "ClaudeBuddyHook.sh";
            internal const string SingleInstanceMutexName = "ClaudeBuddy_SingleInstance_Mutex";
            // The executable every build before phase 3 shipped as: ClaudeBuddy.exe
            // on Windows, Contents/MacOS/ClaudeBuddy in the macOS bundle.
            internal const string Executable = "ClaudeBuddy";
        }
    }
}
