using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// Captures for CB-164's visible surfaces: the cloud orb itself, and the settings
// group in both of its states.
//
// Hand-written one per scenario, because adding a case to tests/UiTests does not
// generate its capture — that suite renders through the null renderer and this
// one through real Skia, which is the whole reason both exist.
//
// No clicks, for the reason the other orb captures give: OrbWindow's pointer
// handling reaches TerminalFocuser, unguarded at its own entry point.
[Collection("Settings")]
public class CloudSessionScreenshots : IDisposable
{
    // ChatPanel is one window shared by every test in the process, so each
    // capture that opens one hides its own session again rather than relying on
    // isolation that does not exist. Same rule as ChatPanelScreenshots.
    private readonly List<string> _panelsToClean = new();

    public void Dispose()
    {
        foreach (var id in _panelsToClean) ChatPanel.HideFor(id);
    }

    private static SessionStatus CloudStatus(int? contextPercent) => new()
    {
        Source = SessionSource.ClaudeCloud,
        Kind = SessionKind.Cloud,
        State = "idle",
        Title = "Refactor the parser",
        Cwd = "",
        Url = "https://claude.ai/code/session_01abc",
        ContextPercent = contextPercent,
        StatusDetail = "Editing files",
        RecentAction = "Ran the tests",
    };

    // The badge and the ring together, since they are the two things that make
    // this orb different from every other one on the screen and a reviewer
    // comparing rids is looking for both.
    [AvaloniaFact]
    public void ACloudOrbWearsTheCloudBadgeAndAContextRing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(contextPercent: 72));

        ScreenshotHelper.Capture(orb, "orb-window-cloud-session.png");
    }

    // And with no reading at all, which is the common case on a session nobody
    // has reported a context percentage for. The two captures side by side are
    // what shows that "no ring" is a state rather than a rendering failure.
    [AvaloniaFact]
    public void ACloudOrbWithNoContextReadingDrawsNoRing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(contextPercent: null));

        ScreenshotHelper.Capture(orb, "orb-window-cloud-session-no-ring.png");
    }

    // The chat panel for a cloud session: a transcript with no box under it, and
    // a sentence where the box was.
    //
    // This is the capture that matters most of the four, because what it shows
    // is a *judgement* rather than a mechanism — whether one line of grey text
    // is enough for somebody who clicked an orb expecting to be able to reply,
    // and whether the panel reads as deliberate rather than as one that failed
    // to finish drawing. No test can answer that; the picture can.
    [AvaloniaFact]
    public void ACloudSessionsPanelShowsItsTranscriptAndNoComposer()
    {
        var id = "screenshot-cloud-" + Guid.NewGuid();
        _panelsToClean.Add(id);

        var fake = new FakeChatSession(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "refactor the transcript parser" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Pulled the envelope handling out into its own type and left the row "
                       + "mapping alone — the second half was already covered.",
            },
        })
        {
            SessionId = id,
            DisplayName = "Refactor the parser",
            IsReadOnly = true,
            ComposerHint = "This conversation is read-only here.",
            ReplyUrl = "https://claude.ai/code/session_01abc",
            MachineName = "Anthropic's cloud",
        };

        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-cloud-read-only.png");
    }

    // The settings group, switched off: one row and the help text that names the
    // Keychain prompt. That sentence is the whole of the feature's first-run
    // story, so it is worth having a picture of it on both runners.
    [AvaloniaFact]
    public void TheCloudSettingsGroupOff()
    {
        Capture(enabled: false, "settings-claude-cloud-off.png");
    }

    // ...and switched on, where progressive disclosure adds the status line and
    // the retry button. Nothing here is platform-gated — it is an HTTPS poll and
    // a Keychain read, and the Windows leg reads its credential from the CLI's
    // own file instead — so the two rids should show the same card. A Windows
    // capture missing these rows is the regression this exists to make visible.
    [AvaloniaFact]
    public void TheCloudSettingsGroupOn()
    {
        Capture(enabled: true, "settings-claude-cloud-on.png");
    }

    private static void Capture(bool enabled, string name)
    {
        var was = ClaudeBuddySettings.ClaudeCloudEnabled;
        try
        {
            ClaudeBuddySettings.ClaudeCloudEnabled = enabled;

            // A state a running app would actually be in. Left alone the line
            // reads "off" even in the switched-on capture — truthfully, since
            // nothing called Restart() here — and a picture of the enabled
            // section whose status says off is a picture that teaches a
            // reviewer the wrong thing about the feature.
            ClaudeCloudSessions.SetStateForTests("checking\u2026");

            var ctor = typeof(SettingsWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                types: Type.EmptyTypes)
                ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

            var window = (Window)ctor.Invoke(null);

            // Shown and flushed so the page is measured and arranged — these
            // stack panels are not virtualized, so every row lays out even
            // though this group sits well below the viewport.
            window.Show();
            ScreenshotHelper.Flush();

            // Anchored on the *group heading* rather than on a row, and taking
            // its immediate parent — Group() builds a StackPanel whose first
            // child is that heading and whose second is the whole card, so the
            // parent is exactly the section and nothing else.
            //
            // The height-and-width heuristic the neighbouring captures use does
            // not work here: this section's first row carries four lines of help
            // text, which makes the row itself taller than the threshold, so the
            // walk stopped there and the capture showed the switch alone — with
            // the status line and the retry button, the two things the switched-
            // on scenario exists to show, cropped out of frame. Caught by
            // looking at the PNG, which is the only thing that could have caught
            // it: the test passed either way.
            var heading = window.GetLogicalDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(block => block.Text == "Claude Code in the cloud");

            Assert.NotNull(heading);

            var group = (Control)heading!.GetLogicalParent()!;

            ScreenshotHelper.CaptureControl(group, name);
        }
        finally
        {
            ClaudeCloudSessions.SetStateForTests("off");
            ClaudeBuddySettings.ClaudeCloudEnabled = was;
        }
    }
}
