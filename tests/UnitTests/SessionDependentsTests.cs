using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers SessionDependents: what is running underneath a session's pid, asked
// before the one irreversible action in the app signals it.
//
// The whole of this file is the rule that CB-26 found missing. SessionTerminator
// claimed its pid could be neither the terminal the user is typing in nor the
// daemon; for the husk a backgrounded turn leaves behind it is both, and ending
// one on a real Mac killed the window a live background job was being read in.
//
// **The fixtures are real command lines, not invented ones.** The tree in
// ObservedHuskTree below is the one recorded in the ticket, read off `ps` on
// macOS 27.0 while a backgrounded turn was running — the same fixture-provenance
// rule the transcript suites keep, and for the same reason: a parser or a
// matcher written against a remembered command line agrees with itself and with
// nothing else.
public class SessionDependentsTests
{
    // --- what a command line is ---------------------------------------------

    // Both shapes SessionPresence.LooksLikeClaudeBinary already knows about,
    // asked through this file's own token walk — a plain `claude`, and the
    // versioned install path every agent-team member runs as.
    [Theory]
    [InlineData("claude")]
    [InlineData("claude --resume abc")]
    [InlineData("/Users/user/.local/share/claude/versions/2.1.246 --bg-pty-host")]
    [InlineData("claude.exe --session-id abc")]
    public void ACommandLineNamingTheClaudeBinaryIsRecognised(string command)
    {
        Assert.True(SessionDependents.NamesTheClaudeBinary(command));
    }

    // The Node forms, which are the Windows-inferred arm: argv[0] is the
    // interpreter and the token that names Claude is the script path. Not
    // reproduced on Windows, and here because leaving it out would make the
    // guard do nothing on the platform where not guarding costs the most.
    [Theory]
    [InlineData(@"node C:\Users\user\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js daemon run")]
    [InlineData(@"C:\Program Files\nodejs\node.exe C:\claude\claude.js --bg-pty-host")]
    public void ACommandLineRunningTheClaudeScriptThroughNodeIsRecognised(string command)
    {
        Assert.True(SessionDependents.NamesTheClaudeBinary(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    // A row whose command column is whitespace, which `ps` prints for a kernel
    // thread and for a process whose argv is unreadable. Not the same input as
    // the empty string: it survives the IsNullOrEmpty guard and tokenises to
    // nothing, which is the arm that would index token zero of an empty array.
    [InlineData("   ")]
    [InlineData("zsh -l")]
    [InlineData("node server.js")]
    // The trap this test caught the first draft falling into, and the reason the
    // binary test is asked of argv[0] rather than of every token. A path
    // *containing* claude is not a process running it, and
    // LooksLikeClaudeBinary is a filename test written for argv[0]: asked of a
    // shell's arguments it reads a user whose directory is called claude as
    // running the CLI. That was live for one build and one test run.
    [InlineData("/bin/zsh -c cd /Users/user/claude && ls")]
    [InlineData("/bin/zsh -c cd /Users/user/claude/versions/ && ls")]
    public void ACommandLineThatDoesNotNameTheBinaryIsNotRecognised(string? command)
    {
        Assert.False(SessionDependents.NamesTheClaudeBinary(command));
    }

    [Fact]
    public void TheDaemonIsTheTwoSubcommandTokensSideBySide()
    {
        Assert.True(SessionDependents.IsDaemon("claude daemon run"));
    }

    // The false-positive surface, which is the direction that matters: reading
    // an ordinary session as a daemon hides "End this session" from an orb that
    // could perfectly well be ended, with no explanation the user can act on.
    // Every line here mentions the word and none of them is the subcommand.
    [Theory]
    [InlineData("claude --resume /Users/user/daemon/run/transcript.jsonl")]
    [InlineData("claude daemon")]
    [InlineData("claude run daemon")]
    [InlineData("claude daemon --help run")]
    [InlineData("/usr/local/bin/mydaemon daemon run")]
    [InlineData("")]
    public void SomethingThatMerelyMentionsTheWordIsNotTheDaemon(string command)
    {
        Assert.False(SessionDependents.IsDaemon(command));
    }

    // Neither of the two processes the daemon is made of counts as a job. Both
    // arms are asserted rather than only the pty host, because the count they
    // guard is shown to a person: reading either as a job reports one more job
    // at stake than there is, on every machine, every time.
    [Fact]
    public void NeitherTheDaemonNorItsPtyHostIsAJob()
    {
        Assert.True(SessionDependents.IsPtyHost("claude --bg-pty-host"));
        Assert.False(SessionDependents.IsJobWorker("claude --bg-pty-host"));
        Assert.False(SessionDependents.IsJobWorker("claude daemon run"));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("claude daemon run")]
    [InlineData("zsh --bg-pty-host")]
    public void SomethingElseIsNotThePtyHost(string command)
    {
        Assert.False(SessionDependents.IsPtyHost(command));
    }

    // The job worker as `ps` printed it on the machine in the ticket.
    [Fact]
    public void AForkedBackgroundTurnIsAJobWorker()
    {
        Assert.True(SessionDependents.IsJobWorker(
            "claude --session-id b1425d42 --fork-session --resume "
            + "/Users/user/.claude/projects/-Users-user/6d3a9d57.jsonl"));
    }

    // Subagents are excluded, and this is the exclusion that changes the number
    // a person reads. A job that has spawned a team of four has one
    // conversation in it; counting its members would report five jobs at stake
    // and make the whole warning untrustworthy.
    [Theory]
    [InlineData("claude --agent-name MenuUX --team-name session-6a6fcb43 --parent-session-id 6a6fcb43-fa28")]
    [InlineData("claude --agent-id CatAudioSourcing@session-6a6fcb43")]
    [InlineData("claude --agent-name=Narrative")]
    [InlineData("claude --parent-session-id=6a6fcb43-fa28")]
    public void AnAgentTeamMemberIsNotCountedAsAJob(string command)
    {
        Assert.False(SessionDependents.IsJobWorker(command));
    }

    // `--agent-id` must not match `--agent-id-thing`, which is what the `=`
    // form's prefix test would do if it were a bare StartsWith.
    [Fact]
    public void AFlagIsMatchedWholeRatherThanAsAPrefix()
    {
        Assert.True(SessionDependents.IsJobWorker("claude --agent-identity-file /tmp/x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("node server.js")]
    public void SomethingThatIsNotClaudeAtAllIsNotAJobWorker(string command)
    {
        Assert.False(SessionDependents.IsJobWorker(command));
    }

    // --- the tree -------------------------------------------------------------

    // The process tree from the ticket, recorded on macOS 27.0 with a
    // backgrounded turn running, and written here bottom-up the way `ps` prints
    // it: 58306 is the husk in the user's tmux pane, and the daemon, its pty
    // host and the live job all hang below it.
    private static List<SessionDependents.ProcessRow> ObservedHuskTree() => new()
    {
        new(58306, 431, "claude"),
        new(86289, 58306, "claude daemon run"),
        new(86308, 86289, "claude --bg-pty-host"),
        new(86418, 86308,
            "claude --session-id b1425d42 --fork-session --resume "
            + "/Users/user/.claude/projects/-Users-user/6d3a9d57.jsonl"),
    };

    [Fact]
    public void TheHuskFromTheTicketIsRefusedAndNamesTheOneJobBelowIt()
    {
        var verdict = SessionDependents.Inspect(ObservedHuskTree(), 58306);

        Assert.True(verdict.DaemonBelow);
        Assert.Equal(1, verdict.JobsBelow);
        Assert.True(SessionDependents.BlocksTermination(verdict));
    }

    // The other half of the acceptance, and the half a guard is most likely to
    // break: an ordinary session with nothing underneath it ends exactly as it
    // does today. 431 is the husk's own parent — the shell — and it has the
    // daemon below it too, which is correct and deliberately not special-cased:
    // anything that is an ancestor of the daemon is the same hazard.
    [Fact]
    public void AnOrdinarySessionWithNothingUnderneathIsNotRefused()
    {
        var table = ObservedHuskTree();
        table.Add(new(9001, 431, "claude"));

        var verdict = SessionDependents.Inspect(table, 9001);

        Assert.Equal(SessionDependents.Nothing, verdict);
        Assert.False(SessionDependents.BlocksTermination(verdict));
    }

    // A session's own Claude children are not jobs. This is why the job count
    // walks from the daemon rather than from the pid: a lead with three team
    // members under it and no daemon anywhere is an ordinary session, and
    // counting claude-shaped descendants from the pid would have called it four
    // background jobs.
    [Fact]
    public void ASessionLeadingATeamIsStillAnOrdinarySession()
    {
        var table = new List<SessionDependents.ProcessRow>
        {
            new(100, 1, "claude"),
            new(101, 100, "claude --agent-name MenuUX --parent-session-id 6a6fcb43"),
            new(102, 100, "claude --agent-name Narrative --parent-session-id 6a6fcb43"),
            new(103, 100, "node /opt/mcp/server.js"),
        };

        Assert.Equal(SessionDependents.Nothing, SessionDependents.Inspect(table, 100));
    }

    // A daemon between turns. Still a refusal — the daemon is what the next
    // background turn is handed to — and the count is honestly zero, which is
    // why Explain has a sentence for it that does not contain the number.
    [Fact]
    public void ADaemonWithNothingRunningInItIsStillARefusal()
    {
        var table = new List<SessionDependents.ProcessRow>
        {
            new(200, 1, "claude"),
            new(201, 200, "claude daemon run"),
        };

        var verdict = SessionDependents.Inspect(table, 200);

        Assert.True(verdict.DaemonBelow);
        Assert.Equal(0, verdict.JobsBelow);
    }

    // Two accounts, two daemons, both below one husk — the arrangement
    // BackgroundJobs.Read already exists to describe, seen from the process
    // table instead of from a listing. Both subtrees are counted, and the
    // visited set is what keeps a process reachable through both from being
    // counted twice.
    [Fact]
    public void JobsUnderEveryDaemonBelowThePidAreCounted()
    {
        var table = new List<SessionDependents.ProcessRow>
        {
            new(300, 1, "claude"),
            new(301, 300, "claude daemon run"),
            new(302, 300, "claude daemon run"),
            new(303, 301, "claude --bg-pty-host"),
            new(304, 303, "claude --session-id aaa --fork-session --resume /t/a.jsonl"),
            new(305, 302, "claude --bg-pty-host"),
            new(306, 305, "claude --session-id bbb --fork-session --resume /t/b.jsonl"),
            new(307, 306, "claude --agent-name Helper --parent-session-id bbb"),
        };

        var verdict = SessionDependents.Inspect(table, 300);

        Assert.True(verdict.DaemonBelow);
        Assert.Equal(2, verdict.JobsBelow);
    }

    // A pid whose only relationship to the daemon is being *underneath* it — a
    // background job asked about its own pid. Nothing is below it, so ending it
    // is an ordinary end, which is exactly what the ticket says the honest
    // alternative is: end the job from the job's own orb.
    [Fact]
    public void AJobAskedAboutItselfHasNothingUnderneathIt()
    {
        Assert.Equal(
            SessionDependents.Nothing, SessionDependents.Inspect(ObservedHuskTree(), 86418));
    }

    // A process table is sampled row by row rather than snapshotted atomically,
    // so a pid recycled between two rows can name itself or an ancestor as its
    // parent. Both shapes below make an unguarded walk run forever, and this
    // test is the reason Descendants carries a visited set rather than a
    // comment saying it cannot happen.
    [Fact]
    public void ARowThatParentsItselfIsDroppedRatherThanWalked()
    {
        var table = new List<SessionDependents.ProcessRow>
        {
            new(400, 400, "claude daemon run"),
            new(401, 1, "claude"),
        };

        Assert.Equal(SessionDependents.Nothing, SessionDependents.Inspect(table, 400));
    }

    [Fact]
    public void ACycleBetweenTwoRowsTerminatesRatherThanHanging()
    {
        var table = new List<SessionDependents.ProcessRow>
        {
            new(500, 1, "claude"),
            new(501, 500, "claude daemon run"),
            new(502, 501, "claude --bg-pty-host"),
            new(501, 502, "claude daemon run"),
        };

        var verdict = SessionDependents.Inspect(table, 500);

        Assert.True(verdict.DaemonBelow);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void APidThatWasNeverRecordedHasNothingUnderneathIt(int pid)
    {
        Assert.Equal(SessionDependents.Nothing, SessionDependents.Inspect(ObservedHuskTree(), pid));
    }

    // A table that could not be read at all reaches Inspect as an empty list,
    // and answers "nothing underneath" rather than "refuse". Wrong-empty costs
    // exactly what the app already does; wrong-blocked would take a working
    // menu item away from every orb on the machine because `ps` did not answer
    // once.
    [Fact]
    public void AnUnreadableProcessTableDoesNotRefuseEverything()
    {
        Assert.Equal(
            SessionDependents.Nothing,
            SessionDependents.Inspect(new List<SessionDependents.ProcessRow>(), 58306));
    }

    // A row with no pid at all — what a malformed `ps` line would parse to if
    // one got past ParsePs — is dropped rather than made the parent of
    // everything whose parent column was also unreadable.
    [Fact]
    public void ARowWithNoPidIsDropped()
    {
        var table = new List<SessionDependents.ProcessRow>
        {
            new(0, 600, "claude daemon run"),
            new(600, 1, "claude"),
        };

        Assert.Equal(SessionDependents.Nothing, SessionDependents.Inspect(table, 600));
    }

    // --- what the menu says ----------------------------------------------------

    [Fact]
    public void AnOrdinarySessionGetsThePlainWording()
    {
        Assert.Equal("End this session", SessionDependents.Explain(SessionDependents.Nothing));
        Assert.StartsWith(
            "Stops the process behind this session",
            SessionDependents.ExplainTip(SessionDependents.Nothing));
    }

    // Three sentences because "0 jobs" is a real answer and must not read as
    // "this is safe", and because "1 background jobs" is the kind of wrong that
    // makes a reader distrust the number beside it.
    [Theory]
    [InlineData(0, "Can't end this: it is hosting the background daemon")]
    [InlineData(1, "Can't end this: it is your view of 1 background job")]
    [InlineData(4, "Can't end this: it is your view of 4 background jobs")]
    public void ARefusalSaysHowMuchIsAtStake(int jobs, string expected)
    {
        Assert.Equal(
            expected, SessionDependents.Explain(new SessionDependents.Verdict(true, jobs)));
    }

    // The tooltip names both halves of the ticket — what happens here, and what
    // would happen on Windows — because the second is the one a macOS user
    // cannot discover by trying it.
    [Fact]
    public void TheRefusalTipSaysWhatWouldBeLostOnBothPlatforms()
    {
        var tip = SessionDependents.ExplainTip(new SessionDependents.Verdict(true, 2));

        Assert.Contains("close that window while the work carries on", tip);
        Assert.Contains("on Windows would stop the background daemon", tip);
    }

    // --- `ps` ------------------------------------------------------------------

    // Real output, columns and all: `ps -eo pid=,ppid=,args=` right-aligns and
    // pads the two numeric columns, and the command is the rest of the line,
    // spaces included. Splitting it unbounded would hand back the first word and
    // make every matcher above answer false for every process.
    [Fact]
    public void ThePaddedThreeColumnFormIsParsedWithTheCommandIntact()
    {
        var rows = SessionDependents.ParsePs(
            "  58306   431 claude\n"
            + "  86289 58306 claude daemon run\n"
            + "  86418 86308 claude --session-id b1425d42 --fork-session --resume /t/6d3a9d57.jsonl\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(new SessionDependents.ProcessRow(58306, 431, "claude"), rows[0]);
        Assert.Equal("claude daemon run", rows[1].Command);
        Assert.Equal(86308, rows[2].ParentPid);
        Assert.EndsWith("/t/6d3a9d57.jsonl", rows[2].Command);
    }

    // Whatever a truncated pipe, a header row or a process with no argv leaves
    // behind is skipped rather than parsed into a row that names pid 0.
    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("  PID  PPID COMMAND\n")]
    [InlineData("  58306 431\n")]
    [InlineData("  notapid 431 claude\n")]
    [InlineData("  58306 notappid claude\n")]
    public void ALineThatIsNotThreeColumnsIsSkipped(string listing)
    {
        Assert.Empty(SessionDependents.ParsePs(listing));
    }

    // The two halves together, from the bytes `ps` would print to the verdict a
    // click acts on — because a parser that is right and a rule that is right
    // can still disagree about where one ends and the other begins.
    [Fact]
    public void ParsedOutputFeedsTheRuleEndToEnd()
    {
        var rows = SessionDependents.ParsePs(
            "  58306   431 claude\n"
            + "  86289 58306 claude daemon run\n"
            + "  86308 86289 claude --bg-pty-host\n"
            + "  86418 86308 claude --session-id b1425d42 --fork-session --resume /t/a.jsonl\n");

        var verdict = SessionDependents.Inspect(rows, 58306);

        Assert.True(SessionDependents.BlocksTermination(verdict));
        Assert.Equal(1, verdict.JobsBelow);
    }
}
