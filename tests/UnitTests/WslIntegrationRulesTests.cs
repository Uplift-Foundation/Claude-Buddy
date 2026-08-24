using Xunit;

namespace ClaudeBuddy.Tests
{
    // The three decisions WslIntegration makes that are not registry reads or
    // subprocess calls.
    //
    // Worth reaching for a reason particular to this file: WSL exists only on
    // Windows, and every public entry point here returns early on anything else,
    // so on the macOS CI leg the file could never be more than its guards. Pure
    // string rules can be asserted on both legs — which matters more than usual
    // here, because a Windows-only path is the one a developer on a Mac cannot
    // check by running the app.
    //
    // None of the three fails loudly when it is wrong. A mis-composed UNC path
    // simply never exists, so the distro reads as un-wired and the Settings
    // window shows a toggle that does nothing; a mis-counted passwd field
    // returns somebody else's home directory, which plausibly exists.
    public class WslIntegrationRulesTests
    {
        // --- SettingsTextMentionsHook: what "wired" means ---

        // A text match rather than a JSON structure check, deliberately, so this
        // surface and the PowerShell installer can never disagree about whether a
        // distro is wired. That shared definition is what these cases pin.
        [Theory]
        [InlineData("""{"hooks":{"Stop":[{"command":"pwsh -File C:/x/ClaudeBuddyHook.ps1"}]}}""")]
        [InlineData("""{"hooks":{"Stop":[{"command":"pwsh -File C:/x/claudebuddyhook.PS1"}]}}""")]
        [InlineData("ClaudeBuddyHook.ps1")]
        public void SettingsMentioningTheHookCountAsWired(string text)
        {
            Assert.True(WslIntegration.SettingsTextMentionsHook(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("""{"hooks":{}}""")]
        [InlineData("""{"hooks":{"Stop":[{"command":"some-other-hook.ps1"}]}}""")]
        // The .sh twin is a different platform's hook and must not count: a WSL
        // distro wired for bash is not wired for this.
        [InlineData("""{"hooks":{"Stop":[{"command":"ClaudeBuddyHook.sh"}]}}""")]
        public void SettingsWithoutTheHookAreNotWired(string text)
        {
            Assert.False(WslIntegration.SettingsTextMentionsHook(text));
        }

        // --- SettingsPathCandidates: the two UNC spellings ---

        [Fact]
        public void TheHomeDirectoryBecomesAUncPathUnderTheDistro()
        {
            var (localhost, dollar) =
                WslIntegration.SettingsPathCandidates("Ubuntu", "/home/kmart");

            Assert.Equal(@"\\wsl.localhost\Ubuntu\home\kmart\.claude\settings.json", localhost);
            Assert.Equal(@"\\wsl$\Ubuntu\home\kmart\.claude\settings.json", dollar);
        }

        // The leading slash of a Linux home has to go, or the composed path gets
        // a doubled separator and never resolves.
        [Fact]
        public void TheLeadingSlashIsNotDoubledIntoTheUncPrefix()
        {
            var (localhost, _) = WslIntegration.SettingsPathCandidates("Ubuntu", "/root");

            Assert.Equal(@"\\wsl.localhost\Ubuntu\root\.claude\settings.json", localhost);
            Assert.DoesNotContain(@"Ubuntu\\", localhost);
        }

        // Any CLAUDE_CONFIG_DIR-style profile name works the same way — see
        // ClaudeBuddySettings.ClaudeCodeProfileDirs.
        [Fact]
        public void ANonDefaultProfileDirectoryIsHonoured()
        {
            var (localhost, dollar) =
                WslIntegration.SettingsPathCandidates("Debian", "/home/kmart", ".claude-work");

            Assert.EndsWith(@"\.claude-work\settings.json", localhost);
            Assert.EndsWith(@"\.claude-work\settings.json", dollar);
        }

        // Both spellings address the same file, differing only in the prefix.
        // The second is the older-build alias the PowerShell script also falls
        // back to, so they must stay in step.
        [Fact]
        public void BothSpellingsDifferOnlyInTheirPrefix()
        {
            var (localhost, dollar) =
                WslIntegration.SettingsPathCandidates("Ubuntu-22.04", "/home/a b");

            Assert.Equal(
                localhost.Replace(@"\\wsl.localhost\", ""), dollar.Replace(@"\\wsl$\", ""));
        }

        [Fact]
        public void ADeepHomeDirectoryKeepsEverySegment()
        {
            var (localhost, _) =
                WslIntegration.SettingsPathCandidates("Ubuntu", "/var/lib/nested/home");

            Assert.Equal(
                @"\\wsl.localhost\Ubuntu\var\lib\nested\home\.claude\settings.json", localhost);
        }

        // --- HomeFromPasswdLines: seven fields, home in the sixth ---

        private static readonly string[] Passwd =
        {
            "root:x:0:0:root:/root:/bin/bash",
            "daemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin",
            "kmart:x:1000:1000:Kawika Miller,,,:/home/kmart:/bin/zsh",
            "second:x:1001:1001::/home/second:/bin/sh",
        };

        [Theory]
        [InlineData(0u, "/root")]
        [InlineData(1u, "/usr/sbin")]
        [InlineData(1000u, "/home/kmart")]
        [InlineData(1001u, "/home/second")]
        public void TheHomeDirectoryComesFromTheSixthField(uint uid, string want)
        {
            Assert.Equal(want, WslIntegration.HomeFromPasswdLines(Passwd, uid));
        }

        // A uid with no account is null rather than a guess — the caller then
        // gives up on that distro instead of addressing a directory belonging to
        // somebody else.
        [Fact]
        public void AUidWithNoAccountIsNull()
        {
            Assert.Null(WslIntegration.HomeFromPasswdLines(Passwd, 4242));
        }

        [Fact]
        public void AnEmptyPasswdFileIsNull()
        {
            Assert.Null(WslIntegration.HomeFromPasswdLines(Array.Empty<string>(), 1000));
        }

        // The GECOS field routinely contains commas and can contain almost
        // anything; splitting on ':' is what keeps that from shifting the home
        // field along. This row has a full five-part GECOS.
        [Fact]
        public void CommasInTheNameFieldDoNotShiftTheHomeField()
        {
            var lines = new[] { "kmart:x:1000:1000:Kawika Miller,Room 1,555-0100,555-0101,note:/home/kmart:/bin/zsh" };

            Assert.Equal("/home/kmart", WslIntegration.HomeFromPasswdLines(lines, 1000));
        }

        // Rows that are not accounts are stepped over rather than ending the
        // walk: a comment, a blank line, and a truncated row all appear in real
        // files, and the account being looked for may come after them.
        [Fact]
        public void MalformedRowsAreSteppedOver()
        {
            var lines = new[]
            {
                "",
                "# a comment nobody should be parsing",
                "truncated:x:1000",
                "kmart:x:1000:1000::/home/kmart:/bin/zsh",
            };

            Assert.Equal("/home/kmart", WslIntegration.HomeFromPasswdLines(lines, 1000));
        }

        // A non-numeric uid field cannot match any uid, and must not throw on the
        // way past.
        [Fact]
        public void ANonNumericUidFieldIsSkipped()
        {
            var lines = new[] { "broken:x:notanumber:1000::/home/broken:/bin/sh" };

            Assert.Null(WslIntegration.HomeFromPasswdLines(lines, 1000));
        }

        // The first match wins. Two rows sharing a uid is a misconfigured system
        // rather than something to arbitrate, and picking the first is what
        // `getent` does.
        [Fact]
        public void TheFirstMatchingRowWins()
        {
            var lines = new[]
            {
                "first:x:1000:1000::/home/first:/bin/sh",
                "second:x:1000:1000::/home/second:/bin/sh",
            };

            Assert.Equal("/home/first", WslIntegration.HomeFromPasswdLines(lines, 1000));
        }
    }
}
