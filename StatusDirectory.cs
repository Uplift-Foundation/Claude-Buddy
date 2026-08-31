using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeBuddy
{
    // Where the hooks leave a session's status file, and where Claude Buddy
    // looks for it.
    //
    // **These have to be the same directory, and on a launchd-started Mac they
    // were not.** The hooks run from the user's shell, where `TMPDIR` is set to
    // the per-user temp directory macOS gives every login session. Claude Buddy
    // asks .NET for `Path.GetTempPath()`, which on Unix reads `TMPDIR` and falls
    // back to `/tmp` when it is unset — and a launchd agent's environment does
    // not carry `TMPDIR` at all. `launchctl print` on the mini listed none, and
    // `/tmp/claude_buddy` did not exist.
    //
    // So a Buddy started by launchd read an empty directory, drew no orbs, and
    // answered every roster request from every other machine with "no sessions
    // here" — while two live Claude Code sessions sat in the real directory the
    // whole time. Nothing failed; the two halves were simply looking in
    // different places.
    //
    // That is the exact deployment the direct link exists for: a headless Mac,
    // always on, started by launchd, serving its sessions to the machine
    // somebody is actually sitting at.
    internal static class StatusDirectory
    {
        internal const string FolderName = "claude_buddy";

        // The name macOS gives the per-user temp directory, and what `TMPDIR`
        // holds in any ordinary shell. Asked of the C library rather than
        // guessed, because it contains a per-boot, per-user token.
        private const int DarwinUserTempDir = 65537;

        [DllImport("libc", SetLastError = true)]
        private static extern ulong confstr(int name, StringBuilder buf, ulong len);

        // Which temp root to use, given what the environment says and what the
        // platform can tell us.
        //
        // Pure so both arms are a test rather than a launchd job. The order is
        // the point: an explicit TMPDIR always wins, because a test or a second
        // instance sets exactly that to get its own sandbox — overruling it
        // would break every isolated run in this repository. Only when there is
        // none does the platform get asked, and only then does /tmp remain.
        internal static string Root(string? tmpdir, Func<string?> perUser, string fallback) =>
            !string.IsNullOrWhiteSpace(tmpdir) ? tmpdir!
            : perUser() is { Length: > 0 } mine ? mine
            : fallback;

        // Excluded from coverage: reads the process environment and calls libc.
        // What it decides is Root, which is pure.
        [ExcludeFromCodeCoverage]
        internal static string Path0() => Root(
            Environment.GetEnvironmentVariable("TMPDIR"),
            PerUserTemp,
            System.IO.Path.GetTempPath());

        // Excluded from coverage: a P/Invoke whose answer differs per machine
        // and per boot.
        [ExcludeFromCodeCoverage]
        private static string? PerUserTemp()
        {
            if (!OperatingSystem.IsMacOS()) return null;

            try
            {
                var buffer = new StringBuilder(1024);
                var written = confstr(DarwinUserTempDir, buffer, 1024);

                return written is > 0 and <= 1024 ? buffer.ToString() : null;
            }
            catch
            {
                // Any failure here means falling back to what .NET said, which
                // is the behaviour this replaces rather than something worse.
                return null;
            }
        }

        // The directory itself.
        [ExcludeFromCodeCoverage]
        internal static string Path() => System.IO.Path.Combine(Path0(), FolderName);
    }
}
