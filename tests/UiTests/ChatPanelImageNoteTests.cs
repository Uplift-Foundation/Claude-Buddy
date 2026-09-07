using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-93: the note line a refused picture leaves in the slot the image would
// have occupied, and its tooltip. Same singleton-cleanup rule as
// ChatPanelTests: ChatPanel is one window shared by every test in the
// process, so each test here HideFor's its own session id when done rather
// than relying on process isolation.
[Collection("Settings")]
public class ChatPanelImageNoteTests : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    private FakeChatSession NewFake(IEnumerable<ChatTurn>? history = null)
    {
        var id = "note-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Fake Session" };
    }

    // Never closed, for the same reason as ChatPanelTests.NewOrb: closing a
    // headless OrbWindow corrupts a process-wide FontManager cache shared
    // with every window built afterward in this run.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    // The note TextBlock among a row's controls, found by the one Opacity
    // value ChatPanel.axaml gives only this element — 0.6, where TimeText is
    // 0.5 and everything else in the per-turn template leaves Opacity at its
    // default 1. There is no per-row x:Name to search by instead: the
    // template is instantiated once per turn, and ChatPanelTests' own
    // ATurnWithImageBytesRendersAsAThumbnail already matches this way for the
    // same reason — there it is an Image's fixed width, here it is this.
    private static TextBlock? NoteTextBlockIn(Avalonia.Controls.Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => Math.Abs(tb.Opacity - 0.6) < 0.001);

    [AvaloniaFact]
    public void ATurnConstructedWithAnImageNoteRendersTheLine()
    {
        const string note = "Picture not shown — the gateway refused it.";

        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "MEDIA:/x/y.png", ImageNote = note };
        var fake = NewFake(new[] { turn });

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);

        Assert.NotNull(block);
        Assert.True(block!.IsVisible);
        Assert.Equal(note, block.Text);
    }

    // The live case, and the exact bug shape that already bit ImageUrl and
    // ImageBytes: OpenClawChatSession.TryResolveLocalMedia and
    // TurnView.LoadImage both resolve asynchronously, well after the row for
    // that turn already exists on screen — see the !HasImage guards in
    // ChatPanel.axaml.cs's own PropertyChanged hook, which exist because of
    // exactly this ordering. ImageNote has to notice the same way rather than
    // only being read once at construction.
    [AvaloniaFact]
    public void ATurnThatGainsANoteAfterItsRowExistsShowsIt()
    {
        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "MEDIA:/x/y.png" };
        var fake = NewFake(new[] { turn });

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        // The element exists from the moment the row is built — Opacity is a
        // literal in the template, not bound — but starts collapsed, since
        // IsVisible is HasImageNote and nothing has failed yet.
        var before = NoteTextBlockIn(ChatPanelTestAccess.Instance!);
        Assert.NotNull(before);
        Assert.False(before!.IsVisible);

        // Set in the same order LoadImage/TryResolveLocalMedia set them —
        // detail before the note, whose own Raise() is what the view
        // actually notices (see ChatTurn.ImageNoteDetail's own header).
        turn.ImageNoteDetail = "/x/y.png — outside-allowed-folders";
        turn.ImageNote = "Picture not shown — the gateway won't serve files from that folder. "
            + "Ask the agent to write it to ~/.openclaw/media/, which is allowed for every agent.";
        Flush();

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);

        Assert.NotNull(block);
        Assert.True(block!.IsVisible);
        Assert.Equal(turn.ImageNote, block.Text);
    }

    [AvaloniaFact]
    public void ATurnWithNoNoteLeavesTheLineCollapsed()
    {
        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "an ordinary reply" };
        var fake = NewFake(new[] { turn });

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);

        Assert.NotNull(block);
        Assert.False(block!.IsVisible);
    }

    // A turn with a decoded picture and a note set never actually happens:
    // both LoadImage and TryResolveLocalMedia return on a successful fetch
    // before either ever asks the gateway why. Pinned here so a later change
    // to that ordering doesn't quietly start setting both at once.
    [AvaloniaFact]
    public void ImageNoteStaysNullWhenAPictureActuallyArrives()
    {
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

        var turn = new ChatTurn
        {
            Role = ChatRole.User,
            Text = "a screenshot",
            IsComplete = true,
            ImageBytes = bytes
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        Assert.Null(turn.ImageNote);

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);
        Assert.NotNull(block);
        Assert.False(block!.IsVisible);
    }

    // ---- CB-109: the row reads the structured source, not the url -------
    //
    // Seeded through FetchMediaAsync's own url-keyed cache, the same seam
    // ChatPanelMarkdownTests uses to drive the decode path without a socket
    // ever opening.
    private static void SeedMediaCache(string url, byte[]? bytes)
    {
        var field = typeof(OpenClawSessions).GetField(
            "Media", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        ((Dictionary<string, byte[]?>)field.GetValue(null)!)[url] = bytes;
    }

    private static byte[] Pixel() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    // The url is fetched verbatim. Since CB-109 it is a fully-formed request
    // carrying the asking session, built where the turn was built; the row
    // reconstructs nothing and must not strip or rebuild any part of it.
    //
    // Proven by seeding only the identity-bearing url and, separately,
    // checking that the same picture behind the *bare* path-only url is not
    // found — a row that rebuilt the request from ImageSourcePath, or trimmed
    // the query, would draw the second one.
    [AvaloniaFact]
    public async Task TheFullyFormedUrlIsFetchedVerbatim()
    {
        var path = "/Users/w/.openclaw/workspace-sample-agent/outputs/" + Guid.NewGuid() + ".png";
        var source = new OpenClawMediaSource(path, "agent:comfyui:discord:direct:100000000000000001");

        Assert.Contains("&sessionKey=", source.Route, StringComparison.Ordinal);
        SeedMediaCache(source.Route, Pixel());

        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "here you go",
            IsComplete = true,
            ImageSourcePath = path,
            ImageUrl = source.Route
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);

        var panel = ChatPanelTestAccess.Instance!;
        Avalonia.Controls.Image? picture = null;

        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            picture = panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
                .FirstOrDefault(im => im.Width == 228);
            if (picture?.Source is not null) break;
            await Task.Delay(10);
        }

        Assert.NotNull(picture);
        Assert.NotNull(picture!.Source);

        // Nothing succeeded, so nothing explained itself.
        Assert.Null(turn.ImageNote);
    }

    // The other half of that: a row whose url carries the session finds
    // nothing when only the identity-free url has bytes behind it. This is
    // what would fail if anybody reintroduced the pre-CB-109 request shape at
    // the fetch, or "simplified" LoadImage into rebuilding one from the path.
    [AvaloniaFact]
    public async Task ThePathOnlyUrlIsNotWhatGetsFetched()
    {
        var path = "/Users/w/.openclaw/media/" + Guid.NewGuid() + ".png";
        var source = new OpenClawMediaSource(path, "agent:comfyui:discord:direct:1");

        SeedMediaCache(OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(path), Pixel());

        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "MEDIA:" + path,
            IsComplete = true,
            ImageSourcePath = path,
            ImageUrl = source.Route
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);

        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        var panel = ChatPanelTestAccess.Instance!;
        Assert.Null(panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>()
            .FirstOrDefault(im => im.Width == 228)?.Source);
    }

    // And when the fetch comes back empty, the note's tooltip carries the
    // path off the structured source. CB-93 had to unescape that path back
    // out of the url because a ChatTurn carried nothing else; the tooltip is
    // where that recovered string was visible, so it is where reading it from
    // the right place is worth asserting.
    //
    // No gateway is configured in this suite, so FetchLocalMediaMetaAsync
    // reaches its own host/token guard and answers null without a socket —
    // which is Explain's honest "couldn't ask" line and Detail's path-alone
    // tooltip. That is the assertion: the path, unmangled, with none of the
    // route's escaping left on it.
    [AvaloniaFact]
    public async Task ARefusedPicturesTooltipIsThePathFromTheSource()
    {
        // Deliberately awkward, so a path recovered by unescaping a url would
        // still pass and a path recovered wrongly would not.
        var path = "/Users/w/.openclaw/media/a drop & 100% " + Guid.NewGuid() + ".png";

        var source = new OpenClawMediaSource(path, "agent:comfyui:discord:direct:1");
        SeedMediaCache(source.Route, Array.Empty<byte>());

        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "MEDIA:" + path,
            IsComplete = true,
            ImageSourcePath = path,
            ImageUrl = source.Route
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);

        for (var i = 0; i < 40 && turn.ImageNote is null; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Flush();

        Assert.Equal("Picture not shown — couldn't ask the gateway why.", turn.ImageNote);
        Assert.Equal(path, turn.ImageNoteDetail);

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);
        Assert.NotNull(block);
        Assert.True(block!.IsVisible);
        Assert.Equal(path, ToolTip.GetTip(block));
    }

    // An assistant-media url arriving on a turn whose path was not set with
    // it. No producer does that — both set the two together — so this is the
    // refusal to caption a note with a guess rather than a media case with a
    // fallback. ShouldAskWhy says yes here (it recognises the url); it is
    // LoadImage's own null-path check that declines, and the whole point of
    // CB-93 is that a wrong reason is worse than none.
    [AvaloniaFact]
    public async Task ATurnWithNoSourcePathIsNeverAskedWhy()
    {
        var url = OpenClawSessions.AssistantMediaRoute
            + Uri.EscapeDataString("/Users/w/.openclaw/media/" + Guid.NewGuid() + ".png");

        SeedMediaCache(url, Array.Empty<byte>());

        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "an attachment with no path recorded",
            IsComplete = true,
            ImageUrl = url
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);

        for (var i = 0; i < 10; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Assert.Null(turn.ImageNote);
        Assert.Null(turn.ImageNoteDetail);

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);
        Assert.NotNull(block);
        Assert.False(block!.IsVisible);
    }

    // And a url from somewhere with no `&meta=1` variant at all — an inbound
    // attachment — is not asked either, which is the guard ShouldAskWhy
    // itself still carries.
    [AvaloniaFact]
    public async Task AnOrdinaryAttachmentUrlIsNeverAskedWhy()
    {
        var url = "/__openclaw__/inbound?source=" + Guid.NewGuid();
        SeedMediaCache(url, Array.Empty<byte>());

        var turn = new ChatTurn
        {
            Role = ChatRole.User,
            Text = "a photo someone sent",
            IsComplete = true,
            ImageUrl = url,
            ImageSourcePath = "/inbound/whatever.png"
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);

        for (var i = 0; i < 10; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Assert.Null(turn.ImageNote);
    }

    [AvaloniaFact]
    public void ImageNoteDetailReachesTheTooltip()
    {
        const string detail = "/x/y.png — outside-allowed-folders";

        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "MEDIA:/x/y.png",
            ImageNote = "Picture not shown — the gateway won't serve files from that folder.",
            ImageNoteDetail = detail
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);

        Assert.NotNull(block);
        Assert.Equal(detail, ToolTip.GetTip(block!));
    }

    // ---- CB-116: the note is gated on confidence, not just on failure -----
    //
    // LocalMediaPathFrom's caption-plus-trailing-token arm (CB-107) matches
    // any caption-shaped prose ending in something extension-shaped — which
    // is right for finding real pictures and wrong for deciding whether a
    // *failed* fetch is worth a visible "picture not shown" note.
    // "I deleted photo.png" matches the identical shape as a real delivery,
    // and unlike a real delivery, the population of ordinary messages that
    // happen to end in a filename is unbounded. The fetch is still attempted
    // (ImageUrl is still set below) — only the note is suppressed.

    // The positive witness this suite's own header comment asks for: a Low
    // turn stays silent on the *same page*, failing the *same way*, as a
    // High turn that does get its note — so the silence is proven to be the
    // gate actually running, not an accident of the panel never having
    // looked at the row at all.
    [AvaloniaFact]
    public async Task ALowConfidenceCandidateStaysSilentOnAFailedFetch()
    {
        var lowPath = "/Users/w/.openclaw/media/" + Guid.NewGuid() + ".png";
        var lowSource = new OpenClawMediaSource(lowPath, "agent:comfyui:discord:direct:1");
        SeedMediaCache(lowSource.Route, Array.Empty<byte>());

        var lowTurn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "I deleted photo.png",
            IsComplete = true,
            ImageSourcePath = lowPath,
            ImageUrl = lowSource.Route,
            Confidence = MediaConfidence.Low
        };

        var highPath = "/Users/w/.openclaw/media/" + Guid.NewGuid() + ".png";
        var highSource = new OpenClawMediaSource(highPath, "agent:comfyui:discord:direct:1");
        SeedMediaCache(highSource.Route, Array.Empty<byte>());

        var highTurn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "MEDIA:" + highPath,
            IsComplete = true,
            ImageSourcePath = highPath,
            ImageUrl = highSource.Route

            // Confidence left at its default (High) deliberately — this is
            // what every real MEDIA: line producer sets, and the point of
            // this turn is to be the ordinary case beside the extraordinary
            // one, not to be specially configured to pass.
        };

        var fake = NewFake(new[] { lowTurn, highTurn });
        ChatPanel.OpenFor(NewOrb(), fake);

        for (var i = 0; i < 40 && highTurn.ImageNote is null; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Flush();

        // The sibling proves the machinery ran at all...
        Assert.Equal("Picture not shown — couldn't ask the gateway why.", highTurn.ImageNote);

        // ...which is what makes this absence meaningful rather than vacuous.
        Assert.Null(lowTurn.ImageNote);
        Assert.Null(lowTurn.ImageNoteDetail);
    }

    // The regression guard for CB-93/CB-108: an explicit "MEDIA:" line keeps
    // explaining a failure, wording unchanged, with Confidence pinned to
    // High explicitly rather than only relying on the default.
    [AvaloniaFact]
    public async Task AnExplicitMediaLineCandidateStillExplainsAFailure()
    {
        var path = "/Users/w/.openclaw/media/" + Guid.NewGuid() + ".png";
        var source = new OpenClawMediaSource(path, "agent:comfyui:discord:direct:1");
        SeedMediaCache(source.Route, Array.Empty<byte>());

        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "MEDIA:" + path,
            IsComplete = true,
            ImageSourcePath = path,
            ImageUrl = source.Route,
            Confidence = MediaConfidence.High
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);

        for (var i = 0; i < 40 && turn.ImageNote is null; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Flush();

        Assert.Equal("Picture not shown — couldn't ask the gateway why.", turn.ImageNote);
        Assert.Equal(path, turn.ImageNoteDetail);
    }

    // The regression guard for provenance-vs-shape — the case a naive "bare
    // filename means low confidence" fix would have broken. A delivery-
    // mirror's own text (see OpenClawSessions.TurnsFromHistory's mirror arm)
    // is just the bare filename with no caption around it at all, and it is
    // still High: TurnsFromHistory sets that from the gateway's own record,
    // never from the text's shape (see
    // OpenClawMediaConfidenceTests.ADeliveryMirrorsBareFilenameShapeDoesNotMakeItLowConfidence
    // for the pure half of this same guard).
    [AvaloniaFact]
    public async Task ADeliveryMirrorShapedCandidateStillExplainsAFailure()
    {
        var path = "/Users/w/.openclaw/media/" + Guid.NewGuid() + ".png";
        var source = new OpenClawMediaSource(path, "agent:comfyui:discord:direct:1");
        SeedMediaCache(source.Route, Array.Empty<byte>());

        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = path[(path.LastIndexOf('/') + 1)..], // the bare filename alone
            IsComplete = true,
            ImageSourcePath = path,
            ImageUrl = source.Route,
            Confidence = MediaConfidence.High
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);

        for (var i = 0; i < 40 && turn.ImageNote is null; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Flush();

        Assert.Equal("Picture not shown — couldn't ask the gateway why.", turn.ImageNote);
    }

    // ---- CB-120: a cross-arm merge must carry the mirror's confidence too -
    //
    // TurnsFromHistory can match one delivered picture with both the
    // delivery-mirror arm (always High — the gateway's own word that it
    // delivered something) and the named-path arm (Low when the agent's own
    // message is ordinary prose with no MEDIA: line and no automation tag).
    // CB-98 collapses the pair into the named turn's bubble, because that is
    // the one carrying the agent's prose — but before this fix it kept the
    // named turn's own, weaker Confidence along with its text, so a
    // gateway-confirmed delivery landed in the Low tier and lost CB-116's
    // explanation the moment its fetch failed.
    //
    // Same silence template as ALowConfidenceCandidateStaysSilentOnAFailedFetch
    // above, built through TurnsFromHistory itself rather than a hand-built
    // ChatTurn: a merged (was-Low, now-High) turn and a genuinely Low-only
    // turn on the *same* history page, both cache entries seeded empty so
    // both fail identically. Asserting the merged turn's note *before* the
    // Low-only turn's absence is what makes that absence meaningful rather
    // than an accident of the panel never having looked.
    [AvaloniaFact]
    public async Task ACrossArmMergedTurnKeepsItsNoteEvenThoughTheNamedTurnAloneWouldHaveBeenSilent()
    {
        var mergedFile = Guid.NewGuid() + ".png";
        var lowOnlyFile = Guid.NewGuid() + ".png";

        // One delivery named by the agent's own path (Low on its own — no
        // MEDIA: line, no openclawAutomation) and mirrored by the gateway
        // (always High); a second, unrelated turn that is genuinely Low with
        // nothing to raise it. Exactly the fixture
        // OpenClawNamedPictureOnHistoryTests.
        // AFileDrawnByBothArmsKeepsHighConfidenceEvenThoughTheNamedTurnAloneWouldBeLow
        // proves at the pure level; this is the same page carried through to
        // what the panel actually shows.
        var historyTurns = OpenClawSessions.TurnsFromHistory(JsonDocument.Parse($$"""
        [{"role":"assistant","content":[{"type":"text","text":"~/.openclaw/media/browser/{{mergedFile}}"}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"{{mergedFile}}"}]},
         {"role":"assistant","content":"I deleted {{lowOnlyFile}}"}]
        """).RootElement, null);

        Assert.Equal(2, historyTurns.Count);

        var mergedHistoryTurn = historyTurns.Single(t => t.Text.Contains("browser"));
        var lowOnlyHistoryTurn = historyTurns.Single(t => t.Text.Contains("I deleted"));

        Assert.Equal(MediaConfidence.High, mergedHistoryTurn.Confidence);
        Assert.Equal(MediaConfidence.Low, lowOnlyHistoryTurn.Confidence);

        // The same shape OpenClawChatSession.SetHistory builds a ChatTurn
        // with — Confidence travels with the turn TurnsFromHistory already
        // decided it for, never re-derived here.
        static ChatTurn ToChatTurn(HistoryTurn t) => new()
        {
            Role = t.Role,
            Text = t.Text,
            ImageSourcePath = t.ImageSourcePath,
            Confidence = t.Confidence,
            ImageUrl = t.ImageUrl,
            ImageAlt = t.ImageAlt,
            At = t.At,
            IsComplete = true
        };

        var mergedTurn = ToChatTurn(mergedHistoryTurn);
        var lowOnlyTurn = ToChatTurn(lowOnlyHistoryTurn);

        SeedMediaCache(mergedTurn.ImageUrl!, Array.Empty<byte>());
        SeedMediaCache(lowOnlyTurn.ImageUrl!, Array.Empty<byte>());

        var fake = NewFake(new[] { mergedTurn, lowOnlyTurn });
        ChatPanel.OpenFor(NewOrb(), fake);

        for (var i = 0; i < 40 && mergedTurn.ImageNote is null; i++)
        {
            Flush();
            await Task.Delay(10);
        }

        Flush();

        // The sibling proves the machinery ran at all...
        Assert.Equal("Picture not shown — couldn't ask the gateway why.", mergedTurn.ImageNote);

        // ...which is what makes this absence meaningful rather than vacuous.
        Assert.Null(lowOnlyTurn.ImageNote);
        Assert.Null(lowOnlyTurn.ImageNoteDetail);
    }
}
