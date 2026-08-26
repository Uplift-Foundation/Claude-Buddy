using Xunit;

namespace ClaudeBuddy.Tests;

// What a session calls itself, read out of its own transcript, plus the two
// rules in SessionManager that decide who gets asked and what is done with the
// answer.
//
// This exists because a status file's title can be wrong forever. The hook
// writes one whenever something happens, so a session that goes quiet
// immediately after being named keeps whatever was caught at the time — and the
// case it was found in is the fixture below: a background job forked from an
// interactive session, whose status file was mtimed to the same second as the
// fork and recorded an empty title, while its transcript had carried
// "evidence (2)" since its second row. Two orbs wore the same two letters and
// nothing was ever going to fix it.
//
// The precedence asserted here is deliberately the hook's, not a new one. If
// the two disagreed, a session's orb would change identity depending on which
// of them happened to answer.
public class TranscriptIdentityTests
{
    // --- TranscriptIdentity.From ---------------------------------------------

    [Fact]
    public void ARenamedSessionIsReadFromItsCustomTitle()
    {
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"custom-title\",\"customTitle\":\"claude-buddy\",\"sessionId\":\"a\"}",
        });

        Assert.Equal("claude-buddy", identity.Title);
        Assert.Null(identity.Color);
    }

    [Fact]
    public void TheNewestRecordOfEachTypeWins()
    {
        // /rename and /color append rather than rewrite, so a transcript holds
        // every name a session has ever had and the last one is current.
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"custom-title\",\"customTitle\":\"first\"}",
            "{\"type\":\"agent-color\",\"agentColor\":\"red\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"second\"}",
            "{\"type\":\"agent-color\",\"agentColor\":\"green\"}",
        });

        Assert.Equal("second", identity.Title);
        Assert.Equal("green", identity.Color);
    }

    [Fact]
    public void AGeneratedTitleIsUsedOnlyWhenThereIsNoChosenOne()
    {
        var generatedOnly = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"ai-title\",\"aiTitle\":\"Package app with a tray\"}",
        });

        Assert.Equal("Package app with a tray", generatedOnly.Title);
    }

    [Fact]
    public void AChosenNameOutranksAGeneratedOneWrittenAfterIt()
    {
        // Claude Code keeps auto-naming a session after you have named it by
        // hand, so "most recent record" alone would let the generated name win
        // back a name the user chose.
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"custom-title\",\"customTitle\":\"job-lawyer\"}",
            "{\"type\":\"ai-title\",\"aiTitle\":\"Reviewing a demand letter\"}",
        });

        Assert.Equal("job-lawyer", identity.Title);
    }

    [Fact]
    public void MessageTextThatLooksLikeARecordIsNotOne()
    {
        // The anchored prefix is what makes this safe: a transcript is mostly
        // message text, and text inside a message is JSON-escaped, so only a
        // real record can begin this way.
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"user\",\"message\":{\"content\":"
            + "\"look at {\\\"type\\\":\\\"custom-title\\\",\\\"customTitle\\\":\\\"nope\\\"}\"}}",
            "  {\"type\":\"custom-title\",\"customTitle\":\"also not anchored\"}",
        });

        Assert.True(identity.IsEmpty);
        Assert.Null(identity.Title);
    }

    [Fact]
    public void AMalformedRecordIsSkippedRatherThanLosingALaterOne()
    {
        // The format belongs to somebody else, and one bad row is not a reason
        // to lose a name a later row carries.
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"custom-title\",\"customTitle\":\"truncated",
            "{\"type\":\"custom-title\",\"customTitle\":\"survivor\"}",
        });

        Assert.Equal("survivor", identity.Title);
    }

    [Theory]
    [InlineData("{\"type\":\"custom-title\"}")]                        // no key
    [InlineData("{\"type\":\"custom-title\",\"customTitle\":\"\"}")]   // empty
    [InlineData("{\"type\":\"custom-title\",\"customTitle\":\"   \"}")] // whitespace
    [InlineData("{\"type\":\"custom-title\",\"customTitle\":42}")]      // not a string
    [InlineData("{\"type\":\"custom-title\",\"customTitle\":null}")]
    public void ARecordWithNoUsableNameLeavesTheTitleUnset(string line)
    {
        Assert.Null(TranscriptIdentity.From(new[] { line }).Title);
    }

    [Fact]
    public void ANameKeepsThePunctuationAPersonTyped()
    {
        // A title is user text. The hook has to strip backslashes to protect
        // its own hand-rolled JSON; this parses, so it does not have to.
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"custom-title\",\"customTitle\":\"fix: the \\\"blank\\\" orb (2)\"}",
        });

        Assert.Equal("fix: the \"blank\" orb (2)", identity.Title);
    }

    // Letters only, exactly as the hook's `tr -cd 'a-zA-Z'` narrows it — which
    // is the point, since the two have to agree about a session's colour.
    //
    // Note what that means for "#00ff00": it survives as "ff", not as nothing.
    // That looked wrong when this test was first written and it is not — a
    // hex string is not a name the app can draw either way, so it fails the
    // colour lookup and the orb keeps its plain ring, which is what it does
    // today. Narrowing further here would be this file disagreeing with the
    // hook about the same bytes, and disagreement is the one outcome worth
    // avoiding: an orb would change colour depending on which of the two
    // answered.
    [Theory]
    [InlineData("green", "green")]
    [InlineData("  green  ", "green")]
    [InlineData("gr33n", "grn")]
    [InlineData("#00ff00", "ff")]
    [InlineData("000000", null)]          // nothing letter-shaped at all
    [InlineData("", null)]
    public void AColourIsNarrowedToLettersTheAppCanDraw(string value, string? expected)
    {
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"agent-color\",\"agentColor\":\"" + value + "\"}",
        });

        Assert.Equal(expected, identity.Color);
    }

    [Fact]
    public void AnUnusableRecordDoesNotEraseAGoodAnswerBeforeIt()
    {
        // "Newest wins" has to mean newest *usable*, for all three record
        // types. A truncated or empty row appended after a real one must not
        // take a name or a colour back off an orb that already had it — which
        // is the same instinct as never overwriting what the hook recorded,
        // one level down.
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"custom-title\",\"customTitle\":\"kept\"}",
            "{\"type\":\"agent-color\",\"agentColor\":\"teal\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"\"}",
            "{\"type\":\"agent-color\",\"agentColor\":\"000000\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"truncated",
        });

        Assert.Equal("kept", identity.Title);
        Assert.Equal("teal", identity.Color);
    }

    [Fact]
    public void AnUnusableGeneratedTitleDoesNotEraseAGoodOneBeforeIt()
    {
        // The same arm on the ai-title branch, which no other case reaches:
        // there is nothing to fall back to but the earlier generated name.
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"ai-title\",\"aiTitle\":\"Fixing the blank orb\"}",
            "{\"type\":\"ai-title\",\"aiTitle\":\"   \"}",
        });

        Assert.Equal("Fixing the blank orb", identity.Title);
    }

    [Fact]
    public void AnEmptyTranscriptIsEmpty()
    {
        Assert.True(TranscriptIdentity.From(new string[0]).IsEmpty);
        Assert.True(TranscriptIdentity.None.IsEmpty);
    }

    [Fact]
    public void ARowThatIsNullIsSteppedOver()
    {
        var identity = TranscriptIdentity.From(new string?[]
        {
            null,
            "{\"type\":\"custom-title\",\"customTitle\":\"still found\"}",
        }!);

        Assert.Equal("still found", identity.Title);
    }

    // The case this was all built for, with the rows in the order and the exact
    // byte shape they were read off a real machine in — a fork whose second row
    // named it and whose status file never caught that name.
    [Fact]
    public void TheForkedJobIsNamedAndStopsWearingItsParentsLetters()
    {
        var identity = TranscriptIdentity.From(new[]
        {
            "{\"type\":\"history-suppression\",\"sessionId\":\"0e9677a5\","
            + "\"cause\":\"fork_inherit\",\"ts\":\"2026-08-26T17:32:07.036Z\"}",
            "{\"type\":\"custom-title\",\"customTitle\":\"evidence (2)\","
            + "\"sessionId\":\"0e9677a5-8813-4800-8b8b-e786d701c097\"}",
            "{\"type\":\"agent-name\",\"agentName\":\"evidence (2)\"}",
        });

        Assert.Equal("evidence (2)", identity.Title);

        // And the payoff, which is the whole point: the parent was drawing "Ev"
        // off its own title "evidence", and the fork was drawing "Ev" off the
        // folder name because it had none. Named, it draws "E2".
        Assert.Equal("Ev", OrbGlyph.For("evidence", twoLetter: true));
        Assert.Equal("E2", OrbGlyph.For(identity.Title!, twoLetter: true));
    }

    // --- SessionManager.WantsIdentityFromTranscript --------------------------

    private static SessionStatus Status(
        SessionSource source = SessionSource.ClaudeCode,
        string title = "", string color = "",
        string transcriptPath = "/tmp/x.jsonl") =>
        new()
        {
            Source = source,
            Title = title,
            Color = color,
            TranscriptPath = transcriptPath,
        };

    [Fact]
    public void ASessionMissingItsNameOrColourIsAsked()
    {
        Assert.True(SessionManager.WantsIdentityFromTranscript(Status()));
        Assert.True(SessionManager.WantsIdentityFromTranscript(Status(title: "named")));
        Assert.True(SessionManager.WantsIdentityFromTranscript(Status(color: "green")));
    }

    [Fact]
    public void ASessionWithBothIsNeverAsked()
    {
        // Which is what keeps this off the path of every healthy session.
        Assert.False(SessionManager.WantsIdentityFromTranscript(
            Status(title: "named", color: "green")));
    }

    [Fact]
    public void ASessionNamingNoTranscriptIsNeverAsked()
    {
        Assert.False(SessionManager.WantsIdentityFromTranscript(Status(transcriptPath: "")));
    }

    [Theory]
    [InlineData(SessionSource.Codex)]
    [InlineData(SessionSource.OpenClaw)]
    [InlineData(SessionSource.RemoteControl)]
    public void OnlyClaudeCodeIsAsked(SessionSource source)
    {
        // Those three records are Claude Code's own. A Codex rollout contains
        // none of them, so asking would be a tail read per scan that could only
        // ever answer nothing — the same reason the hook gates its own branch on
        // the CLI.
        Assert.False(SessionManager.WantsIdentityFromTranscript(Status(source)));
    }

    // --- SessionManager.ApplyIdentity ---------------------------------------

    [Fact]
    public void OnlyTheMissingHalfIsFilledIn()
    {
        var status = Status(color: "green");
        SessionManager.ApplyIdentity(status, new TranscriptIdentity("evidence (2)", "red"));

        Assert.Equal("evidence (2)", status.Title);
        Assert.Equal("green", status.Color);   // not overwritten
    }

    [Fact]
    public void ATitleTheHookRecordedIsNeverSecondGuessed()
    {
        // It read the same file with the same precedence, and if the two ever
        // disagreed the status file is the more recent reading.
        var status = Status(title: "what the hook said");
        SessionManager.ApplyIdentity(status, new TranscriptIdentity("older", "green"));

        Assert.Equal("what the hook said", status.Title);
        Assert.Equal("green", status.Color);
    }

    [Fact]
    public void NothingIsWrittenWhenTheTranscriptHadNoAnswer()
    {
        var status = Status();
        SessionManager.ApplyIdentity(status, TranscriptIdentity.None);

        Assert.Equal("", status.Title);
        Assert.Equal("", status.Color);
    }

    [Fact]
    public void AnEmptyAnswerNeverReplacesAnEmptyField()
    {
        // Guards the arm where the identity carries one half and not the other.
        var status = Status();
        SessionManager.ApplyIdentity(status, new TranscriptIdentity("named", null));

        Assert.Equal("named", status.Title);
        Assert.Equal("", status.Color);

        var other = Status();
        SessionManager.ApplyIdentity(other, new TranscriptIdentity(null, "teal"));

        Assert.Equal("", other.Title);
        Assert.Equal("teal", other.Color);
    }
}
