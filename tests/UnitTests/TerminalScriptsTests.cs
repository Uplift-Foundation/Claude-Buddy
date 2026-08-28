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

        // --- ShellQuote and TmuxAttachScript: attaching a detached server ---
        //
        // The script that answers a click on an agent-team member whose pane is
        // alive in a `claude-swarm-<pid>` socket nothing is attached to. Being
        // wrong here is the same shape as being wrong anywhere else in this file:
        // the output is executed, not displayed.

        // A directory with a space or an apostrophe has to arrive as one word,
        // and the shell's way to put a quote inside single quotes is to close,
        // escape and reopen. Getting this wrong splits the argument, and `cd`
        // then either fails or — worse — succeeds somewhere else.
        [Theory]
        [InlineData("/Users/warren/Source/Claude-Buddy", "'/Users/warren/Source/Claude-Buddy'")]
        [InlineData("/Users/warren/My Projects", "'/Users/warren/My Projects'")]
        [InlineData("/Users/warren/warren's", "'/Users/warren/warren'\\''s'")]
        [InlineData("", "''")]
        public void AnArgumentIsQuotedTheWayTheShellUnderstands(string value, string want)
        {
            Assert.Equal(want, TerminalScripts.ShellQuote(value));
        }

        [Fact]
        public void TheAttachScriptPinsTheServerAndExecsTheAttach()
        {
            var script = TerminalScripts.TmuxAttachScript(
                "/opt/homebrew/bin/tmux", "/tmp/tmux-501/claude-swarm-88341",
                "/Users/warren/Source/Claude-Buddy");

            Assert.StartsWith("#!/bin/sh\n", script);

            // The socket, pinned. A pane id is only unique within one server, and
            // a swarm socket is not the default one — attaching to the wrong
            // server would show somebody else's session.
            Assert.Contains("'-S' '/tmp/tmux-501/claude-swarm-88341'", script);

            // A plain attach, with no target: the caller has already run
            // select-window and select-pane against the pane it wants, so this
            // lands on the right teammate.
            Assert.EndsWith("'attach'\n", script);

            // exec, not a call: the window's shell becomes tmux rather than
            // waiting behind it.
            Assert.Contains("exec '/opt/homebrew/bin/tmux'", script);

            // The cd is for after the attach ends — detach or exit drops the
            // window to a shell, and the useful place to land is the directory
            // whose orb was clicked.
            Assert.Contains("cd '/Users/warren/Source/Claude-Buddy' || exit 1\n", script);
        }

        // No socket recorded means the default server, which `attach` finds on
        // its own. TmuxArgs already answers this and is reused rather than
        // re-decided, so the two can never disagree about when -S is passed.
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void WithNoSocketTheAttachDoesNotPinAServer(string? socket)
        {
            var script = TerminalScripts.TmuxAttachScript("/usr/bin/tmux", socket, "/tmp");

            Assert.DoesNotContain("-S", script);
            Assert.EndsWith("exec '/usr/bin/tmux' 'attach'\n", script);
        }

        // `cd ''` fails, and `|| exit 1` would then take the attach with it —
        // the one thing the script exists to do. A session with no cwd recorded
        // still gets its terminal.
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void WithNoCwdTheScriptIsJustTheAttach(string? cwd)
        {
            var script = TerminalScripts.TmuxAttachScript("/usr/bin/tmux", "/tmp/s", cwd);

            Assert.DoesNotContain("cd ", script);
            Assert.Equal("#!/bin/sh\nexec '/usr/bin/tmux' '-S' '/tmp/s' 'attach'\n", script);
        }

        // A directory with an apostrophe in it, end to end — the case the
        // quoting rule above exists for, asserted where it is actually used.
        [Fact]
        public void AnAwkwardDirectoryStillArrivesAsOneWord()
        {
            var script = TerminalScripts.TmuxAttachScript(
                "/usr/bin/tmux", null, "/Users/warren/warren's stuff");

            Assert.Contains("cd '/Users/warren/warren'\\''s stuff' || exit 1", script);
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

        // --- ChooseClient -----------------------------------------------------

        // The two-client state that exposed this, as a fixture: iTerm with two
        // tmux clients, one on the session holding the target pane and one on
        // another session. With a single client attached the distinction does not
        // exist, which is why "any client" survived until a machine had two — and
        // two clients are two windows of the same application, so choosing wrong
        // brings the wrong window to the front.
        private static TerminalScripts.TmuxClient Client(
            string tty, string session, string activity = "1000", bool control = false) =>
            new(tty, session, activity, control);

        // Already looking at the right session: that one, and no switch. Switching
        // it would be a no-op at best; switching a *different* client onto the
        // session would drag that one off whatever it was showing.
        [Fact]
        public void AClientAlreadyOnTheTargetSessionIsChosenAndNotSwitched()
        {
            var choice = TerminalScripts.ChooseClient(
                new[]
                {
                    Client("/dev/ttys009", "1", activity: "2000"),
                    Client("/dev/ttys002", "0", activity: "1000"),
                },
                targetSession: "0");

            Assert.Equal("/dev/ttys002", choice!.Value.Client.Tty);
            Assert.False(choice.Value.NeedsSwitch);
        }

        // ...even when the other client is the more recently active one, which is
        // the case that matters: recency only decides between clients that are all
        // equally wrong, and a client already on the session is right.
        [Fact]
        public void BeingOnTheSessionBeatsBeingMoreRecentlyActive()
        {
            var choice = TerminalScripts.ChooseClient(
                new[]
                {
                    Client("/dev/ttys002", "0", activity: "1000"),
                    Client("/dev/ttys009", "1", activity: "9999"),
                },
                targetSession: "0");

            Assert.Equal("/dev/ttys002", choice!.Value.Client.Tty);
            Assert.False(choice.Value.NeedsSwitch);
        }

        // Nobody on the session: the most recently active client, switched. A
        // person with several terminals open is working in the one they touched
        // last, so moving that one is the least surprising way to show them
        // something.
        [Fact]
        public void WithNobodyOnTheSessionTheMostRecentClientIsSwitched()
        {
            var choice = TerminalScripts.ChooseClient(
                new[]
                {
                    Client("/dev/ttys009", "1", activity: "1787874871"),
                    Client("/dev/ttys002", "2", activity: "1787875071"),
                },
                targetSession: "0");

            Assert.Equal("/dev/ttys002", choice!.Value.Client.Tty);
            Assert.True(choice.Value.NeedsSwitch);
        }

        // Two on the session: the more recently active of those, still no switch.
        [Fact]
        public void AmongSeveralOnTheSessionTheMostRecentWins()
        {
            var choice = TerminalScripts.ChooseClient(
                new[]
                {
                    Client("/dev/ttys002", "0", activity: "1000"),
                    Client("/dev/ttys009", "0", activity: "2000"),
                },
                targetSession: "0");

            Assert.Equal("/dev/ttys009", choice!.Value.Client.Tty);
            Assert.False(choice.Value.NeedsSwitch);
        }

        // Nothing attached at all: no client to choose, and the caller must not
        // invent one. Also a client row with no tty, which `list-clients` can
        // produce and which nothing downstream could aim at.
        [Fact]
        public void WithNoUsableClientThereIsNoChoice()
        {
            Assert.Null(TerminalScripts.ChooseClient(
                Array.Empty<TerminalScripts.TmuxClient>(), "0"));

            Assert.Null(TerminalScripts.ChooseClient(new[] { Client("", "0") }, "0"));
        }

        // Control mode travels with the choice, because the caller has to know:
        // an iTerm2 `-CC` client's tty belongs to a hidden control tab rather than
        // to any window worth looking at, so the per-tty selection is skipped for
        // it and the app is activated instead.
        [Fact]
        public void ControlModeTravelsWithTheChosenClient()
        {
            var choice = TerminalScripts.ChooseClient(
                new[] { Client("/dev/ttys002", "0", control: true) }, "0");

            Assert.True(choice!.Value.Client.ControlMode);
        }

        // --- PlacementFor -----------------------------------------------------

        // Round 6a in three cases. "It's taking me to a different tmux window -
        // not this one": every previous answer moved the user, and the complaint
        // was never about the destination.
        [Fact]
        public void AResolvedActiveWindowMeansSplitBesideTheUser()
        {
            Assert.Equal(
                TerminalScripts.AttachPlacement.BesideTheUser,
                TerminalScripts.PlacementFor("warren", "warren:3"));
        }

        // Attached somewhere, but the second lookup failed. A new window in their
        // own session is still inside the thing they use to move around, which is
        // what this app did before 6a — a consolation prize, not a wrong answer.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AClientWithNoResolvableWindowGetsAWindowOfItsOwn(string? activeWindow)
        {
            Assert.Equal(
                TerminalScripts.AttachPlacement.ItsOwnTmuxWindow,
                TerminalScripts.PlacementFor("warren", activeWindow));
        }

        // No client attached anywhere: a window created in a detached server is
        // the same nowhere the orb already pointed at, so neither tmux answer
        // applies and a terminal of its own is the only one left. Asserted for a
        // resolved window too, which cannot really happen — the window lookup is
        // asked of a session a client is on — because the rule must not depend on
        // that staying true.
        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData(null, "warren:3")]
        public void NothingAttachedMeansATerminalOfItsOwn(string? session, string? activeWindow)
        {
            Assert.Equal(
                TerminalScripts.AttachPlacement.ATerminalWindow,
                TerminalScripts.PlacementFor(session, activeWindow));
        }

        // --- TmuxSplitArgs / TmuxNewWindowArgs --------------------------------

        // The command is the last element and arrives untouched. tmux hands that
        // element to `sh -c`, so anything this builder did to it would be a
        // syntax error in a pane that just appeared — see the `sh -n` cases in
        // tests/IntegrationTests/TmuxAttachScriptTests.
        [Fact]
        public void TheSplitPutsTheCommandLastAndUnaltered()
        {
            const string command = "'/usr/bin/claude' attach '0e043819'";

            var args = TerminalScripts.TmuxSplitArgs(null, "warren:3", "/tmp/x", command);

            Assert.Equal(command, args[^1]);
        }

        // -h so the conversation lands beside their work rather than under it, and
        // -P -F '#{pane_id}' so the new pane's id comes back for the caller to
        // focus — the same contract new-window already had, and the reason neither
        // builder selects or raises anything itself.
        [Fact]
        public void TheSplitAsksForAHorizontalPaneAndItsId()
        {
            var args = TerminalScripts.TmuxSplitArgs(null, "warren:3", "/tmp/x", "cmd");

            Assert.Equal("split-window", args[0]);
            Assert.Contains("-h", args);
            Assert.Contains("-P", args);
            Assert.Contains("#{pane_id}", args);

            // Targeted at the window, not the session: that is the whole
            // difference between landing beside the user and landing wherever
            // their session last had current.
            var target = Array.IndexOf(args, "-t");
            Assert.Equal("warren:3", args[target + 1]);
        }

        // `-c ''` fails and would take the split with it, which is the same trap
        // TmuxAttachScript's `cd` guard exists for. Omitted, not passed empty.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NoCwdMeansNoDashC(string? cwd)
        {
            Assert.DoesNotContain("-c", TerminalScripts.TmuxSplitArgs(null, "warren:3", cwd, "cmd"));
            Assert.DoesNotContain("-c", TerminalScripts.TmuxNewWindowArgs(null, "warren", cwd, "cmd"));
        }

        [Fact]
        public void ACwdIsPassedThroughDashC()
        {
            var args = TerminalScripts.TmuxSplitArgs(null, "warren:3", "/tmp/x", "cmd");
            var at = Array.IndexOf(args, "-c");

            Assert.True(at > 0);
            Assert.Equal("/tmp/x", args[at + 1]);
        }

        // "<session>:" with the colon. Bare, tmux reads the target as a *window*
        // and refuses with "index N in use" the moment that index is taken; the
        // trailing colon names the session and lets it pick the next free index.
        // That was a real failure, which is why it is asserted rather than left to
        // a comment beside an argument list.
        [Fact]
        public void TheNewWindowTargetsTheSessionWithItsColon()
        {
            var args = TerminalScripts.TmuxNewWindowArgs(null, "warren", "/tmp/x", "cmd");

            Assert.Equal("new-window", args[0]);

            var target = Array.IndexOf(args, "-t");
            Assert.Equal("warren:", args[target + 1]);
        }

        // Both go through TmuxArgs, so both pin the socket when there is one and
        // pass nothing when there is not — the rule that keeps a swarm socket's
        // pane from being looked for on the default server.
        [Fact]
        public void BothBuildersPinTheSocketWhenGivenOne()
        {
            var split = TerminalScripts.TmuxSplitArgs(
                "/tmp/tmux-501/claude-swarm-1", "warren:3", "/tmp/x", "cmd");

            Assert.Equal("-S", split[0]);
            Assert.Equal("/tmp/tmux-501/claude-swarm-1", split[1]);
            Assert.Equal("split-window", split[2]);

            var window = TerminalScripts.TmuxNewWindowArgs(
                "/tmp/tmux-501/claude-swarm-1", "warren", "/tmp/x", "cmd");

            Assert.Equal("-S", window[0]);
            Assert.Equal("new-window", window[2]);
        }
    }
}
