using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// The header's third line, drawn through real Skia rather than the null
// renderer.
//
// tests/UiTests/ChatPanelHeaderMetaTests.cs asserts what the line says; these
// exist because the remaining questions are judgements nobody can settle from
// an assertion — whether three facts fit beside a 68pt portrait without the
// card growing taller than the conversation under it, whether a dim 11pt line
// under a dim 11pt subtitle is still legible, and whether the accent on a far
// machine reads as "look here" rather than as a broken colour.
//
// One capture per case worth a judgement: an ordinary local session with all
// three facts, a room with only one, and a mirrored session whose machine is
// coloured. The room capture is also the regression: it is the header shape
// CB-133 shipped, and what it has to show is two lines that did not move.
//
// The machine name in every image is whatever the runner is called, which is
// the honest answer and part of what is being reviewed — a capture from the
// macOS leg and one from the Windows leg should differ in exactly that token.
[Collection("Settings")]
public class ChatHeaderFactsScreenshots : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    // Under the real home, because the panel asks Environment for it and the
    // `~/Source/...` collapse is half of what this capture is showing. No
    // directory is created: nothing here reads the path, and a capture that
    // wrote into somebody's home to draw a label would be a poor trade.
    private static readonly string Project = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Source", "HauntedMansionTerminalTheme");

    // Deliberately never closed — same reason as ChatPanelScreenshots: closing
    // a headless Window corrupts a process-wide FontManager cache for every
    // window built afterwards in this run.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    private static OrbWindow LocalOrb(string title, string cwd, string color = "")
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

    private string Track(string id)
    {
        _sessionIdsToClean.Add(id);
        return id;
    }

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.CloseFor(id);
    }

    [AvaloniaFact]
    public void ALocalHeaderShowsTheSessionNameItsFolderAndItsMachine()
    {
        // A persona on the title line is what gives the session's own name
        // somewhere to go, so this is the header at its fullest: a name, a
        // folder and a machine under "Leota" and her portrait's circle.
        var fake = new FakeChatSession(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "Where are you working?" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "In the theme repo, on this Mac — it says so under my name.",
            },
        })
        {
            SessionId = Track("chat-header-facts-local-" + Guid.NewGuid()),

            // What the header's first line reads. The status carries the
            // session's own name separately, which is the fact the new line
            // adds — set to different strings deliberately, so the capture
            // shows two facts rather than one repeated.
            DisplayName = "Leota",
        };

        ChatPanel.OpenFor(LocalOrb("haunted-mansion", Project, color: "purple"), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-header-facts-local.png");
    }

    [AvaloniaFact]
    public void ARoomHeaderKeepsItsChipsAndAddsOnlyTheMachine()
    {
        // A conversation in a channel is not running in a directory on this
        // disk and its room is already the title line, so one fact is left.
        // The two lines above it are the regression this image is for.
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            Source = SessionSource.OpenClaw,
            State = "idle",
            Kind = SessionKind.Channel,
            Title = "",
            Cwd = "",
        });

        var fake = new FakeChatSession(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "who is in here?" },
            new ChatTurn { Role = ChatRole.Assistant, Text = "Four of us, and the heartbeat." },
        })
        {
            SessionId = Track("chat-header-facts-room-" + Guid.NewGuid()),
            DisplayName = "#openclaw-management — wtvamp",
        };

        ChatPanel.OpenFor(orb, fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-header-facts-room.png");
    }

    [AvaloniaFact]
    public void AMirroredSessionWearsItsMachineInTheAccentColour()
    {
        // The case the colour exists for, and the one an assertion cannot
        // settle: whether a coloured token at the end of a deliberately quiet
        // line reads as "this is happening somewhere else" or just as a line
        // with an odd word in it.
        var far = new MirroredSession(
            Track("chat-header-facts-mirror-" + Guid.NewGuid()),
            "Aurora",
            "the-host-mac-mini");

        ChatPanel.OpenFor(LocalOrb("cut the release", Project, color: "green"), far);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-header-facts-mirrored.png");
    }

    // A session mirrored from another machine. FakeChatSession says nothing
    // about a machine — which is what a local session looks like — so the far
    // case needs its own, and the smallest possible one says clearly which
    // member the header actually reads.
    private sealed class MirroredSession : IRemoteChatSession, IRemoteChatMachine
    {
        public MirroredSession(string sessionId, string displayName, string machineName)
        {
            SessionId = sessionId;
            DisplayName = displayName;
            MachineName = machineName;
        }

        public string SessionId { get; }

        public string DisplayName { get; }

        public RemoteChatState State => RemoteChatState.Connected;

        public IReadOnlyList<ChatTurn> History { get; } = new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "is this running here?" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "No — on the mini. The line under my folder says which machine.",
            },
        };

        public string? MachineName { get; }

        public event Action? MachineChanged;

        public event Action<ChatTurn>? TurnAdded;

        public event Action<ChatTurn>? TurnUpdated;

        public event Action<RemoteChatState>? StateChanged;

        public Task SendAsync(string text)
        {
            // Nothing here sends: the capture is of a header.
            return Task.CompletedTask;
        }

        public void Cancel()
        {
            // Nothing is ever in flight.
        }
    }
}
