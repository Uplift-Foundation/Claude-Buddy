using Xunit;

namespace Orbweaver.Tests;

// The decisions behind "what does the speaker read", with no speech engine,
// no settings file and no subprocess anywhere near them.
//
// This is the whole reason SpeechPlan was lifted out of ChatPanel.SpeakLatest.
// The utterance itself is excluded from coverage — TextToSpeech.Speak makes the
// machine make a noise — so a choice left inside the method that utters is a
// choice nothing can assert.
public class SpeechPlanTests
{
    private static string Long(int chars) => new('x', chars);

    // --- nothing to say ---

    // Three ways a reply can be absent, all of which have to answer the same
    // way in both modes. A summary mode that asked a model to summarise an
    // empty string would spend seconds to produce nothing.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void NothingToSayIsSilentInBothModes(string? text)
    {
        Assert.True(SpeechPlan.For(text, SpeakScope.Full).Silent);

        var summary = SpeechPlan.For(text, SpeakScope.Summary);
        Assert.True(summary.Silent);
        Assert.False(summary.NeedsSummary);
    }

    // --- full ---

    // The existing behaviour, which the default has to keep exactly: the whole
    // text, untouched, and no round trip.
    [Fact]
    public void FullModeSpeaksTheWholeReplyAndAsksNobody()
    {
        var reply = Long(SpeechPlan.ShortEnoughChars * 3);

        var plan = SpeechPlan.For(reply, SpeakScope.Full);

        Assert.Equal(reply, plan.Text);
        Assert.False(plan.NeedsSummary);
        Assert.False(plan.Silent);
    }

    // Full mode does not trim, because it does not measure. Asserted so the
    // trimming in the summary arm is visibly a consequence of measuring rather
    // than a general tidy-up somebody can move.
    [Fact]
    public void FullModeDoesNotTouchTheText()
    {
        const string padded = "  the reply  ";

        Assert.Equal(padded, SpeechPlan.For(padded, SpeakScope.Full).Text);
    }

    // --- summary ---

    // The case the mode exists for.
    [Fact]
    public void ALongReplyInSummaryModeNeedsSummarising()
    {
        var plan = SpeechPlan.For(Long(SpeechPlan.ShortEnoughChars + 1), SpeakScope.Summary);

        Assert.True(plan.NeedsSummary);
        Assert.False(plan.Silent);
    }

    // A reply already about as long as its own summary would be. Summarising it
    // buys several seconds of silence in exchange for nothing, which is exactly
    // how the mode would come to read as broken on short replies.
    [Fact]
    public void AShortReplyInSummaryModeIsSpokenAsItIs()
    {
        var reply = Long(SpeechPlan.ShortEnoughChars);

        var plan = SpeechPlan.For(reply, SpeakScope.Summary);

        Assert.False(plan.NeedsSummary);
        Assert.Equal(reply, plan.Text);
    }

    // Both sides of the boundary in one case, because an off-by-one here is the
    // difference between "at the threshold" and "one past it" and neither reads
    // wrong on its own.
    [Fact]
    public void TheThresholdIsInclusive()
    {
        Assert.False(SpeechPlan.For(Long(SpeechPlan.ShortEnoughChars), SpeakScope.Summary).NeedsSummary);
        Assert.True(SpeechPlan.For(Long(SpeechPlan.ShortEnoughChars + 1), SpeakScope.Summary).NeedsSummary);
    }

    // Padding is not length. A short reply wrapped in blank lines is still a
    // short reply, and measuring it untrimmed would send it on a round trip it
    // does not need.
    [Fact]
    public void WhitespacePaddingDoesNotMakeAReplyLong()
    {
        var padded = "\n\n\n" + Long(SpeechPlan.ShortEnoughChars) + "\n\n\n";

        var plan = SpeechPlan.For(padded, SpeakScope.Summary);

        Assert.False(plan.NeedsSummary);
        Assert.Equal(Long(SpeechPlan.ShortEnoughChars), plan.Text);
    }
}

// The two pure halves of producing the summary: what gets asked, and what is
// done with the answer. The subprocess between them is excluded and covered by
// the seam in the UI suite.
public class SpeechSummaryTextTests
{
    // --- the prompt ---

    // The instruction has to say the output is *heard*. Every constraint in it
    // exists because the alternative is spoken aloud: a preamble is wasted
    // audio, markdown is read as punctuation.
    [Fact]
    public void TheInstructionAsksForSpeakableProse()
    {
        var instruction = SpeechSummary.Instruction();

        Assert.Contains("two or three sentences", instruction);
        Assert.Contains("read aloud", instruction);
        Assert.Contains("No preamble", instruction);
        Assert.Contains("standard input", instruction);
    }

    // Reply is the default kind — the negative control for the variant below.
    [Fact]
    public void TheInstructionDefaultsToTheReplyKind()
    {
        Assert.Equal(SpeechSummary.Instruction(), SpeechSummary.Instruction(SpeechSummaryKind.Reply));
    }

    // CB-167's vibe summary: a different question from the reply summary
    // above — "what's next" rather than "what did it say" — because this is
    // what gets spoken instead of a chime when a turn finishes, and knowing
    // whether to come back is the whole reason to prefer it over a Glass
    // sound.
    [Fact]
    public void TheTurnFinishedInstructionAsksWhatWasDoneAndWhatsNext()
    {
        var instruction = SpeechSummary.Instruction(SpeechSummaryKind.TurnFinished);

        Assert.Contains("what's next", instruction);
        Assert.Contains("two or three short sentences", instruction);
        Assert.Contains("at most three sentences", instruction);
        Assert.Contains("about 40 words in total", instruction);
        Assert.Contains("read aloud", instruction);
        Assert.Contains("No preamble", instruction);
    }

    // The two kinds ask different questions — what would fail if TurnFinished
    // silently reused the Reply instruction with the kind ignored.
    [Fact]
    public void TheTwoKindsProduceDifferentInstructions()
    {
        Assert.NotEqual(
            SpeechSummary.Instruction(SpeechSummaryKind.Reply),
            SpeechSummary.Instruction(SpeechSummaryKind.TurnFinished));
    }

    // It travels as a command-line argument, so it stays on one line: a
    // newline in an argument is the one thing whose handling differs between
    // a direct exec and a Windows command shim.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheInstructionIsOneLine(bool turnFinished)
    {
        var kind = turnFinished ? SpeechSummaryKind.TurnFinished : SpeechSummaryKind.Reply;

        Assert.DoesNotContain('\n', SpeechSummary.Instruction(kind));
    }

    // The truncation is mid-sentence by design, so the model is told it may
    // be — told nothing, it asked the user instead of summarising.
    [Fact]
    public void TheInstructionSaysTheTextMayBeCutOff()
    {
        Assert.Contains("ends abruptly", SpeechSummary.Instruction());
    }

    // What goes on stdin is the reply and nothing else. The regression this
    // pins was the instruction riding along on stdin, where Claude Code frames
    // it as pasted content the model should not obey.
    [Fact]
    public void TheSourceIsTheReplyAloneWithNoInstructionInIt()
    {
        Assert.Equal("the assistant said something", SpeechSummary.Source("the assistant said something"));
    }

    // A very long reply is exactly what this mode is for, but its tail adds
    // little to three sentences and costs latency on the slow leg.
    [Fact]
    public void AVeryLongReplyIsTruncatedBeforeBeingSent()
    {
        var huge = new string('y', SpeechSummary.MaxSourceChars * 2);

        Assert.Equal(SpeechSummary.MaxSourceChars, SpeechSummary.Source(huge).Length);
    }

    // The negative control for the case above: a reply under the bound is sent
    // whole. Without this, a Source that truncated everything would pass.
    [Fact]
    public void AReplyUnderTheBoundIsSentWhole()
    {
        var reply = new string('y', SpeechSummary.MaxSourceChars - 1);

        Assert.Equal(reply, SpeechSummary.Source(reply));
    }

    // --- cleaning the answer ---

    [Fact]
    public void AnOrdinarySummaryIsPassedThrough()
    {
        Assert.Equal(
            "The retry logic moved into its own class. Tests were added.",
            SpeechSummary.Clean("The retry logic moved into its own class. Tests were added."));
    }

    // Told not to write a preamble; sometimes writes one anyway. Only stripped
    // when it is short, ends in a colon and has something after it.
    [Fact]
    public void AShortPreambleLineIsRemoved()
    {
        Assert.Equal(
            "The retry logic moved.",
            SpeechSummary.Clean("Summary:\nThe retry logic moved."));
    }

    // A summary whose own first sentence contains a colon is not a preamble,
    // and losing it would lose the summary's opening. This is the arm that
    // makes the rule above safe.
    [Fact]
    public void ARealSentenceEndingInAColonSurvives()
    {
        const string text = "One thing changed and it is this: the timeout is per attempt now.";

        Assert.Equal(text, SpeechSummary.Clean(text));
    }

    // Told not to write code; a model that fenced its prose anyway would have
    // the fence spoken as backticks.
    [Fact]
    public void AFencedBlockIsUnwrapped()
    {
        Assert.Equal(
            "The retry logic moved.",
            SpeechSummary.Clean("```\nThe retry logic moved.\n```"));
    }

    // Paragraph breaks are not information once spoken — engines pause at them
    // as if a new topic had started.
    [Fact]
    public void ParagraphBreaksAreCollapsedToSingleSpaces()
    {
        Assert.Equal(
            "First sentence. Second sentence.",
            SpeechSummary.Clean("First sentence.\n\n\nSecond sentence."));
    }

    // Nothing usable came back. Null rather than an empty string, so the caller
    // takes the stated-failure path instead of speaking silence.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("```\n\n```")]
    public void NothingUsableAnswersNull(string? output)
    {
        Assert.Null(SpeechSummary.Clean(output));
    }

    // Stated, not silent, and not the full reply — a user who chose this mode
    // to avoid a five-minute reading is not helped by being given one.
    [Fact]
    public void TheFailureAnswerSaysSomething()
    {
        Assert.False(string.IsNullOrWhiteSpace(SpeechSummary.Unavailable));
    }
}

// What actually gets spoken once the summariser has had its turn — every arm
// of it, including the ones that only happen when something goes wrong.
//
// This exists as its own class because it drives the seam, which is
// process-wide: the disposal below has to put it back or a later class in this
// assembly gets this one's fake summariser.
[Collection("SpeechSummarySeam")]
public class SpeechSummaryOutcomeTests : IDisposable
{
    public void Dispose() => SpeechSummary.SummarizerForTests = null;

    private static void Answer(Func<string, Task<string?>> summarizer) =>
        SpeechSummary.SummarizerForTests = summarizer;

    // The ordinary case, and the negative control for every failure below:
    // without it, an implementation that always answered Unavailable would
    // satisfy all of them.
    [Fact]
    public async Task AWorkingSummariserIsWhatGetsSpoken()
    {
        Answer(_ => Task.FromResult<string?>("Three files changed and the tests pass."));

        Assert.Equal(
            "Three files changed and the tests pass.",
            await SpeechSummary.SummarizeOrSayWhyAsync("a very long reply"));
    }

    // The reply reaches the summariser intact. Without this, a summariser being
    // handed the wrong text would still pass every other case here.
    [Fact]
    public async Task TheReplyIsWhatTheSummariserIsAskedAbout()
    {
        string? seen = null;
        Answer(reply => { seen = reply; return Task.FromResult<string?>("ok"); });

        await SpeechSummary.SummarizeOrSayWhyAsync("the original reply");

        Assert.Equal("the original reply", seen);
    }

    // Nothing usable came back. Stated, not silent — a speaker that says
    // nothing is indistinguishable from a broken one.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NothingUsableIsSaidRatherThanSwallowed(string? answer)
    {
        Answer(_ => Task.FromResult(answer));

        Assert.Equal(SpeechSummary.Unavailable, await SpeechSummary.SummarizeOrSayWhyAsync("reply"));
    }

    // The CLI's own words for an account over its spend limit, as captured
    // from a real run, alongside a failure that names nothing — the negative
    // control, without which a FailureSentence that always matched would pass.
    [Theory]
    [InlineData("You've hit your org's monthly spend limit · run /usage-credits to raise it, or visit claude.ai/admin-settings/usage · your weekly limit resets 10pm (America/Los_Angeles)", SpeechSummary.SpendLimitReached)]
    [InlineData("\nSPEND LIMIT exceeded", SpeechSummary.SpendLimitReached)]
    [InlineData("Error: connection refused", null)]
    [InlineData(null, null)]
    public void AFailedRunIsNamedOnlyWhenItSaysWhy(string? output, string? expected)
    {
        Assert.Equal(expected, SpeechSummary.FailureSentence(output));
    }

    // A failure that already knows its sentence is spoken as that sentence,
    // not collapsed into Unavailable with every other failure.
    [Fact]
    public async Task ASpendLimitIsSaidAsSuch()
    {
        Answer(_ => throw new SpeechSummary.SpokenFailureException(SpeechSummary.SpendLimitReached));

        Assert.Equal(SpeechSummary.SpendLimitReached, await SpeechSummary.SummarizeOrSayWhyAsync("reply"));
    }

    // A summariser that threw. Same answer as any other failure, and critically
    // it does not propagate: this is awaited from a UI thread, and an exception
    // escaping would take the panel with it.
    [Fact]
    public async Task AThrowingSummariserFailsToASentenceRatherThanUpwards()
    {
        Answer(_ => throw new InvalidOperationException("no CLI here"));

        Assert.Equal(SpeechSummary.Unavailable, await SpeechSummary.SummarizeOrSayWhyAsync("reply"));
    }

    // The same, for a faulted task rather than a synchronous throw — the shape
    // a real async failure actually arrives in.
    [Fact]
    public async Task AFaultedSummariserTaskAlsoFailsToASentence()
    {
        Answer(_ => Task.FromException<string?>(new TimeoutException("took too long")));

        Assert.Equal(SpeechSummary.Unavailable, await SpeechSummary.SummarizeOrSayWhyAsync("reply"));
    }

    // Deliberately *not* the full reply. Somebody who chose summary mode chose
    // it to avoid a five-minute reading, and handing them one because the
    // summariser failed is the opposite of what they asked for. This is the
    // assertion that stops a future "helpful" fallback being added.
    [Fact]
    public async Task AFailureNeverFallsBackToReadingTheWholeReply()
    {
        var reply = new string('x', 10_000);
        Answer(_ => Task.FromResult<string?>(null));

        var spoken = await SpeechSummary.SummarizeOrSayWhyAsync(reply);

        Assert.NotEqual(reply, spoken);
        Assert.True(spoken.Length < 200);
    }

    // CB-167's entry point: the two-argument overload SpeechRequest.
    // SpeakTurnSummary calls. The seam itself is kind-agnostic (it never
    // builds a prompt), so this proves the overload reaches the same working
    // path as the reply-kind default rather than a parallel one nobody
    // exercises.
    [Fact]
    public async Task TheTurnFinishedOverloadReachesTheSameSeamAsTheDefault()
    {
        Answer(_ => Task.FromResult<string?>("Fixed the bug and pushed it."));

        Assert.Equal(
            "Fixed the bug and pushed it.",
            await SpeechSummary.SummarizeOrSayWhyAsync("a very long reply", SpeechSummaryKind.TurnFinished));
    }
}

// Whose account a summary is billed to (CB-248): the session's own, read off
// where its transcript lives, and handed to the child as CLAUDE_CONFIG_DIR —
// named for an extra account, unset for the default one, left alone when
// nothing could be worked out.
//
// Paths are built with Path.Combine rather than written out, so every case
// means the same thing on both CI legs.
[Collection("SpeechSummarySeam")]
public class SpeechSummaryAccountTests : IDisposable
{
    public void Dispose() => SpeechSummary.AccountSummarizerForTests = null;

    private static readonly string Home = Path.Combine(Path.GetTempPath(), "cb248-home");

    private static string Transcript(string accountDir, params string[] below) =>
        Path.Combine(new[] { accountDir, "projects", "K--some-project" }.Concat(below).ToArray());

    private static bool Exists(string _) => true;

    // --- AccountDirFor --------------------------------------------------------

    [Fact]
    public void ATranscriptsAccountIsTheParentOfItsProjectsDirectory()
    {
        var account = Path.Combine(Home, ".claude-work");
        Assert.Equal(account, SpeechSummary.AccountDirFor(Transcript(account, "abc.jsonl"), Exists));
    }

    // A subagent's transcript sits two levels further down, under the parent
    // session's own directory; it still belongs to the same account.
    [Fact]
    public void ASubagentsTranscriptBelongsToTheSameAccount()
    {
        var account = Path.Combine(Home, ".claude");
        var path = Transcript(account, "abc", "subagents", "agent-1.jsonl");
        Assert.Equal(account, SpeechSummary.AccountDirFor(path, Exists));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoTranscriptMeansNoAccount(string? path) =>
        Assert.Null(SpeechSummary.AccountDirFor(path, Exists));

    // Codex keeps its rollouts under ~/.codex/sessions, with no `projects`
    // above them: that is not a Claude account, and must not be guessed at.
    [Fact]
    public void ATranscriptWithNoProjectsAboveItIsNotAClaudeAccount()
    {
        var path = Path.Combine(Home, ".codex", "sessions", "2026", "10", "06", "rollout-x.jsonl");
        Assert.Null(SpeechSummary.AccountDirFor(path, Exists));
    }

    // A WSL session's Linux path seen from Windows names a root that is not
    // there. Naming it would start the CLI in a fresh, logged-out context.
    // The paired positive is the first case above, same shape, root present.
    [Fact]
    public void ARootThatDoesNotExistOnThisMachineIsNotUsed()
    {
        var account = Path.Combine(Home, ".claude-work");
        Assert.Null(SpeechSummary.AccountDirFor(Transcript(account, "abc.jsonl"), _ => false));
    }

    // The default is the real filesystem, so a temp tree on disk is enough to
    // prove the default arm is Directory.Exists and not something looser.
    [Fact]
    public void TheDefaultExistenceCheckIsTheRealFilesystem()
    {
        var account = Directory.CreateTempSubdirectory("cb248-account-").FullName;
        try
        {
            Assert.Equal(account, SpeechSummary.AccountDirFor(Transcript(account, "abc.jsonl")));
        }
        finally
        {
            Directory.Delete(account, recursive: true);
        }

        Assert.Null(SpeechSummary.AccountDirFor(Transcript(account, "abc.jsonl")));
    }

    // --- StartInfoFor ---------------------------------------------------------

    [Fact]
    public void AnExtraAccountIsNamedToTheChild()
    {
        var account = Path.Combine(Home, ".claude-work");
        var startInfo = SpeechSummary.StartInfoFor("claude", accountDir: account, home: Home);
        Assert.Equal(account, startInfo.Environment["CLAUDE_CONFIG_DIR"]);
    }

    // Unset, not named and not inherited. Named would be CB-42's different
    // context; inherited would bill whichever account Buddy was launched under.
    // Where the test run itself has CLAUDE_CONFIG_DIR set — a developer on a
    // second account — this is also the proof that an inherited value is
    // actively removed rather than merely not added.
    [Theory]
    [InlineData(".claude")]
    [InlineData(".claude/")]
    [InlineData(".CLAUDE")]
    public void TheDefaultAccountIsUnsetEvenWhenSpelledDifferently(string spelling)
    {
        var startInfo = SpeechSummary.StartInfoFor(
            "claude", accountDir: Path.Combine(Home, spelling), home: Home);
        Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    // Unknown leaves the child exactly as it was before this ticket.
    [Fact]
    public void AnUnknownAccountLeavesTheInheritedEnvironmentAlone()
    {
        var startInfo = SpeechSummary.StartInfoFor("claude", accountDir: null, home: Home);
        Assert.Equal(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var value) ? value : null);
    }

    // --- the seam carries it --------------------------------------------------

    [Fact]
    public async Task TheAccountReachesTheSummariser()
    {
        string? seen = "not called";
        SpeechSummary.AccountSummarizerForTests = (_, account) =>
        {
            seen = account;
            return Task.FromResult<string?>("Done.");
        };

        var account = Path.Combine(Home, ".claude-work");
        var spoken = await SpeechSummary.SummarizeOrSayWhyAsync("a reply", SpeechSummaryKind.Reply, account);

        Assert.Equal("Done.", spoken);
        Assert.Equal(account, seen);
    }
}

// CB-254: the turn-finished cue is capped by sentence count and length, the
// reply summary is not.
[Collection("SpeechSummarySeam")]
public class SpeechSummaryShortenTests : IDisposable
{
    public void Dispose() => SpeechSummary.SummarizerForTests = null;

    private static string S(string t, int sentences = 3, int chars = 400) => SpeechSummary.Shorten(t, sentences, chars);

    [Fact]
    public void ReplyInstructionStillAsksForTwoOrThreeSentences() =>
        Assert.Contains("two or three sentences", SpeechSummary.Instruction(SpeechSummaryKind.Reply));

    [Fact]
    public void TextWithinBothLimitsIsUnchanged() =>
        Assert.Equal("Done. Next up.", S("  Done. Next up.  "));

    [Fact]
    public void OnlyTheFirstThreeSentencesAreKept() =>
        Assert.Equal("A b. C d! E f?", S("A b. C d! E f? G h. I j. K l."));

    [Fact]
    public void ExactlyThreeSentencesAreKept() =>
        Assert.Equal("A. B. C.", S("A. B. C."));

    [Fact]
    public void DecimalsDoNotSplit() =>
        Assert.Equal("Volume is 0.5 now. Fine. Ok.", S("Volume is 0.5 now. Fine. Ok. Extra."));

    [Fact]
    public void AbbreviationsDoNotSplit() =>
        Assert.Equal("Use e.g. a flag, i.e. this one. Next. Last.", S("Use e.g. a flag, i.e. this one. Next. Last. Four."));

    [Fact]
    public void AnEllipsisDoesNotSplit() =>
        Assert.Equal("Hmm... still going. Two. Three.", S("Hmm... still going. Two. Three. Four."));

    [Fact]
    public void ACapMidSentenceCutsAtTheLastSentenceEnd() =>
        Assert.Equal("One two.", S("One two. Three four five six seven", 3, 25));

    [Fact]
    public void WithNoSentenceEndInsideTheCapItCutsAtTheLastWord() =>
        Assert.Equal("alpha beta", S("alpha beta gamma delta", 3, 12));

    [Fact]
    public void WithNoSpacesAtAllItCutsHardAtTheCap() =>
        Assert.Equal("abcde", S("abcdefghij", 3, 5));

    [Fact]
    public void ASixSentenceSummaryOfRealisticLengthComesOutAsThree() =>
        Assert.Equal("Fixed the bug. Pushed it. Tests pass.",
            S("Fixed the bug. Pushed it. Tests pass. CI is green. PR is open. Waiting on review. Nothing else."));

    [Fact]
    public void FileNamesAndVersionsDoNotSplit() =>
        Assert.Equal("Edited a.cs and v1.2 in 3.5 seconds. Two. Three.", S("Edited a.cs and v1.2 in 3.5 seconds. Two. Three. Four."));

    [Fact]
    public void TextExactlyAtTheCapIsUnchanged() =>
        Assert.Equal("abcdefghij", S("abcdefghij", 3, 10));

    [Fact]
    public void OneCharOverTheCapIsCut() =>
        Assert.Equal("abcd", S("abcd efghi", 3, 9));

    [Fact]
    public void AFirstSentenceLongerThanTheCapFallsToTheWordCut() =>
        Assert.Equal("aaa bbb", S("aaa bbb ccc ddd. Next.", 3, 9));

    [Fact]
    public void AnyWhitespaceIsAWordBoundary() =>
        Assert.Equal("aaa", S("aaa\tbbb ccc", 3, 6));

    [Fact]
    public void TheHardCutDoesNotSplitASurrogatePair() =>
        Assert.Equal("ab", S("ab\U0001F600cd", 3, 3));

    [Fact]
    public void UnspacedCjkIsCutHardAtTheCap() =>
        Assert.Equal("\u3053\u3093\u306B", S("\u3053\u3093\u306B\u3061\u306F", 3, 3));

    [Fact]
    public async Task AFiveHundredCharReplyIsByteIdentical()
    {
        var five = string.Join(" ", Enumerable.Repeat("This is a fairly long reply sentence for the test.", 10));
        SpeechSummary.SummarizerForTests = _ => Task.FromResult<string?>(five);
        Assert.Equal(five, await SpeechSummary.SummarizeOrSayWhyAsync("x", SpeechSummaryKind.Reply));
    }

    [Fact]
    public void ACloserAfterTheStopStillEndsTheSentence() =>
        Assert.Equal("He said \"done.\" Then \"next.\" Then \"last.\"",
            S("He said \"done.\" Then \"next.\" Then \"last.\" Then \"more.\" Then \"x.\""));

    [Fact]
    public void ABracketCloserAfterTheStopStillEndsTheSentence() =>
        Assert.Equal("Fixed it (see foo.) Next (bar.) Third (baz.)",
            S("Fixed it (see foo.) Next (bar.) Third (baz.) Fourth (qux.) Fifth."));

    [Fact]
    public void ACloserFollowedByMoreTextIsNotASentenceEnd() =>
        Assert.Equal("a \"bb.\"c dd. Ee. Ff.", S("a \"bb.\"c dd. Ee. Ff. Gg."));

    [Fact]
    public void InitialsLikeUSDoNotSplit() =>
        Assert.Equal("Built the U.S. release. Shipped to the U.S. Army. Done.",
            S("Built the U.S. release. Shipped to the U.S. Army. Done. Extra."));

    [Fact]
    public void AParenthesisedAbbreviationDoesNotSplit() =>
        Assert.Equal("Fixed (e.g. this) now. Two. Three.", S("Fixed (e.g. this) now. Two. Three. Four."));

    [Fact]
    public void ALoneLetterBeforeTheStopStillEndsTheSentence() =>
        Assert.Equal("Take plan B. Then C. Then D.", S("Take plan B. Then C. Then D. Then E."));

    [Fact]
    public void ADigitBeforeTheStopIsNotAnInitial() =>
        Assert.Equal("Version 2. Two. Three.", S("Version 2. Two. Three. Four."));

    [Fact]
    public void AWordCutNeverEndsMidWordOnTabsOrNewlines() =>
        Assert.Equal("alpha\tbeta\nalpha", S("alpha\tbeta\nalpha\tbeta\n", 3, 20));

    [Fact]
    public void AWindowEndingExactlyOnAWordKeepsTheWord() =>
        Assert.Equal("alpha beta", S("alpha beta gamma", 3, 10));

    [Fact]
    public void AnUnpunctuatedBulletListIsBoundedByTheCap() =>
        Assert.Equal("one two", S("one two three four", 3, 9));

    [Fact]
    public void ASixOrSevenSentenceFixtureInTheRealRangeComesOutAsThree()
    {
        var raw = "Fixed the bug in the parser. Added tests for it. The suite is green. "
            + "Pushed the branch to origin. Opened a pull request. CI is running now. Waiting on review.";
        Assert.InRange(raw.Length, 150, 470);
        Assert.Equal("Fixed the bug in the parser. Added tests for it. The suite is green.", S(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespaceBecomesEmpty(string? text) =>
        Assert.Equal("", SpeechSummary.Shorten(text, 3, 400));

    [Fact]
    public async Task TurnFinishedIsShortenedButReplyIsNot()
    {
        var many = string.Join(" ", Enumerable.Repeat("Words go here.", 8));
        SpeechSummary.SummarizerForTests = _ => Task.FromResult<string?>(many);

        var turn = await SpeechSummary.SummarizeOrSayWhyAsync("x", SpeechSummaryKind.TurnFinished);
        var reply = await SpeechSummary.SummarizeOrSayWhyAsync("x", SpeechSummaryKind.Reply);

        Assert.Equal("Words go here. Words go here. Words go here.", turn);
        Assert.Equal(many, reply);
    }

    [Fact]
    public async Task FailureSentencesAreNotTouchedForTurnFinished()
    {
        SpeechSummary.SummarizerForTests = _ => Task.FromResult<string?>(null);
        Assert.Equal(SpeechSummary.Unavailable,
            await SpeechSummary.SummarizeOrSayWhyAsync("x", SpeechSummaryKind.TurnFinished));

        var longMsg = new string('a', 400);
        SpeechSummary.SummarizerForTests = _ => throw new SpeechSummary.SpokenFailureException(longMsg);
        Assert.Equal(longMsg,
            await SpeechSummary.SummarizeOrSayWhyAsync("x", SpeechSummaryKind.TurnFinished));
    }
}
