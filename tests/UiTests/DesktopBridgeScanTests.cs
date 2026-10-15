using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-266: a Claude Desktop chat on Remote Control appears twice if nothing
// stops it, once as the local session the hook recorded and once as the cloud
// row the account API lists for the same conversation. These drive the whole
// scan (the status file, the bridge lookup seam, the cloud snapshot) and assert
// on the orbs that come out, with a control that would have shown two orbs had
// the shadow rule been wrong.
//
// Same harness as CloudScanTests: a scratch status directory, no Start(), and
// the cloud snapshot arriving through SetSnapshotForTests. The bridge lookup is
// handed over as a seam so no test reads this machine's Claude Desktop profiles.
[Collection("Settings")]
public class DesktopBridgeScanTests
{
    // Invented ids with the measured shapes. Nothing here names a real chat.
    private const string BridgeId = "session_01BridgeFixtureAAAA";
    private const string Local = "11111111-2222-3333-4444-555555555555";
    private const string HostId = "local_00000000-aaaa-bbbb-cccc-000000000003";

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-bridgescan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Write(string sessionId, SessionStatus status) =>
            File.WriteAllText(Path.Combine(Dir, sessionId + ".txt"),
                System.Text.Json.JsonSerializer.Serialize(status));

        public void WriteDesktopChat(string sessionId) => Write(sessionId, new SessionStatus
        {
            State = "idle",
            Entrypoint = SessionStatus.DesktopEntrypoint,
            HostSessionId = HostId,
            Cwd = "/Users/user/project",
            SessionPid = Environment.ProcessId,
        });

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static SessionManager Manager(string dir, Func<SessionStatus, string?> bridge)
    {
        ClaudeBuddySettings.ClaudeCodeEnabled = true;

        return new SessionManager(
            dir,
            () => new Dictionary<string, string>(),
            () => new HashSet<string>(StringComparer.Ordinal),
            dependents: _ => SessionDependents.Nothing,
            stopJob: (_, _) => false,
            desktopBridge: bridge);
    }

    private static T Field<T>(SessionManager manager, string name) =>
        (T)typeof(SessionManager).GetField(
            name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;

    private static List<string> OrbIds(SessionManager manager) =>
        Field<Dictionary<string, OrbWindow>>(manager, "_windows").Keys.OrderBy(k => k).ToList();

    private static SessionStatus StatusOf(SessionManager manager, string id) =>
        Field<Dictionary<string, SessionStatus>>(manager, "_statuses")[id];

    private static void PublishCloud(string id)
    {
        ClaudeBuddySettings.ClaudeCloudEnabled = true;
        ClaudeCloudSessions.SetSnapshotForTests(new[]
        {
            new ClaudeCloudSessions.Session(
                Id: id, Title: "Same chat", State: "idle", LastActivity: DateTime.UtcNow.AddSeconds(-5),
                Url: "https://claude.ai/code/" + id, StatusBucket: "idle", NeedsAction: false,
                Model: "claude-opus-5", ContextPercent: null, StatusDetail: null, RecentAction: null,
                OwnerRoot: null),
        });
    }

    private static void PublishNothing()
    {
        ClaudeCloudSessions.SetSnapshotForTests(Array.Empty<ClaudeCloudSessions.Session>());
        ClaudeBuddySettings.ClaudeCloudEnabled = false;
    }

    [AvaloniaFact]
    public void ABridgedDesktopChatAndItsCloudTwinAreOneOrbKeyedByTheLocalId()
    {
        using var scratch = new Scratch();
        try
        {
            scratch.WriteDesktopChat(Local);
            PublishCloud(BridgeId);

            var manager = Manager(scratch.Dir, _ => BridgeId);
            manager.ScanAndUpdate();

            Assert.Equal(new[] { Local }, OrbIds(manager));
            var status = StatusOf(manager, Local);
            Assert.Equal(BridgeId, status.BridgeSessionId);
            Assert.True(status.IsBridgedDesktop);
        }
        finally
        {
            PublishNothing();
        }
    }

    // The control: the same two rows, but the chat's bridge is a different
    // session (or none), so the cloud row is a conversation of its own and both
    // orbs stay. Without it the case above would pass on a scan that simply
    // dropped every cloud row while a Desktop chat was around.
    [AvaloniaTheory]
    [InlineData("session_01SomeOtherBridgeBBBB")]
    [InlineData(null)]
    public void ADesktopChatBridgedElsewhereOrNotAtAllLeavesTheCloudRowAlone(string? bridge)
    {
        using var scratch = new Scratch();
        try
        {
            scratch.WriteDesktopChat(Local);
            PublishCloud(BridgeId);

            var manager = Manager(scratch.Dir, _ => bridge);
            manager.ScanAndUpdate();

            Assert.Equal(new[] { Local, "cloud:" + BridgeId }, OrbIds(manager));
            Assert.Equal(bridge ?? "", StatusOf(manager, Local).BridgeSessionId);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A terminal session never asks: the lookup is for Desktop chats only, so a
    // seam that throws proves the scan did not call it.
    [AvaloniaFact]
    public void ATerminalSessionIsNeverAskedForABridge()
    {
        using var scratch = new Scratch();
        scratch.Write(Local, new SessionStatus
        {
            State = "idle",
            Entrypoint = "cli",
            Cwd = "/Users/user/project",
            SessionPid = Environment.ProcessId,
            TermProgram = "iTerm.app",
            Tty = "/dev/ttys004",
        });

        var manager = Manager(scratch.Dir, _ => throw new InvalidOperationException("asked"));
        manager.ScanAndUpdate();

        Assert.Equal("", StatusOf(manager, Local).BridgeSessionId);
    }
}
