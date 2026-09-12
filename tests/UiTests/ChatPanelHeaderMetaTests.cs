using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The chat panel's third header line, driven through the real ChatPanel.
//
// ChatHeaderMeta's own table says what the line *should* read; this says the
// panel actually draws it, which is a different question and has its own ways
// of going wrong: it is drawn as two rows rather than one (the machine has a
// row to itself, and its own colour), it is refreshed from two places (a hook
// write and a roster answer), and the panel is reused across sessions so
// anything left behind is drawn over the next conversation.
//
// [Collection("Settings")] for the two reasons this suite always has one:
// constructing a panel reads settings, and these tests also swap the panel's
// cached machine name, which is process-wide static state. Two classes racing
// on either would not race to a failure — they would race to a different set
// of executed lines.
[Collection("Settings")]
public class ChatPanelHeaderMetaTests : IDisposable
{
    private readonly List<string> _toClean = new();

    // The real home, because that is what the panel asks Environment for. A
    // literal would make the `~` arm depend on whose machine ran the suite.
    private static readonly string Home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string Project =
        Path.Combine(Home, "Source", "HauntedMansionTerminalTheme");

    // The home-relative path as the panel writes it, with whatever separator
    // this machine uses. ChatHeaderMeta keeps the native one deliberately — a
    // path is a thing somebody pastes into their own shell — so an expected
    // string with a literal "/" in it passes on a Mac and fails on Windows,
    // which is exactly what the Windows leg caught the first time this ran.
    private static string Under(params string[] parts) =>
        "~" + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts);

    private static readonly string ProjectShown = Under("Source", "HauntedMansionTerminalTheme");

    // A name no machine has. The far-machine arm is the one that matters here,
    // and it is only far if it differs from whatever this Mac or this runner
    // calls itself — so the name is chosen to be one nobody's `scutil` will
    // ever answer, rather than trusted to differ by luck.
    private const string Elsewhere = "cb134-not-this-machine";

    // What CB-149's redundancy pass draws Elsewhere and the local machine
    // name as: MachineNames.Readable() turns every hyphen a Bonjour name is
    // wearing back into a space at the point of display, on the theory that
    // it is standing in for whatever character — usually an apostrophe or a
    // space — the name could not carry onto the network. The raw, hyphenated
    // constants above are still what gets fed *in* (WithMachineName, a
    // MirroredSession's reported name); these are only what the panel is
    // asserted to have drawn.
    private const string ElsewhereShown = "cb134 not this machine";
    private const string MachineShown = "warrens macbook pro";

    private FakeChatSession NewFake(string displayName, string? sessionId = null)
    {
        var id = sessionId ?? "header-meta-" + Guid.NewGuid();
        _toClean.Add(id);
        return new FakeChatSession(null) { SessionId = id, DisplayName = displayName };
    }

    // Never closed — see ChatPanelTests' own comment on NewOrb: closing a
    // headless OrbWindow corrupts a process-wide FontManager resource shared
    // by every other window built in this run.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static OrbWindow OrbShowing(string title, string cwd, string color = "")
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "idle",
            Title = title,
            Cwd = cwd,
            Color = color,
        });

        return orb;
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    // The block, which is what collapses, carries the tooltip, and holds the
    // two rows.
    private static StackPanel Row(ChatPanel panel) => panel.FindControl<StackPanel>("MetaRow")!;

    private static TextBlock Lead(ChatPanel panel) => panel.FindControl<TextBlock>("MetaText")!;

    private static TextBlock MachineBlock(ChatPanel panel) =>
        panel.FindControl<TextBlock>("MetaMachineText")!;

    // Both rows read as one string, the way ChatHeaderMeta.Text reads them —
    // so a test that expects the whole header and a test that expects one row
    // are asking different questions rather than the same one twice. A row
    // that is not showing contributes nothing, which is how the collapse and
    // the room case are told apart from a row that is merely empty.
    private static string LineOf(ChatPanel panel)
    {
        var rows = new[] { Lead(panel), MachineBlock(panel) }
            .Where(row => row.IsVisible)
            .Select(row => row.Text ?? "")
            .Where(text => text.Length > 0);

        return string.Join(" · ", rows);
    }

    // ISolidColorBrush rather than SolidColorBrush: a brush set in the XAML
    // arrives as an *immutable* one, and a brush set in code does not, so a
    // cast to the mutable type passes for the machine row and throws for the
    // row beside it. Both are the same colour to a reader, which is the thing
    // being asserted.
    private static Color InkOf(TextBlock block) => ((ISolidColorBrush)block.Foreground!).Color;

    private static string? TipOf(ChatPanel panel) => ToolTip.GetTip(Row(panel)) as string;

    public void Dispose()
    {
        foreach (var id in _toClean) ChatPanel.HideFor(id);
    }

    // --- the three facts --------------------------------------------------

    [AvaloniaFact]
    public void ALocalSessionsHeaderNamesItsFolderAndItsMachine()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // The ordinary case: the title line already *is* the session's name,
        // so repeating it underneath would be noise and the line is the folder
        // and the machine.
        ChatPanel.OpenFor(
            OrbShowing("haunted-mansion", Project), NewFake("haunted-mansion"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(Row(panel).IsVisible);
        Assert.Equal(ProjectShown + " · " + MachineShown, LineOf(panel));
    }

    [AvaloniaFact]
    public void APersonaOnTheTitleLineGivesTheSessionNameSomewhereToGo()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // CB-133 put a CLAUDE.md persona on the title line. That is what makes
        // the session's own name worth drawing at all — the header stopped
        // saying it anywhere else the moment "Leota" took the top line.
        ChatPanel.OpenFor(OrbShowing("haunted-mansion", Project), NewFake("Leota"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Equal("Leota", panel.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal(
            "haunted-mansion · " + ProjectShown + " · " + MachineShown, LineOf(panel));
    }

    [AvaloniaFact]
    public void TheWholePathIsInTheTooltipRatherThanOnTheLine()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        ChatPanel.OpenFor(OrbShowing("haunted-mansion", Project), NewFake("Leota"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        // CharacterEllipsis is what happens to this line in a narrow panel, so
        // the hover is the only place the path survives whole — and it is on
        // the row, so the gap the ellipsis makes answers too.
        Assert.Equal(TextTrimming.CharacterEllipsis, Lead(panel).TextTrimming);
        Assert.Contains(Project, TipOf(panel));
        Assert.DoesNotContain("~", TipOf(panel)!);

        // The machine is a row of its own, under the rest rather than at the
        // end of it, which is what stops it being the first thing to
        // disappear: at the width a panel opens at, one row holding all three
        // facts trimmed the machine away entirely.
        Assert.True(MachineBlock(panel).IsVisible);
        Assert.Equal(
            Row(panel).Children.IndexOf(Lead(panel)) + 1,
            Row(panel).Children.IndexOf(MachineBlock(panel)));
    }

    [AvaloniaFact]
    public void AGatewayRoomHasNoFolderAndSaysOnlyTheMachine()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // "#openclaw-management — wtvamp" splits into the room and the place,
        // which are the first two lines. A conversation in a channel is not
        // running in a directory on this disk, so the third has one fact left.
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.OpenClaw,
            State = "idle",
            Kind = SessionKind.Channel,
            Title = "",
            Cwd = "",
        });

        ChatPanel.OpenFor(orb, NewFake("#openclaw-management — wtvamp"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(Row(panel).IsVisible);
        Assert.Equal(MachineShown, LineOf(panel));

        // The empty row above it is collapsed rather than left blank, so the
        // machine sits directly under the chips instead of after a gap.
        Assert.False(Lead(panel).IsVisible);
        Assert.True(MachineBlock(panel).IsVisible);

        // And the two lines above it are untouched, which is the regression
        // this capture's twin in tests/UiScreenshots exists to show.
        Assert.Equal("#openclaw-management", panel.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal("wtvamp", panel.FindControl<TextBlock>("SubtitleText")!.Text);
    }

    // CB-150: the case AGatewayRoomHasNoFolderAndSaysOnlyTheMachine's status
    // leaves empty. There, status.Title is "" and nothing is repeated by
    // construction. Here it is the gateway's own session title — "Annabel Lee
    // — #cascadia-forensics-marketing" — which ApplyTitle has *already* split
    // across TitleText ("Annabel Lee") and SubtitleText
    // ("#cascadia-forensics-marketing"). Before this fix, Unrepeated compared
    // status.Title only against TitleText, saw "Annabel Lee" next to the
    // whole "Annabel Lee — #cascadia-forensics-marketing" and called them
    // different, so the meta row drew the channel a second time directly
    // under the chip that already names it.
    [AvaloniaFact]
    public void AGatewaySessionsSplitTitleIsNotRepeatedInTheMetaLine()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.OpenClaw,
            State = "idle",
            Kind = SessionKind.Channel,
            Title = "Annabel Lee — #cascadia-forensics-marketing",
            Cwd = "",
        });

        ChatPanel.OpenFor(orb, NewFake("Annabel Lee — #cascadia-forensics-marketing"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Equal("Annabel Lee", panel.FindControl<TextBlock>("TitleText")!.Text);
        Assert.Equal(
            "#cascadia-forensics-marketing", panel.FindControl<TextBlock>("SubtitleText")!.Text);

        // The meta row still shows the machine — that fact is never a
        // repeat — but the lead line, which would only have restated the
        // two lines above it, stays collapsed.
        Assert.False(Lead(panel).IsVisible);
        Assert.Equal(MachineShown, LineOf(panel));
    }

    [AvaloniaFact]
    public void APanelBoundBeforeTheFirstHookWriteStillNamesTheMachine()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // An orb can be clicked before its hook has ever fired, and then there
        // is no status anywhere to read: the orb has none, and SessionManager
        // is not running in this suite either, which is what makes this the
        // arm ApplyMeta's own fallback is written for. What it must not do is
        // draw a row with a separator and nothing on either side of it.
        ChatPanel.OpenFor(NewOrb(), NewFake("Leota"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.True(Row(panel).IsVisible);
        Assert.Equal(MachineShown, LineOf(panel));
        Assert.False(Lead(panel).IsVisible);
    }

    [AvaloniaFact]
    public void AHeaderWithNothingToSayStaysTwoLinesTall()
    {
        // Only reachable with the machine name emptied: MachineNames.Mine()
        // has never answered blank on a real machine, so in the app this line
        // is drawn essentially always. The collapse is what keeps a panel that
        // knows nothing exactly as tall as it was before CB-134 — and a line
        // that could only be verified by finding a machine with no name would
        // be a rule nobody ever checked.
        using var machine = ChatPanelTestAccess.WithMachineName("");

        ChatPanel.OpenFor(OrbShowing("", ""), NewFake("#openclaw-management"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.False(Row(panel).IsVisible);
        Assert.Equal("", LineOf(panel));
        Assert.Null(TipOf(panel));
    }

    // --- which machine, and in what colour --------------------------------

    [AvaloniaFact]
    public void ThisMachineIsDrawnInTheSameQuietInkAsTheRestOfTheLine()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        ChatPanel.OpenFor(OrbShowing("haunted-mansion", Project), NewFake("haunted-mansion"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Equal(MachineShown, MachineBlock(panel).Text);
        Assert.Equal(Color.Parse("#80FFFFFF"), InkOf(MachineBlock(panel)));
        Assert.Equal(InkOf(Lead(panel)), InkOf(MachineBlock(panel)));
    }

    [AvaloniaFact]
    public void AMirroredSessionsMachineIsDrawnInTheOrbsOwnAccent()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // The case the colour exists for: a panel open on this Mac showing a
        // conversation happening on the mini. The accent is the orb's own, so
        // the panel and the ring on the thing that was clicked agree.
        var orb = OrbShowing("deploy the release", Project, color: "green");
        var far = new MirroredSession("mirror-" + Guid.NewGuid(), "Aurora", Elsewhere);
        _toClean.Add(far.SessionId);

        ChatPanel.OpenFor(orb, far);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Equal(ElsewhereShown, MachineBlock(panel).Text);
        Assert.NotNull(orb.AccentColor);
        Assert.Equal(orb.AccentColor, InkOf(MachineBlock(panel)));
    }

    [AvaloniaFact]
    public void AMirroredSessionOnAnOrbWithNoColourFallsBackToTheLinkBlue()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // Most sessions have never been given a /color, so the fallback is the
        // ordinary case rather than the exotic one — and it still has to be
        // brighter than the line around it or the flag says nothing.
        var orb = OrbShowing("deploy the release", Project);
        var far = new MirroredSession("mirror-" + Guid.NewGuid(), "Aurora", Elsewhere);
        _toClean.Add(far.SessionId);

        ChatPanel.OpenFor(orb, far);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Null(orb.AccentColor);
        Assert.Equal(Color.Parse("#FF9FD0FF"), InkOf(MachineBlock(panel)));
    }

    [AvaloniaFact]
    public void AFarSessionReportingThisMachineIsNotFlagged()
    {
        // Unarrangeable outside a test — the mirror only ever reports a machine
        // that is not this one — and the reason the comparison is a rule with a
        // test rather than a bool worked out at the call site.
        using var machine = ChatPanelTestAccess.WithMachineName(Elsewhere);

        var far = new MirroredSession("mirror-" + Guid.NewGuid(), "Aurora", Elsewhere);
        _toClean.Add(far.SessionId);

        ChatPanel.OpenFor(OrbShowing("deploy the release", Project, color: "green"), far);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Equal(ElsewhereShown, MachineBlock(panel).Text);
        Assert.Equal(Color.Parse("#80FFFFFF"), InkOf(MachineBlock(panel)));
    }

    [AvaloniaFact]
    public void TheLineGainsItsAccentWhenTheRosterFinallyNamesTheMachine()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // A mirrored session's machine arrives after the panel is open, and
        // its hook writes happen on the *other* machine — so if the roster's
        // answer did not refresh this line, nothing else ever would.
        var far = new MirroredSession("mirror-" + Guid.NewGuid(), "Aurora", null);
        _toClean.Add(far.SessionId);

        ChatPanel.OpenFor(OrbShowing("deploy the release", Project), far);
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        // Until it answers, the line says this machine — naming no machine
        // would be a hole, and naming a guessed one would be worse.
        Assert.EndsWith(MachineShown, LineOf(panel));
        Assert.Equal(Color.Parse("#80FFFFFF"), InkOf(MachineBlock(panel)));

        far.Arrive(Elsewhere);
        Flush();

        Assert.EndsWith(ElsewhereShown, LineOf(panel));
        Assert.Equal(Color.Parse("#FF9FD0FF"), InkOf(MachineBlock(panel)));
    }

    // --- facts that arrive late ------------------------------------------

    [AvaloniaFact]
    public void ATitleArrivingOnALaterHookWriteFillsTheNameIn()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // Claude Code names a chat some way into it, so a status file read
        // early says "title":"". OrbWindow.UpdateFrom calls RefreshIdentityFor,
        // which re-runs ApplyTitle and with it this line — the same reason
        // ApplyTitle re-reads DisplayName rather than caching it at Bind.
        var orb = OrbShowing("", Project);

        ChatPanel.OpenFor(orb, NewFake("Leota"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Equal(ProjectShown + " · " + MachineShown, LineOf(panel));

        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "idle",
            Title = "haunted-mansion",
            Cwd = Project,
        });
        Flush();

        Assert.Equal(
            "haunted-mansion · " + ProjectShown + " · " + MachineShown, LineOf(panel));
    }

    [AvaloniaFact]
    public void OpeningASecondSessionReplacesTheLineRatherThanAddingToIt()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // The transient panel is one window reused across orbs, so anything
        // these rows leave behind is drawn over the next conversation — a row
        // still showing the last session's machine most of all, since nothing
        // about the new session would clear it.
        ChatPanel.OpenFor(OrbShowing("haunted-mansion", Project), NewFake("Leota"));
        Flush();

        var second = Path.Combine(Home, "Source", "Claude-Buddy");
        ChatPanel.OpenFor(OrbShowing("Claude-Buddy", second), NewFake("Claude-Buddy"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;

        Assert.Equal(Under("Source", "Claude-Buddy") + " · " + MachineShown, LineOf(panel));
        Assert.DoesNotContain("haunted-mansion", LineOf(panel));
        Assert.DoesNotContain(Project, TipOf(panel));
    }

    [AvaloniaFact]
    public void APanelThatLearnsNothingNewLosesNothingItAlreadyHad()
    {
        using var machine = ChatPanelTestAccess.WithMachineName("warrens-macbook-pro");

        // A hook write that changes only the state still runs the whole
        // header. Nothing about the line should flicker or empty out on it —
        // which is the failure mode the avatar had before ApplyBorrowedIdentity
        // learned never to blank what was already there.
        var orb = OrbShowing("haunted-mansion", Project);
        ChatPanel.OpenFor(orb, NewFake("Leota"));
        Flush();

        var panel = ChatPanelTestAccess.Instance!;
        var before = LineOf(panel);

        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            State = "generating",
            Title = "haunted-mansion",
            Cwd = Project,
        });
        Flush();

        Assert.Equal(before, LineOf(panel));
        Assert.Equal(MachineShown, MachineBlock(panel).Text);
    }

    // A session that is being mirrored from another machine: the one thing
    // IRemoteChatMachine exists for, and the only interface arm this line
    // reads. FakeChatSession is sealed and deliberately says nothing about a
    // machine — that is what a local session looks like — so the far case
    // needs its own, and a minimal one says more clearly which member the
    // panel actually asks for.
    private sealed class MirroredSession : IRemoteChatSession, IRemoteChatMachine
    {
        public MirroredSession(string sessionId, string displayName, string? machineName)
        {
            SessionId = sessionId;
            DisplayName = displayName;
            MachineName = machineName;
        }

        public string SessionId { get; }

        public string DisplayName { get; }

        public RemoteChatState State => RemoteChatState.Connected;

        public IReadOnlyList<ChatTurn> History { get; } = Array.Empty<ChatTurn>();

        // Null until the roster answers, which a panel can open before. Arrive
        // is that answer landing — the event is what the real one raises, and
        // raising it is the whole point of this fake.
        public string? MachineName { get; private set; }

        public void Arrive(string name)
        {
            MachineName = name;
            MachineChanged?.Invoke();
        }

        public event Action? MachineChanged;

        public event Action<ChatTurn>? TurnAdded;

        public event Action<ChatTurn>? TurnUpdated;

        public event Action<RemoteChatState>? StateChanged;

        public Task SendAsync(string text)
        {
            // Nothing here sends: this fake exists for the header, and a turn
            // added would only be a row the assertions have to skip past.
            TurnAdded?.Invoke(new ChatTurn { Role = ChatRole.User, Text = text });
            return Task.CompletedTask;
        }

        public void Cancel()
        {
            // Nothing is ever in flight.
        }
    }
}
