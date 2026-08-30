using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// A scan with sessions from another machine in it.
//
// Same harness as SessionScanTests and GatewayScanTests: SessionManager's internal
// constructor takes a scratch status directory, Start() is never called, and the
// snapshot is published through a test seam because the only thing that publishes
// one in production drives a live relay.
//
// A remote session is thin by nature. The peer list gives a name and a status word
// and nothing else — no hostname, no path, no transcript — so what this scan does
// is translate that into the few things an orb draws, and every one of those
// translations has a reason recorded beside it.
[Collection("Settings")]
public class RemoteScanTests
{
    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-rcscan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static SessionManager Manager(string statusDir)
    {
        var ctor = typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!;

        return (SessionManager)ctor.Invoke(new object[] { statusDir });
    }

    private static Dictionary<string, OrbWindow> Orbs(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (Dictionary<string, OrbWindow>)field.GetValue(manager)!;
    }

    private static void Publish(params RemoteControlSessions.Remote[] remotes)
    {
        ClaudeBuddySettings.RemoteControlEnabled = true;
        RemoteControlSessions.SetSnapshotForTests(remotes);
    }

    private static void PublishNothing() =>
        RemoteControlSessions.SetSnapshotForTests(Array.Empty<RemoteControlSessions.Remote>());

    private static RemoteControlSessions.Remote Remote(
        string name, string status = "idle", string account = ".claude", string? colour = null) =>
        new(name, "bridge:session_01", status, DateTime.UtcNow, account, colour);

    // Skipped where the bridge cannot run at all — it is tmux-based, so a Windows
    // runner has no remote sessions to scan and Snapshot answers empty whatever
    // is published. Asserting otherwise there would be asserting the skip.
    // Every transport this app has is cross-platform now, so there is no
    // longer a platform to gate on. Kept as a constant rather than removed,
    // because the tests that read it are asserting "the feature is available",
    // which is still a real question and may acquire a new answer.
    private static bool Supported => true;

    [AvaloniaFact]
    public void ARemoteSessionGetsAnOrb()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("rc:.claude:mac-mini", Orbs(manager).Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Two accounts can hold identically-named sessions — the same person naming
    // things the same way twice is the normal case — so the account is part of the
    // key. Without it they collapse onto one orb and one chat panel, with messages
    // going to whichever the dictionary happened to hold.
    [AvaloniaFact]
    public void TwoAccountsWithTheSameSessionNameGetTwoOrbs()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(
                Remote("mac-mini", account: ".claude"),
                Remote("mac-mini", account: ".claude-work"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);

            Assert.Contains("rc:.claude:mac-mini", orbs.Keys);
            Assert.Contains("rc:.claude-work:mac-mini", orbs.Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // The peer list's own word, translated into the two states an orb draws.
    // "running" is the one that matters and the one the first version missed: the
    // vocabulary is not `claude agents --json`'s, which prints "busy", so a remote
    // session sat still for the entire time a machine elsewhere was working.
    [AvaloniaFact]
    public void AWorkingRemoteSessionReadsAsGenerating()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("busy-box", status: "running"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("generating", manager.StatusFor("rc:.claude:busy-box")!.State);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Anything not recognisably work counts as idle: an orb that spins forever
    // because a label changed upstream is worse than one that never spins.
    [AvaloniaFact]
    public void AnUnrecognisedStatusReadsAsIdle()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("quiet-box", status: "some-word-from-a-later-cli"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("idle", manager.StatusFor("rc:.claude:quiet-box")!.State);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Its name on the other machine is all the peer list gives, and it is
    // deliberately not padded out with a guess.
    [AvaloniaFact]
    public void TheTitleIsTheNameOnTheOtherMachine()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("mac-mini", manager.StatusFor("rc:.claude:mac-mini")!.Title);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Marked Remote, which is what draws the two-way arrow badge — the exception
    // worth marking, because almost every orb on screen is local and clicking a
    // remote one opens a chat instead of jumping to a terminal.
    [AvaloniaFact]
    public void ARemoteSessionIsMarkedAsRemote()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal(SessionKind.Remote, manager.StatusFor("rc:.claude:mac-mini")!.Kind);
        }
        finally
        {
            PublishNothing();
        }
    }

    // What the session itself said, when it has been asked and answered. A remote
    // colour cannot be derived here: a peer row carries neither the transcript
    // /color writes into nor the cwd auto-colour hashes.
    [AvaloniaFact]
    public void AnAnsweredColourIsUsed()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini", colour: "green"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("green", manager.StatusFor("rc:.claude:mac-mini")!.Color);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A finding, asserted as it behaves rather than as its comment promises.
    //
    // The call site says the colour "falls back to a colour hashed from the name,
    // which is stable per session ... better than every remote orb being
    // identical while the answer is still in flight, or if it never comes." It
    // does not. The fallback is OpenClawSessions.ColourForAgent, which is a lookup
    // into the colours dealt out over the *last gateway listing* — and a remote
    // session's name is never in one, because it comes from a peer list on another
    // machine and not from the gateway at all. So it answers "" every time, and
    // every remote orb with no answered colour is identical: exactly the outcome
    // the comment says it avoids.
    //
    // Left as it is rather than fixed here. It is cosmetic, the intent is written
    // down, and changing what colour a user's orbs are drawn in is a visible
    // behaviour change that belongs in its own ticket rather than riding along in
    // a coverage pass. This test is the record, and it will start failing the day
    // somebody implements the fallback — which is the right moment to notice.
    [AvaloniaFact]
    public void AnUnansweredColourIsEmptyDespiteTheCommentPromisingAFallback()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"), Remote("linux-box"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var one = manager.StatusFor("rc:.claude:mac-mini")!.Color;
            var two = manager.StatusFor("rc:.claude:linux-box")!.Color;

            Assert.True(string.IsNullOrEmpty(one));
            Assert.True(string.IsNullOrEmpty(two));
        }
        finally
        {
            PublishNothing();
        }
    }

    // ...and an answered colour still arrives, which is what makes the gap above
    // cosmetic rather than total: a remote session that has been asked and
    // answered is coloured correctly.
    [AvaloniaFact]
    public void AnAnsweredColourStillArrivesForEachSession()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini", colour: "green"), Remote("linux-box", colour: "blue"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal("green", manager.StatusFor("rc:.claude:mac-mini")!.Color);
            Assert.Equal("blue", manager.StatusFor("rc:.claude:linux-box")!.Color);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Turning the feature off takes the orbs away on the next scan rather than at
    // the next launch, which is what the switch promises.
    [AvaloniaFact]
    public void TurningRemoteControlOffTakesItsOrbsAway()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();
            Assert.NotEmpty(Orbs(manager));

            ClaudeBuddySettings.RemoteControlEnabled = false;
        // Both transports, because "off" is now two switches. A test that
        // turns one off and leaves the other to whatever the last test set
        // is asserting about a state it did not arrange — and settings here
        // persist through ReloadForTests, since the setter writes the file.
        ClaudeBuddySettings.PeerLinkEnabled = false;
            manager.ScanAndUpdate();

            Assert.Empty(Orbs(manager));
        }
        finally
        {
            ClaudeBuddySettings.RemoteControlEnabled = true;
            PublishNothing();
        }
    }

    // --- talking to a remote session's conversation ---

    private static IDictionary<string, SessionStatus> Statuses(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_statuses", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IDictionary<string, SessionStatus>)field.GetValue(manager)!;
    }

    // Cached like the local ones: the conversation exists only in memory, so
    // rebuilding it on every click would throw the whole exchange away rather
    // than merely losing scroll position.
    [AvaloniaFact]
    public void RemoteChatForARemoteSessionIsCachedOnceCreated()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini", account: ".claude"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var chat = manager.RemoteChatFor("rc:.claude:mac-mini");
            Assert.NotNull(chat);
            Assert.Equal("mac-mini", chat!.DisplayName);

            Assert.Same(chat, manager.RemoteChatFor("rc:.claude:mac-mini"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // The id's own "rc:<account>:<name>" shape is what the scan always mints,
    // but the account/name split falls back to the status's title and the
    // default profile dir for an id that does not have a second colon in it
    // — which nothing on the scan path produces today, but the source
    // comment is explicit that the split is "the first separator after the
    // prefix", implying one might not be there.
    [AvaloniaFact]
    public void RemoteChatForAnIdWithNoAccountSeparatorFallsBackToTheTitleAndDefaultAccount()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        var manager = Manager(scratch.Dir);

        Statuses(manager)["rc:onlyname"] = new SessionStatus
        {
            Source = SessionSource.RemoteControl,
            Title = "fallback-name",
        };

        var chat = manager.RemoteChatFor("rc:onlyname");

        Assert.NotNull(chat);
        Assert.Equal("fallback-name", chat!.DisplayName);
    }

    // OnRemoteMessage and OnRemoteWorkingChanged are what SessionManager.Start
    // wires RemoteControlSessions' two events to — reached here directly
    // because Start is excluded (it also creates a tray icon and a live
    // FileSystemWatcher, neither of which this is about). Delivery is only to
    // an already-open conversation, which is the behaviour under test: an
    // inbound message with no panel behind it is a reply to nothing.
    private static void InvokeOnRemoteMessage(SessionManager manager, BridgeProtocol.InboundMessage message) =>
        typeof(SessionManager).GetMethod("OnRemoteMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(manager, new object[] { message });

    private static void InvokeOnRemoteWorkingChanged(SessionManager manager, string sessionKey, bool working) =>
        typeof(SessionManager).GetMethod("OnRemoteWorkingChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(manager, new object[] { sessionKey, working });

    [AvaloniaFact]
    public void AnInboundMessageIsDeliveredToTheMatchingOpenConversation()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini", account: ".claude"));
            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var chat = manager.RemoteChatFor("rc:.claude:mac-mini")!;

            InvokeOnRemoteMessage(manager,
                new BridgeProtocol.InboundMessage("mac-mini", "bridge:session_01", "prompting", "hello back", ".claude"));

            Assert.Contains(chat.History, t => t.Text == "hello back");
        }
        finally
        {
            PublishNothing();
        }
    }

    // A remote session named the same as one on a different account must not
    // receive a reply meant for the other — matched on both name and account.
    [AvaloniaFact]
    public void AnInboundMessageForADifferentAccountIsNotDelivered()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini", account: ".claude"));
            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var chat = manager.RemoteChatFor("rc:.claude:mac-mini")!;
            var before = chat.History.Count;

            InvokeOnRemoteMessage(manager,
                new BridgeProtocol.InboundMessage("mac-mini", "bridge:session_02", "prompting", "not for you", ".claude-work"));

            Assert.Equal(before, chat.History.Count);
        }
        finally
        {
            PublishNothing();
        }
    }

    [AvaloniaFact]
    public void WorkingChangedTogglesTheMatchingCachedConversation()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        try
        {
            Publish(Remote("mac-mini", account: ".claude"));
            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var chat = manager.RemoteChatFor("rc:.claude:mac-mini")!;

            InvokeOnRemoteWorkingChanged(manager, "rc:.claude:mac-mini", true);

            Assert.Contains(chat.History, t => t.Text.Contains("is working", StringComparison.Ordinal));
        }
        finally
        {
            PublishNothing();
        }
    }

    // A key with no open conversation behind it is simply not found — this is
    // what "delivered only to an already-open conversation" means in practice.
    [AvaloniaFact]
    public void WorkingChangedForASessionWithNoOpenConversationDoesNothing()
    {
        if (!Supported) return;

        using var scratch = new Scratch();
        var manager = Manager(scratch.Dir);

        InvokeOnRemoteWorkingChanged(manager, "rc:.claude:never-opened", true);
    }
}
