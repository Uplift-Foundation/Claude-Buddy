using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ClaudeBuddy.Tests;

// Covers the half of BackgroundJobs that decides *whose* listing to read.
//
// `claude agents` answers for one Claude Code config directory — one account,
// one daemon — and a machine running a second account via CLAUDE_CONFIG_DIR has
// a second daemon whose jobs the first listing does not mention. Not "mentions
// as finished": does not mention, which is indistinguishable here from a
// session that was never a job at all.
//
// That distinction is the bug. A background job read as not-a-job takes
// SessionPresence.ShapeOf down the Terminal branch, and the chat panel then
// tells the user to reply in a terminal that does not exist, because a daemon
// runs the job precisely so that none has to.
//
// Both fixtures below are trimmed from real `claude agents --json` output taken
// off one machine at one moment — the default account and a second account at
// ~/.claude-board — rather than written from memory, for the reason the sibling
// file states: an invented fixture would have agreed with the bug.
public class BackgroundJobsAccountsTests
{
    // The default account. Note what is *not* here: no row for e4f5c5e4, the
    // session whose panel started this. Its process was alive throughout.
    private const string DefaultAccount = """
    [
      {
        "pid": 86418,
        "id": "b1425d42",
        "cwd": "/Users/user/Source/Claude-Buddy",
        "kind": "background",
        "sessionId": "9f1880fe-581c-4b09-99c3-8bc4f8381f55",
        "name": "red",
        "status": "busy",
        "state": "working"
      },
      {
        "pid": 71665,
        "cwd": "/Users/user/Documents/GTD/Evidence",
        "kind": "interactive",
        "sessionId": "90ba5d91-bbbb-4e73-9802-af04cc7c49f7",
        "name": "evidence-cleanup",
        "status": "idle"
      }
    ]
    """;

    // The second account, read with CLAUDE_CONFIG_DIR=~/.claude-board. e4f5c5e4
    // is "makayla-case" — a background job, finished — and 3520d459 is one that
    // is merely parked, so the two are not the same answer once found.
    private const string BoardAccount = """
    [
      {
        "pid": 85810,
        "id": "e4f5c5e4",
        "cwd": "/Users/user/Documents/GTD/Evidence",
        "kind": "background",
        "sessionId": "e4f5c5e4-dc8d-4a3a-b7a0-39e01160ab82",
        "name": "makayla-case",
        "status": "idle",
        "state": "done"
      },
      {
        "pid": 95902,
        "id": "3520d459",
        "cwd": "/Users/user/Documents/GTD/Evidence",
        "kind": "background",
        "sessionId": "3520d459-5204-4d21-8575-9586e5c8f3e7",
        "name": "resume-background-session",
        "status": "idle",
        "state": "blocked"
      }
    ]
    """;

    private const string Makayla = "e4f5c5e4-dc8d-4a3a-b7a0-39e01160ab82";
    private const string Parked = "3520d459-5204-4d21-8575-9586e5c8f3e7";

    private static Dictionary<string, string>? Merged =>
        BackgroundJobs.Merge(
            BackgroundJobs.Parse(DefaultAccount), BackgroundJobs.Parse(BoardAccount));

    // --- the bug, stated ---------------------------------------------------

    // What the app did before: one account's listing, read successfully, with
    // the other account's job simply not in it. "Absent from a listing we read"
    // is the strongest negative this class has, so a working job read as
    // never-having-been-one.
    [Fact]
    public void SecondAccountsJobIsInvisibleToTheDefaultAccountsListingAlone()
    {
        var alone = BackgroundJobs.Parse(DefaultAccount);

        Assert.Equal(JobPhase.NotAJob, BackgroundJobs.Phase(alone, Makayla));
        Assert.False(BackgroundJobs.IsLive(alone, Makayla));
    }

    // A CLI session in a terminal Buddy has no way to address: no tmux pane,
    // and a TERM_PROGRAM that is neither iTerm2 nor Terminal.app. Since CB-79
    // the note names the terminal it found, so the fixture has to have one.
    private static readonly SessionStatus InAnUnknownTerminal = new()
    {
        Cli = "claude",
        TermProgram = "its terminal",
    };

    // And the consequence, in the vocabulary the user actually sees: a job with
    // no terminal classified as a session that has one.
    [Fact]
    public void MisreadJobTakesTheTerminalBranchAndIsToldToReplyInATerminal()
    {
        var alone = BackgroundJobs.Parse(DefaultAccount);
        var shape = SessionPresence.ShapeOf(new SessionStatus(), BackgroundJobs.Phase(alone, Makayla));

        Assert.Equal(LocalSessionShape.Terminal, shape);
        Assert.Equal(
            "its terminal isn't a terminal Buddy can type into without bringing it forward. "
                + "tmux, iTerm2, Terminal.app, kitty and WezTerm are the ones it can. "
                + "Reply in the terminal instead.",
            LocalCliChatSession.NoPaneNote(InAnUnknownTerminal, shape, onMacOS: true, onWindows: false));
    }

    // --- the fix -----------------------------------------------------------

    // Merged, the same session is what it always was: a background job, and a
    // finished one.
    [Fact]
    public void MergedListingFindsTheSecondAccountsJob()
    {
        Assert.Equal(JobPhase.Done, BackgroundJobs.Phase(Merged, Makayla));
        Assert.False(BackgroundJobs.IsLive(Merged, Makayla));
    }

    // The other row of the same account, to show the merge carries state rather
    // than merely presence: parked is not finished, and the two draw differently.
    [Fact]
    public void MergedListingKeepsEachRowsOwnState()
    {
        Assert.Equal(JobPhase.Parked, BackgroundJobs.Phase(Merged, Parked));
        Assert.True(BackgroundJobs.IsLive(Merged, Parked));
    }

    // And the composer says the thing that is true of a job with no terminal,
    // which is the whole visible point of the change.
    [Fact]
    public void MergedListingTakesTheBackgroundBranch()
    {
        var shape = SessionPresence.ShapeOf(new SessionStatus(), BackgroundJobs.Phase(Merged, Makayla));

        Assert.Equal(LocalSessionShape.Background, shape);
        Assert.Contains("background job", LocalCliChatSession.NoPaneNote(InAnUnknownTerminal, shape, onMacOS: true, onWindows: false));
    }

    // The default account's own rows are untouched by having a second one.
    [Fact]
    public void MergingDoesNotDisturbTheAccountReadFirst()
    {
        Assert.Equal(
            JobPhase.Working, BackgroundJobs.Phase(Merged, "9f1880fe-581c-4b09-99c3-8bc4f8381f55"));
    }

    // --- Merge -------------------------------------------------------------

    // A null on either side is "there was no listing", and it has to swallow the
    // whole answer. A partial merge would be a confident verdict about an
    // account nobody managed to ask — which is the bug again, one account
    // narrower.
    [Fact]
    public void EitherSideUnreadableMakesTheWholeAnswerUnknown()
    {
        var real = BackgroundJobs.Parse(BoardAccount);

        Assert.Null(BackgroundJobs.Merge(null, real));
        Assert.Null(BackgroundJobs.Merge(real, null));
        Assert.Null(BackgroundJobs.Merge(null, null));

        // ...and Unknown is what downstream sees, not NotAJob.
        Assert.Equal(JobPhase.Unknown, BackgroundJobs.Phase(BackgroundJobs.Merge(real, null), Makayla));
        Assert.True(BackgroundJobs.IsLive(BackgroundJobs.Merge(real, null), Makayla));
    }

    // The account read first wins a collision. Only two short job ids from
    // different daemons can collide at all, and nothing here could settle such a
    // tie on the merits — so it is settled the same way every time.
    [Fact]
    public void FirstAccountWinsAKeyCollision()
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal) { ["abcd1234"] = "working" };
        var second = new Dictionary<string, string>(StringComparer.Ordinal) { ["abcd1234"] = "done" };

        Assert.Equal("working", BackgroundJobs.Merge(first, second)!["abcd1234"]);
    }

    // Merging answers with a new map rather than editing one of its arguments:
    // the caller folds over several accounts, and a merge that wrote into the
    // accumulator would make the order of the fold observable elsewhere.
    [Fact]
    public void MergeLeavesItsArgumentsAlone()
    {
        var first = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "working" };
        var second = new Dictionary<string, string>(StringComparer.Ordinal) { ["b"] = "done" };

        var merged = BackgroundJobs.Merge(first, second)!;

        Assert.Equal(2, merged.Count);
        Assert.Single(first);
        Assert.Single(second);
    }

    [Fact]
    public void MergingAnEmptyListingKeepsTheOther()
    {
        var real = BackgroundJobs.Parse(BoardAccount);
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Equal(JobPhase.Done, BackgroundJobs.Phase(BackgroundJobs.Merge(real, empty), Makayla));
        Assert.Equal(JobPhase.Done, BackgroundJobs.Phase(BackgroundJobs.Merge(empty, real), Makayla));
    }

    // --- ExtraAccountDirs --------------------------------------------------

    private const string Home = "/home/someone";

    private static List<string> Extras(params string[] configured) =>
        BackgroundJobs.ExtraAccountDirs(Home, configured);

    // The ordinary machine: one account, so nothing extra to ask and no second
    // subprocess per scan to pay for it.
    [Fact]
    public void NoConfiguredAccountsMeansNoExtraLaunches()
    {
        Assert.Empty(Extras());
    }

    [Fact]
    public void ConfiguredAccountResolvesUnderHome()
    {
        Assert.Equal(new[] { Path.Combine(Home, ".claude-board") }, Extras(".claude-board"));
    }

    // ~/.claude is held out because Read has already asked it — by path, so that
    // naming it explicitly (which the settings UI permits) doesn't buy a second
    // launch for an answer already in hand.
    [Fact]
    public void DefaultAccountIsNeverAskedTwice()
    {
        Assert.Empty(Extras(".claude"));
        Assert.Equal(new[] { Path.Combine(Home, ".claude-board") }, Extras(".claude", ".claude-board"));
    }

    [Fact]
    public void RepeatedEntriesAreAskedOnce()
    {
        Assert.Equal(
            new[] { Path.Combine(Home, ".claude-board") },
            Extras(".claude-board", ".claude-board"));
    }

    // One account reached under two capitalizations is one account — Windows
    // paths are case-insensitive, and this list is shared with the installer,
    // which people type into by hand.
    [Fact]
    public void CapitalizationDoesNotBuyASecondLaunch()
    {
        Assert.Empty(Extras(".CLAUDE"));
        Assert.Single(Extras(".claude-board", ".Claude-Board"));
    }

    // A blank entry would resolve to $HOME, which is not a config directory at
    // all; whatever listing that produced would belong to nobody.
    [Fact]
    public void BlankEntriesAreSkippedRatherThanResolvingToHome()
    {
        Assert.Empty(Extras("", "   "));
        Assert.Equal(new[] { Path.Combine(Home, ".claude-board") }, Extras("", ".claude-board", "  "));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        Assert.Equal(new[] { Path.Combine(Home, ".claude-board") }, Extras("  .claude-board  "));
    }

    // Several accounts stay in the order they were configured, so the collision
    // rule above is stated over a stable sequence rather than a set.
    [Fact]
    public void ConfiguredOrderIsPreserved()
    {
        Assert.Equal(
            new[]
            {
                Path.Combine(Home, ".claude-board"),
                Path.Combine(Home, ".claude-work")
            },
            Extras(".claude-board", ".claude-work"));
    }

    // --- ExtraAccountDirs: the inherited CLAUDE_CONFIG_DIR (CB-114) ---------
    //
    // BackgroundJobs.Read's null read asks whatever account *this app's own
    // process* inherited, which is the default one on nearly every machine but
    // not always. These cover the matrix CB-114 asks for: unset, set to the
    // default account itself, set to some other account, and set to an account
    // already named in ClaudeCodeProfileDirs — each a distinct outcome for
    // de-duplication rather than a variation on one.
    //
    // Passed as a plain parameter rather than through
    // Environment.SetEnvironmentVariable, so none of these need the
    // ConfigDirEnv collection: ExtraAccountDirs stays pure, and the one place
    // that reads the real variable (BackgroundJobs.Read) is excluded from
    // coverage the same way ReadOne already is.

    private static List<string> ExtrasWithInherited(string? inherited, params string[] configured) =>
        BackgroundJobs.ExtraAccountDirs(Home, configured, inherited);

    // The bug itself: an app that inherited a non-default account has that
    // account's own directory covered by the null read already, but the
    // default account was never asked by anything — until now.
    [Fact]
    public void AnInheritedAccountBringsTheDefaultAccountBackIn()
    {
        Assert.Equal(
            new[] { Path.Combine(Home, ".claude") },
            ExtrasWithInherited(Path.Combine(Home, ".claude-board")));
    }

    // Unset is the ordinary, single-account machine, and behaves exactly as it
    // always did: the null read already reached the default account, so
    // nothing is added for it.
    [Fact]
    public void AnUnsetInheritedConfigDirAsksNothingExtraForTheDefaultAccount()
    {
        Assert.Empty(ExtrasWithInherited(null));
    }

    // Blank is the same as unset — Claude Code itself falls back to the
    // default account rather than treating "" as a real directory, and this
    // has to agree with that rather than asking for a listing that belongs to
    // nobody.
    [Fact]
    public void ABlankInheritedConfigDirIsTreatedAsUnset()
    {
        Assert.Empty(ExtrasWithInherited("   "));
    }

    // Set to the default account itself is the same case as unset, once
    // resolved — the null read already covers it and it must not be asked
    // twice.
    [Fact]
    public void AnInheritedConfigDirEqualToTheDefaultAsksNothingExtra()
    {
        Assert.Empty(ExtrasWithInherited(Path.Combine(Home, ".claude")));
    }

    // The inherited account can also be one of the configured extras — the
    // null read has already asked it by inheritance, so it must not appear a
    // second time just because it is also named in settings.
    [Fact]
    public void AnInheritedAccountAlreadyInTheConfiguredListIsNotAskedTwice()
    {
        Assert.Equal(
            new[] { Path.Combine(Home, ".claude") },
            ExtrasWithInherited(Path.Combine(Home, ".claude-board"), ".claude-board"));
    }

    // The default account, when it is added back at all, leads the configured
    // extras rather than landing wherever it happens to fall — so it never
    // moves around depending on how many other accounts are configured.
    [Fact]
    public void TheDefaultAccountLeadsTheExtrasWhenItIsAddedBack()
    {
        Assert.Equal(
            new[] { Path.Combine(Home, ".claude"), Path.Combine(Home, ".claude-work") },
            ExtrasWithInherited(Path.Combine(Home, ".claude-board"), ".claude-work"));
    }

    // Windows paths are case-insensitive, and the inherited value arrives
    // however the environment happened to spell it — it still has to collapse
    // onto the same account as one spelled differently in settings.
    [Fact]
    public void AnInheritedAccountsCapitalizationDoesNotBuyASecondLaunch()
    {
        Assert.Equal(
            new[] { Path.Combine(Home, ".claude") },
            ExtrasWithInherited(Path.Combine(Home, ".claude-board").ToUpperInvariant(), ".Claude-Board"));
    }

    // A trailing separator on the inherited value must not defeat the
    // de-duplication against the default account, which Path.Combine never
    // produces one of.
    [Fact]
    public void ATrailingSeparatorOnTheInheritedValueIsTrimmed()
    {
        Assert.Empty(ExtrasWithInherited(Path.Combine(Home, ".claude") + "/"));
    }
}
