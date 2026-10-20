using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace Orbweaver.Tests;

// CB-267: Settings' "let orbs message sessions directly" rows and the one-time
// card that introduces them.
//
// Nothing here touches a real config root. The roots, the state read, the write
// and the path display are all seams on SettingsWindow, replaced below by an
// in-memory world; the production delegates get one test of their own, run
// against a scratch directory. Handlers are called directly rather than by
// clicking a ToggleSwitch, whose template depends on the theme that happened to
// load — the buttons are clicked, because they are plain.
[Collection("Settings")]
public class SettingsPeerInboundRowTests : IDisposable
{
    private readonly Func<IReadOnlyList<string>> _roots = SettingsWindow.InboundRoots;
    private readonly Func<string, InboundSetting> _state = SettingsWindow.InboundState;
    private readonly Func<string, bool, PeerInboundWrite> _apply = SettingsWindow.InboundApply;
    private readonly Func<string, string> _display = SettingsWindow.InboundDisplayPath;

    private readonly Dictionary<string, InboundSetting> _world = new();
    private readonly List<(string Root, bool Accept)> _writes = new();
    private PeerInboundWrite _next = new(PeerInboundWriteKind.Written, null);

    public SettingsPeerInboundRowTests()
    {
        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.ClaudeCodeChatEnabled = true;
        OrbweaverSettings.ClaudeCodeReplyEnabled = true;
        OrbweaverSettings.PeerInboundNoticeSeen = false;

        SettingsWindow.InboundRoots = () => _world.Keys.ToList();
        SettingsWindow.InboundState = root => _world[root];
        SettingsWindow.InboundDisplayPath = path => "~" + path;
        SettingsWindow.InboundApply = (root, accept) =>
        {
            _writes.Add((root, accept));
            if (_next.Kind == PeerInboundWriteKind.Written)
                _world[root] = accept ? InboundSetting.Accepting : InboundSetting.NotAccepting;
            return _next;
        };
    }

    public void Dispose()
    {
        SettingsWindow.InboundRoots = _roots;
        SettingsWindow.InboundState = _state;
        SettingsWindow.InboundApply = _apply;
        SettingsWindow.InboundDisplayPath = _display;

        OrbweaverSettings.ClaudeCodeEnabled = true;
        OrbweaverSettings.ClaudeCodeChatEnabled = true;
        OrbweaverSettings.ClaudeCodeReplyEnabled = false;
        OrbweaverSettings.PeerInboundNoticeSeen = false;
    }

    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static IEnumerable<ILogical> Walk(IEnumerable<Control> rows) =>
        rows.SelectMany(row => new ILogical[] { row }.Concat(row.GetLogicalDescendants()));

    private static string TextOf(IEnumerable<Control> rows) =>
        string.Join("\n", Walk(rows).OfType<TextBlock>().Select(t => t.Text));

    private static Button ButtonNamed(IEnumerable<Control> rows, string label) =>
        Walk(rows).OfType<Button>().Single(b => (string?)b.Content == label);

    private static bool HasButton(IEnumerable<Control> rows, string label) =>
        Walk(rows).OfType<Button>().Any(b => (string?)b.Content == label);

    private static Control[] Rows(SettingsWindow window) => window.ClaudeCodeChatRows();

    private static int Switches(IEnumerable<Control> rows) =>
        Walk(rows).Count(l => l is ToggleSwitch or CheckBox);

    private static int InboundRowCount(IEnumerable<Control> rows) =>
        Walk(rows).OfType<TextBlock>().Count(t => t.Text?.StartsWith(PeerInbound.ButtonLabel) == true);

    // ---- when the rows exist ----

    [AvaloniaFact]
    public void RowAppearsOnlyWhenClaudeCodeChatAndReplyAreOn()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        OrbweaverSettings.PeerInboundNoticeSeen = true;

        Assert.Equal(1, InboundRowCount(Rows(NewWindow())));

        OrbweaverSettings.ClaudeCodeReplyEnabled = false;
        Assert.Equal(0, InboundRowCount(Rows(NewWindow())));

        OrbweaverSettings.ClaudeCodeReplyEnabled = true;
        OrbweaverSettings.ClaudeCodeChatEnabled = false;
        Assert.Equal(0, InboundRowCount(Rows(NewWindow())));

        OrbweaverSettings.ClaudeCodeChatEnabled = true;
        OrbweaverSettings.ClaudeCodeEnabled = false;
        Assert.Equal(0, InboundRowCount(Rows(NewWindow())));
    }

    [AvaloniaFact]
    public void NoRootsMeansNoRows()
    {
        OrbweaverSettings.PeerInboundNoticeSeen = true;
        Assert.Equal(0, InboundRowCount(Rows(NewWindow())));
        Assert.Empty(NewWindow().InboundRows());
    }

    [AvaloniaFact]
    public void OneRowPerRootAndThePathNamesTheRootOnlyWhenThereIsMoreThanOne()
    {
        OrbweaverSettings.PeerInboundNoticeSeen = true;
        _world["/home/.claude"] = InboundSetting.NotAccepting;

        var single = TextOf(NewWindow().InboundRows());
        Assert.Contains(PeerInbound.ButtonLabel, single);
        Assert.DoesNotContain(PeerInbound.ButtonLabel + " (", single);

        _world["/home/.claude-board"] = InboundSetting.Accepting;

        var both = NewWindow().InboundRows();
        Assert.Equal(2, InboundRowCount(both));
        var text = TextOf(both);
        Assert.Contains(PeerInbound.ButtonLabel + " (.claude)", text);
        Assert.Contains(PeerInbound.ButtonLabel + " (.claude-board)", text);
        Assert.Contains("crossSessionInbound: accept", text);
        Assert.Contains("settings.json.orbweaver-backup", text);
    }

    // ---- turning on ----

    [AvaloniaFact]
    public void TurningOnOnlyAsksAndWritesNothing()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        var window = NewWindow();

        window.OnInboundToggled("/home/.claude", true);

        Assert.Empty(_writes);
        var rows = window.InboundRows();
        Assert.Contains(PeerInbound.TradeOff, TextOf(rows));
        Assert.Contains("~" + PeerInboundSettingsFile.PathFor("/home/.claude"), TextOf(rows));
        Assert.True(HasButton(rows, PeerInbound.ConfirmLabel));
        Assert.True(HasButton(rows, "Cancel"));
    }

    [AvaloniaFact]
    public void CancelClosesTheQuestionWithoutWritingAndTheSwitchReturnsToOff()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        var window = NewWindow();
        window.OnInboundToggled("/home/.claude", true);

        ButtonNamed(window.InboundRows(), "Cancel").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(_writes);
        Assert.False(HasButton(window.InboundRows(), PeerInbound.ConfirmLabel));
        Assert.DoesNotContain(Walk(window.InboundRows()).OfType<ToggleSwitch>(), t => t.IsChecked == true);
        Assert.DoesNotContain(Walk(window.InboundRows()).OfType<CheckBox>(), t => t.IsChecked == true);
    }

    [AvaloniaFact]
    public void ConfirmWritesOnceAndMarksTheNoticeSeen()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        var window = NewWindow();
        window.OnInboundToggled("/home/.claude", true);

        ButtonNamed(window.InboundRows(), PeerInbound.ConfirmLabel).RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(new[] { ("/home/.claude", true) }, _writes);
        Assert.Equal(InboundSetting.Accepting, _world["/home/.claude"]);
        Assert.True(OrbweaverSettings.PeerInboundNoticeSeen);
        Assert.False(HasButton(window.InboundRows(), PeerInbound.ConfirmLabel));
    }

    [AvaloniaFact]
    public void ConfirmingSomethingAlreadyOnStillCountsAsSeen()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        _next = new PeerInboundWrite(PeerInboundWriteKind.Unchanged, null);
        var window = NewWindow();

        window.OnInboundConfirmed("/home/.claude");

        Assert.True(OrbweaverSettings.PeerInboundNoticeSeen);
    }

    [AvaloniaFact]
    public void ARefusedWriteIsShownWithItsReasonAndLeavesTheNoticeUnseen() =>
        AssertFailureShown(PeerInboundWriteKind.Refused, "settings.json has comments");

    [AvaloniaFact]
    public void AFailedWriteWithNoReasonSaysSomethingAnyway() =>
        AssertFailureShown(PeerInboundWriteKind.Failed, null);

    private void AssertFailureShown(PeerInboundWriteKind kind, string? reason)
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        _next = new PeerInboundWrite(kind, reason);
        var window = NewWindow();

        window.OnInboundConfirmed("/home/.claude");

        Assert.False(OrbweaverSettings.PeerInboundNoticeSeen);
        var text = TextOf(window.InboundRows());
        Assert.Contains("Not changed: " + (reason ?? "the file could not be written"), text);
        Assert.Contains("~" + PeerInboundSettingsFile.PathFor("/home/.claude"), text);

        // A later success clears the message.
        _next = new PeerInboundWrite(PeerInboundWriteKind.Written, null);
        window.OnInboundConfirmed("/home/.claude");
        Assert.DoesNotContain("Not changed", TextOf(window.InboundRows()));
    }

    // The handlers above are called directly; these two flip the real switch the
    // row built, which is what proves it is wired to them. ToggleSwitch and the
    // CheckBox fallback are both ToggleButtons, so this holds whichever template
    // the theme supplied.
    [AvaloniaFact]
    public void FlippingTheRealSwitchOnAsksAndWritesNothing()
    {
        OrbweaverSettings.PeerInboundNoticeSeen = true;
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        var window = NewWindow();

        var toggle = Assert.Single(Walk(window.InboundRows()).OfType<ToggleButton>());
        toggle.IsChecked = true;

        Assert.Empty(_writes);
        Assert.True(HasButton(window.InboundRows(), PeerInbound.ConfirmLabel));
    }

    [AvaloniaFact]
    public void FlippingTheRealSwitchOffOnAnAcceptingRootWrites()
    {
        OrbweaverSettings.PeerInboundNoticeSeen = true;
        _world["/home/.claude"] = InboundSetting.Accepting;
        var window = NewWindow();

        var toggle = Assert.Single(Walk(window.InboundRows()).OfType<ToggleButton>());
        Assert.True(toggle.IsChecked);
        toggle.IsChecked = false;

        Assert.Equal(new[] { ("/home/.claude", false) }, _writes);
    }

    // ---- turning off ----

    [AvaloniaFact]
    public void TurningOffWritesAtOnce()
    {
        _world["/home/.claude"] = InboundSetting.Accepting;
        var window = NewWindow();

        window.OnInboundToggled("/home/.claude", false);

        Assert.Equal(new[] { ("/home/.claude", false) }, _writes);
        Assert.Equal(InboundSetting.NotAccepting, _world["/home/.claude"]);
    }

    [AvaloniaFact]
    public void FlippingTheSwitchBackWhileAskingIsACancelNotAWrite()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        var window = NewWindow();
        window.OnInboundToggled("/home/.claude", true);

        window.OnInboundToggled("/home/.claude", false);

        Assert.Empty(_writes);
        Assert.False(HasButton(window.InboundRows(), PeerInbound.ConfirmLabel));
    }

    [AvaloniaFact]
    public void AFailedTurnOffIsReported()
    {
        _world["/home/.claude"] = InboundSetting.Accepting;
        _next = new PeerInboundWrite(PeerInboundWriteKind.Failed, "disk full");
        var window = NewWindow();

        window.OnInboundToggled("/home/.claude", false);

        Assert.Contains("Not changed: disk full", TextOf(window.InboundRows()));
    }

    [AvaloniaFact]
    public void AnUnreadableFileGetsADisabledSwitchAndAnExplanation()
    {
        OrbweaverSettings.PeerInboundNoticeSeen = true;
        _world["/home/.claude"] = InboundSetting.Unreadable;

        var rows = NewWindow().InboundRows();

        Assert.Contains("could not be read, so it was left alone", TextOf(rows));
        var controls = Walk(rows).OfType<Control>().Where(c => c is ToggleSwitch or CheckBox).ToList();
        Assert.NotEmpty(controls);
        Assert.All(controls, c => Assert.False(c.IsEnabled));
    }

    // ---- the first-run card ----

    [AvaloniaFact]
    public void TheCardShowsFirstWhileNothingHasBeenSeenAndSomeRootLacksAccept()
    {
        _world["/home/.claude"] = InboundSetting.Accepting;
        _world["/home/.claude-board"] = InboundSetting.NotAccepting;

        var cards = NewWindow().ClaudeCodeSection();

        Assert.Contains("Claude Code holds a message", TextOf(new[] { cards[0] }));
        Assert.True(HasButton(new[] { cards[0] }, "Got it"));
    }

    [AvaloniaFact]
    public void TheCardIsNotShownOnceSeenWhenEveryRootAcceptsOrClaudeCodeIsOff()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;

        OrbweaverSettings.PeerInboundNoticeSeen = true;
        Assert.DoesNotContain("Got it", TextOf(NewWindow().ClaudeCodeSection()));
        Assert.False(HasButton(NewWindow().ClaudeCodeSection(), "Got it"));

        OrbweaverSettings.PeerInboundNoticeSeen = false;
        _world["/home/.claude"] = InboundSetting.Accepting;
        Assert.False(HasButton(NewWindow().ClaudeCodeSection(), "Got it"));

        _world["/home/.claude"] = InboundSetting.NotAccepting;
        OrbweaverSettings.ClaudeCodeEnabled = false;
        Assert.False(HasButton(NewWindow().ClaudeCodeSection(), "Got it"));
    }

    [AvaloniaFact]
    public void TheToggleIsDrawnOnceNotOncePerCardAndChatRows()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        var window = NewWindow();

        // The card carries it while it is up...
        Assert.Equal(1, InboundRowCount(window.ClaudeCodeSection()));

        // ...and the chat rows take it over afterwards.
        window.OnInboundNoticeDismissed();
        Assert.Equal(1, InboundRowCount(window.ClaudeCodeSection()));
    }

    [AvaloniaFact]
    public void GotItPersists()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        var window = NewWindow();

        ButtonNamed(window.InboundNoticeRows(), "Got it").RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(OrbweaverSettings.PeerInboundNoticeSeen);
        Assert.False(HasButton(window.ClaudeCodeSection(), "Got it"));
    }

    [AvaloniaFact]
    public void ConfirmingFromTheCardWorksToo()
    {
        _world["/home/.claude"] = InboundSetting.NotAccepting;
        var window = NewWindow();
        window.OnInboundToggled("/home/.claude", true);

        Assert.True(HasButton(window.ClaudeCodeSection(), PeerInbound.ConfirmLabel));

        window.OnInboundConfirmed("/home/.claude");

        Assert.Equal(InboundSetting.Accepting, _world["/home/.claude"]);
        Assert.False(HasButton(window.ClaudeCodeSection(), "Got it"));
    }

    // ---- search and the production seams ----

    [AvaloniaFact]
    public void TheSettingsFilterFindsTheRowByTheWordMessage()
    {
        OrbweaverSettings.PeerInboundNoticeSeen = true;
        _world["/home/.claude"] = InboundSetting.NotAccepting;

        var row = Assert.IsType<SettingsWindow.SettingsRow>(
            NewWindow().InboundRows().First(r => r is SettingsWindow.SettingsRow));

        Assert.True(SettingsFilter.Matches("message", row.SearchText));
    }

    [AvaloniaFact]
    public void TheProductionSeamsReadOnlyWhatTheyAreGiven()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "cb267-seams-" + Guid.NewGuid());
        Directory.CreateDirectory(scratch);
        try
        {
            // No settings.json in the scratch root: a missing file is "not accepting".
            Assert.Equal(InboundSetting.NotAccepting, InboundSeamBootstrap.ProductionState(scratch));

            File.WriteAllText(Path.Combine(scratch, "settings.json"), "{\"crossSessionInbound\":\"accept\"}");
            Assert.Equal(InboundSetting.Accepting, InboundSeamBootstrap.ProductionState(scratch));

            var shown = InboundSeamBootstrap.ProductionDisplayPath(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json"));
            Assert.StartsWith("~", shown);

            Assert.All(InboundSeamBootstrap.ProductionRoots(), root => Assert.True(Directory.Exists(root)));
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }
}
