using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Arms of the URL-routing and mirror work that its own suites do not reach.
//
// Grouped by nothing except that: each is a decision the surrounding code makes
// on a path that is otherwise a process launch, a scan of the machine's real
// processes, or a live relay.
public class MirrorAndRoutingArmsTests
{
    // ---- which directory a running instance is on --------------------------

    // No override in the environment means the app resolved its own default
    // location — a Dock or shell launch, or this app's own launch of the
    // Default profile, which deliberately passes no override so that Claude
    // Desktop does not re-run its deployment-mode chooser.
    //
    // Getting this wrong routes a sign-in callback to the wrong profile, which
    // is the bug the whole router exists to fix.
    [Fact]
    public void AnInstanceWithNoOverrideIsOnTheDefaultDirectory()
    {
        var instance = new ClaudeInstance(Pid: 42, UserDataDir: null);

        Assert.Equal("/Users/w/Library/Application Support/Claude",
            ClaudeDesktopManager.DirectoryOf(instance, "/Users/w/Library/Application Support/Claude"));
    }

    // A real directory is canonicalised, so two spellings of one path — a
    // symlink, a trailing slash, /tmp against /private/tmp — do not read as two
    // different profiles and split one instance into two candidates.
    [Fact]
    public void ARealDirectoryComesBackCanonicalised()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cb-udd-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var instance = new ClaudeInstance(Pid: 42, UserDataDir: directory + "/");

            Assert.Equal(ClaudeDesktopManager.Canonicalise(directory),
                ClaudeDesktopManager.DirectoryOf(instance, "/default"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A directory that is not there cannot be canonicalised — Canonicalise
    // resolves against the filesystem — so the path is normalised by hand
    // instead. An instance whose userData directory has been deleted out from
    // under it is still a running instance, and still has to be addressable.
    [Fact]
    public void ADirectoryThatIsNotThereIsStillNormalised()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cb-gone-" + Guid.NewGuid().ToString("n")[..8]);

        var answer = ClaudeDesktopManager.DirectoryOf(
            new ClaudeInstance(Pid: 42, UserDataDir: missing + "/"), "/default");

        Assert.Equal(Path.GetFullPath(missing).TrimEnd('/'), answer);
        Assert.DoesNotContain("//", answer!);
    }

    // ---- the transcript a mirror sends -------------------------------------

    // Three roles go over the wire as one letter each, and the letters have to
    // survive the round trip in both directions or a live view relabels
    // everybody. System is the one worth pinning: it is what the panel's own
    // notes are, so getting it wrong turns "the relay isn't running" into
    // something the far session appears to have said.
    [Theory]
    [InlineData("u", ChatRole.User)]
    [InlineData("s", ChatRole.System)]
    [InlineData("a", ChatRole.Assistant)]
    public void EveryRoleLetterComesBackAsItsRole(string letter, ChatRole role) =>
        Assert.Equal(role, MirrorProtocol.RoleOf(letter));

    // Anything else is an assistant turn rather than an exception: the letters
    // come off another machine, which may be running a different version, and a
    // turn shown under the wrong name is a far better outcome than a live view
    // that stops.
    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("U")]
    public void AnUnknownRoleLetterIsReadAsTheAssistant(string letter) =>
        Assert.Equal(ChatRole.Assistant, MirrorProtocol.RoleOf(letter));

    // And the same three the other way, from real transcript rows rather than
    // from hand-built turns — which is what makes this an assertion about the
    // mapping and not just about the switch.
    [Fact]
    public void ASystemRowIsSentAsTheSystemLetter()
    {
        var turns = MirrorProtocol.TurnsFrom(new[] { SystemRow, AssistantRow, UserRow }, "claude");

        Assert.Contains(turns, t => t.Role == "s");
        Assert.Contains(turns, t => t.Role == "a");
        Assert.Contains(turns, t => t.Role == "u");
    }

    // Every letter this sends is one RoleOf knows, which is the property that
    // actually matters — the two switches are written separately and nothing
    // else stops them drifting apart.
    [Fact]
    public void EveryLetterSentIsOneTheOtherSideUnderstands()
    {
        var turns = MirrorProtocol.TurnsFrom(new[] { SystemRow, AssistantRow, UserRow }, "claude");

        Assert.All(turns, t => Assert.Contains(t.Role, new[] { "u", "s", "a" }));
    }

    private const string UserRow =
        """{"type":"user","uuid":"u1","timestamp":"2026-08-16T10:00:00Z","message":{"role":"user","content":"fix it"}}""";

    private const string AssistantRow =
        """{"type":"assistant","uuid":"a1","timestamp":"2026-08-16T10:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed."}]}}""";

    // A thinking block, which ChatTranscript maps to a System turn — watching a
    // session think is most of the value of an orb that pulses, so it is shown
    // as its own turn rather than folded into the reply. This is the shape that
    // reaches the "s" arm without inventing a row type.
    private const string SystemRow =
        """{"type":"assistant","uuid":"s1","timestamp":"2026-08-16T10:00:05Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"weighing two options"}]}}""";

    // ---- a transcript that arrives unreadable ------------------------------

    // Null rather than an exception, and the caller turns that into "the
    // transcript arrived unreadable" on screen. The payload is gzip off another
    // machine, so bytes that are not what they claim is an ordinary failure
    // rather than a corruption to report — and a live view that throws here
    // takes the panel with it.
    [Fact]
    public void APayloadThatIsNotATranscriptDecodesToNothing() =>
        Assert.Null(MirrorProtocol.DecodeTurns(new byte[] { 0x4E, 0x4F, 0x50, 0x45 }));

    [Fact]
    public void AnEmptyPayloadDecodesToNothing() =>
        Assert.Null(MirrorProtocol.DecodeTurns(Array.Empty<byte>()));

    // Valid gzip holding something that is not a turn list takes the same
    // route, which is the case a version skew would actually produce.
    [Fact]
    public void ValidGzipHoldingTheWrongThingDecodesToNothing()
    {
        var packed = MirrorProtocol.PackRows(new[] { "not json at all" });

        Assert.Null(MirrorProtocol.DecodeTurns(packed));
    }

    // The round trip it is the failure half of, so the two are asserted
    // together rather than only the failure being pinned.
    [Fact]
    public void ARealTranscriptSurvivesTheRoundTrip()
    {
        var turns = MirrorProtocol.TurnsFrom(new[] { UserRow, AssistantRow }, "claude");

        var back = MirrorProtocol.DecodeTurns(MirrorProtocol.EncodeTurns(turns));

        Assert.NotNull(back);
        Assert.Equal(turns.Select(t => t.Text), back!.Select(t => t.Text));
    }
}
