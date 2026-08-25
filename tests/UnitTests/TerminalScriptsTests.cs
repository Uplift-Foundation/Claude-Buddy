using Xunit;

namespace ClaudeBuddy.Tests
{
    // The AppleScript handed to osascript, and the small pure rules around it.
    //
    // Nothing here runs a process — that is the whole reason this class exists
    // apart from TerminalFocuser. Which makes it the one part of the
    // click-an-orb path that can be checked at all, and it is the part where
    // being wrong is worst: the output is a script that selects a window and
    // sends keystrokes into a live terminal session, so a builder that drops a
    // clause does not show something wrong, it presses something somewhere else.
    //
    // The assertions are deliberately about *structure* rather than exact text.
    // Comparing the whole script against a golden string would fail on every
    // harmless reflow and say nothing about which clause went missing, and it is
    // the clauses that were bought with real bug reports.
    public class TerminalScriptsTests
    {
        // --- TmuxArgs: pinning the server ---

        // Several tmux servers can coexist (plain tmux, tmuxinator, a -L named
        // socket) and a pane id is only unique within one, so the socket has to
        // be pinned when the status file recorded it. Sending to the wrong server
        // means typing into somebody else's pane.
        [Fact]
        public void ARecordedSocketIsPinnedWithDashS()
        {
            var args = TerminalScripts.TmuxArgs("/tmp/tmux-501/default", "send-keys", "-t", "%30");

            Assert.Equal(
                new[] { "-S", "/tmp/tmux-501/default", "send-keys", "-t", "%30" }, args);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void WithNoSocketTheArgumentsAreUnchanged(string? socket)
        {
            var args = TerminalScripts.TmuxArgs(socket, "list-clients");

            Assert.Equal(new[] { "list-clients" }, args);
        }

        [Fact]
        public void PinningPreservesArgumentOrder()
        {
            var args = TerminalScripts.TmuxArgs("/s", "a", "b", "c", "d");

            Assert.Equal(new[] { "-S", "/s", "a", "b", "c", "d" }, args);
        }

        [Fact]
        public void AnEmptyArgumentListStillGetsTheSocket()
        {
            Assert.Equal(new[] { "-S", "/s" }, TerminalScripts.TmuxArgs("/s"));
        }

        // --- LeafOf: naming a Windows Terminal tab after its directory ---

        [Theory]
        [InlineData("/Users/warren/Source/Claude-Buddy", "Claude-Buddy")]
        [InlineData("C:\\Users\\warren\\Source\\Claude-Buddy", "Claude-Buddy")]
        [InlineData("/Users/warren/Source/Claude-Buddy/", "Claude-Buddy")]
        [InlineData("C:\\Source\\proj\\", "proj")]
        [InlineData("relative", "relative")]
        public void TheLeafIsTheLastPathSegment(string cwd, string want)
        {
            Assert.Equal(want, TerminalScripts.LeafOf(cwd));
        }

        // Both separators, because a status file can be written on either
        // platform and read on the same one — but a path with mixed separators
        // is what a WSL session produces, so the last of either wins.
        [Fact]
        public void EitherSeparatorEndsASegment()
        {
            Assert.Equal("deep", TerminalScripts.LeafOf("C:\\Users/warren\\Source/deep"));
        }

        // "C:" is a drive, not a directory anyone named, so it produces nothing
        // rather than a tab called "C:".
        [Theory]
        [InlineData("C:", "")]
        [InlineData("C:\\", "")]
        [InlineData("/", "")]
        [InlineData("", "")]
        public void ADriveOrRootNamesNothing(string cwd, string want)
        {
            Assert.Equal(want, TerminalScripts.LeafOf(cwd));
        }

        // --- EscapeForAppleScript: quoting dictated text ---

        // The order is the point. Escaping quotes first would then have their
        // new backslashes escaped again by the backslash pass, doubling them —
        // so backslashes go first.
        [Theory]
        [InlineData("plain text", "plain text")]
        [InlineData("say \"hello\"", "say \\\"hello\\\"")]
        [InlineData("a\\b", "a\\\\b")]
        [InlineData("a\\\"b", "a\\\\\\\"b")]
        public void QuotesAndBackslashesAreEscapedInThatOrder(string text, string want)
        {
            Assert.Equal(want, TerminalScripts.EscapeForAppleScript(text));
        }

        // The case that matters for a script being *built* rather than merely
        // displayed: an unescaped quote closes the AppleScript string literal
        // early, and everything after it is read as AppleScript. Escaping is
        // what stops dictated text from becoming instructions.
        [Fact]
        public void TextCannotCloseTheStringLiteralItIsPlacedIn()
        {
            var hostile = "\" & (do shell script \"rm -rf ~\") & \"";

            var escaped = TerminalScripts.EscapeForAppleScript(hostile);

            // Every quote in the result is preceded by a backslash, so none of
            // them terminates the literal.
            for (var i = 0; i < escaped.Length; i++)
            {
                if (escaped[i] != '"') continue;
                Assert.True(i > 0 && escaped[i - 1] == '\\', $"unescaped quote at {i}");
            }
        }

        // --- ActivateThenSettle: waiting for activation to land ---

        // This used to be `delay 0.2`, and it lost the race often enough to be
        // reported as "clicking an orb doesn't switch desktops any more":
        // `activate` was measured at 145ms, 167ms and 531ms on three consecutive
        // runs, so the spread was the problem rather than the average. Polling
        // `frontmost` waits exactly as long as it needs to.
        [Fact]
        public void ActivationIsPolledRatherThanWaitedOutWithAFixedDelay()
        {
            var script = TerminalScripts.ActivateThenSettle("iTerm");

            Assert.Contains("tell application \"iTerm\" to activate", script);
            Assert.Contains($"repeat {TerminalScripts.ActivationPollTicks} times", script);
            Assert.Contains("if frontmost of application \"iTerm\" then exit repeat", script);
            Assert.Contains("delay 0.05", script);
        }

        // Both the delay and the repeat sit *outside* the tell block on purpose:
        // inside one they are dispatched to the application, which does not
        // understand them, and the whole script fails with "Can't continue
        // delay". The one-line `tell ... to activate` form is what keeps them
        // out.
        [Fact]
        public void TheLoopIsNotInsideATellBlock()
        {
            var script = TerminalScripts.ActivateThenSettle("Terminal");

            Assert.DoesNotContain("end tell", script);
            Assert.Contains("to activate", script);
        }

        [Fact]
        public void TheAppNameIsTheOneAskedFor()
        {
            Assert.Contains("\"Terminal\"", TerminalScripts.ActivateThenSettle("Terminal"));
            Assert.DoesNotContain("iTerm", TerminalScripts.ActivateThenSettle("Terminal"));
        }

        // The ceiling is a backstop, not a timeout anyone should reach: if the
        // app never comes forward, selecting a window will fail anyway and
        // hanging the click is worse than trying and missing. Pinned so it stays
        // a couple of seconds rather than drifting into something a user waits
        // through.
        [Fact]
        public void TheActivationCeilingIsAboutTwoSeconds()
        {
            Assert.Equal(2000, TerminalScripts.ActivationPollTicks * 50);
        }

        // --- ITermSelectScript ---

        [Fact]
        public void TheITermScriptActivatesBeforeItSelects()
        {
            var script = TerminalScripts.ITermSelectScript("id", "abc-123");

            var activate = script.IndexOf("to activate", StringComparison.Ordinal);
            var select = script.IndexOf("select w", StringComparison.Ordinal);

            Assert.True(activate >= 0 && select >= 0);

            // Activate, *wait*, then select. The order is load-bearing and the
            // obvious experiment gives the wrong answer: tested from a desktop
            // with no terminal window on it, select-then-activate appears to
            // work, because activation alone switches Spaces there. From a
            // desktop that does have one, the same script needed two clicks.
            Assert.True(activate < select, "the script must activate before selecting");
        }

        [Fact]
        public void TheITermScriptMatchesOnTheGivenPropertyAndValue()
        {
            var script = TerminalScripts.ITermSelectScript("tty", "/dev/ttys004");

            Assert.Contains("if tty of s is \"/dev/ttys004\" then", script);
        }

        // The retry loop, which is the fix for orbs going to the wrong desktop on
        // the first click and the right one on the second. `frontmost of
        // application` flips true when the app becomes active, which is *before*
        // macOS has finished raising the window activation brings forward — so a
        // select landing in that gap is overruled a moment later.
        [Fact]
        public void TheITermSelectionIsReAssertedUntilItTakes()
        {
            var script = TerminalScripts.ITermSelectScript("id", "abc");

            Assert.Contains($"repeat {TerminalScripts.SelectionVerifyTicks} times", script);

            // Checked against the *window's* own answer. `id of current window`
            // was tried first and is useless: it is iTerm's internal notion of
            // current and reads as correct while a different window is on screen,
            // which is exactly the failure being chased.
            Assert.Contains("if frontmost of w then return", script);
        }

        // Deliberately not `set frontmost of w to true` alongside the select:
        // that was in the first version and measured worse than the code it
        // replaced — three failures in six against none.
        [Fact]
        public void TheITermScriptDoesNotSetFrontmostDirectly()
        {
            var script = TerminalScripts.ITermSelectScript("id", "abc");

            Assert.DoesNotContain("set frontmost of w to true", script);
        }

        [Fact]
        public void TheITermScriptSelectsWindowTabAndSessionTogether()
        {
            var script = TerminalScripts.ITermSelectScript("id", "abc");

            Assert.Contains("select w", script);
            Assert.Contains("select t", script);
            Assert.Contains("select s", script);
        }

        // --- TerminalSelectScript ---

        // Accepts either form the two paths produce: a bare "ttys004" from the
        // hook, or a "/dev/ttys004" client tty from tmux. Both have to end up
        // matching Terminal's own `tty of t`, which is the fully qualified one.
        [Theory]
        [InlineData("ttys004")]
        [InlineData("/dev/ttys004")]
        public void EitherFormOfTheTtyIsNormalisedToTheDevicePath(string tty)
        {
            var script = TerminalScripts.TerminalSelectScript(tty);

            Assert.Contains("if tty of t is \"/dev/ttys004\" then", script);
            Assert.DoesNotContain("\"/dev//dev/", script);
        }

        [Fact]
        public void TheTerminalScriptActivatesBeforeItSelects()
        {
            var script = TerminalScripts.TerminalSelectScript("ttys004");

            var activate = script.IndexOf("to activate", StringComparison.Ordinal);
            var select = script.IndexOf("set selected of t to true", StringComparison.Ordinal);

            Assert.True(activate >= 0 && select >= 0);
            Assert.True(activate < select, "the script must activate before selecting");
        }

        // Same Spaces rule as iTerm: re-assert until it takes, checked against
        // the window's own `frontmost`.
        [Fact]
        public void TheTerminalSelectionIsReAssertedUntilItTakes()
        {
            var script = TerminalScripts.TerminalSelectScript("ttys004");

            Assert.Contains($"repeat {TerminalScripts.SelectionVerifyTicks} times", script);
            Assert.Contains("if frontmost of w then return", script);
            Assert.Contains("set index of w to 1", script);
        }

        [Fact]
        public void TheSelectionCeilingIsUnderASecond()
        {
            Assert.Equal(600, TerminalScripts.SelectionVerifyTicks * 50);
        }

        // Both select scripts wrap their work in a tell block for the app whose
        // windows they are walking, and both close it. An unclosed tell block is
        // a syntax error osascript reports at run time, by which point the click
        // has already silently done nothing.
        [Fact]
        public void BothSelectScriptsOpenAndCloseTheirTellBlock()
        {
            foreach (var script in new[]
                     {
                         TerminalScripts.ITermSelectScript("id", "abc"),
                         TerminalScripts.TerminalSelectScript("ttys004"),
                     })
            {
                var opens = Occurrences(script, "tell application");
                var closes = Occurrences(script, "end tell");

                // One `tell ... to activate` one-liner needs no `end tell`, so
                // the block form is the difference between the two counts.
                Assert.Equal(opens - 1, closes);
            }
        }

        private static int Occurrences(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
                 i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }
}
