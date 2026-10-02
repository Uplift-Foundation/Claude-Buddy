using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-225: the session half of archiving and deleting a cloud session — which
// login asks, what happens to the orb the moment it succeeds, and what an open
// chat panel on it then says.
//
// In this suite rather than tests/UnitTests because every case flips the
// process-wide cloud setting and the static snapshot, and the Settings
// collection is what keeps that from racing the classes that read them.
[Collection("Settings")]
public class CloudLifecycleSessionTests
{
    private const string Token = "sk-ant-oat01-CANARY-ACCESS-abcdef0123456789";

    private static ClaudeCloudSessions.Session Session(string id = "session_01abc", string? owner = null) =>
        new(
            Id: id,
            Title: "Refactor the parser",
            State: "idle",
            LastActivity: DateTime.UtcNow.AddSeconds(-5),
            Url: "https://claude.ai/code/" + id,
            StatusBucket: "idle",
            NeedsAction: false,
            Model: null,
            ContextPercent: null,
            StatusDetail: null,
            RecentAction: null,
            OwnerRoot: owner);

    private sealed class FakeApi : ICloudApi
    {
        private readonly Func<CloudRequestContext, CloudApiResult> _answer;

        internal FakeApi(Func<CloudRequestContext, CloudApiResult> answer) => _answer = answer;

        internal List<CloudRequestContext> Requests { get; } = new();

        public Task<CloudApiResult> SendAsync(CloudRequestContext context, CancellationToken token)
        {
            Requests.Add(context);
            return Task.FromResult(_answer(context));
        }
    }

    private sealed class Login : ICloudCredentialSource
    {
        public string? Stamp() => "stamp";
        public CredentialRead Read() => new(CredentialOutcome.Found, Token, null, "present");
    }

    private sealed class NoLogin : ICloudCredentialSource
    {
        public string? Stamp() => null;
        public CredentialRead Read() => new(CredentialOutcome.NotLoggedIn, null, null, "none");
    }

    private static CloudApiResult Answer(int status, string? body) =>
        new(CloudOutcomes.OutcomeFor(status, body), body);

    private const string NotFoundJson =
        """{"error":{"message":"Session session_01abc not found","type":"not_found_error"},"request_id":"r","type":"error"}""";

    // A delete the endpoint takes and the confirming read agrees with.
    private static FakeApi Accepting() => new(c => c.Method is null ? Answer(404, NotFoundJson) : Answer(200, "{}"));

    private sealed class Scope : IDisposable
    {
        private readonly ICloudApi _api;
        private readonly Func<string?, ICloudCredentialSource> _creds;

        internal List<string?> RootsAsked { get; } = new();

        internal Scope(ICloudApi api, ICloudCredentialSource? login = null, bool enabled = true,
            params ClaudeCloudSessions.Session[] sessions)
        {
            _api = ClaudeCloudSessions.LifecycleApi;
            _creds = ClaudeCloudSessions.LifecycleCredentials;

            ClaudeBuddySettings.ClaudeCloudEnabled = enabled;
            ClaudeCloudSessions.ClearTombstonesForTests();
            ClaudeCloudSessions.SetSnapshotForTests(sessions);
            ClaudeCloudSessions.LifecycleApi = api;
            ClaudeCloudSessions.LifecycleCredentials = root =>
            {
                RootsAsked.Add(root);
                return login ?? new Login();
            };
        }

        public void Dispose()
        {
            ClaudeCloudSessions.LifecycleApi = _api;
            ClaudeCloudSessions.LifecycleCredentials = _creds;
            ClaudeCloudSessions.ClearTombstonesForTests();
            ClaudeCloudSessions.SetSnapshotForTests(Array.Empty<ClaudeCloudSessions.Session>());
            ClaudeBuddySettings.ClaudeCloudEnabled = false;
        }
    }

    // --- the tombstone ----------------------------------------------------------

    [Fact]
    public void ATombstonedSessionIsHiddenFromTheSnapshotAtOnce()
    {
        using var scope = new Scope(Accepting(), sessions: new[] { Session("session_01abc"), Session("session_02def") });

        ClaudeCloudSessions.Tombstone("session_01abc", DateTime.UtcNow);

        Assert.Equal(new[] { "session_02def" }, ClaudeCloudSessions.Snapshot().Select(s => s.Id));
    }

    // The roster agreeing — the next publish no longer lists it — lets go.
    [Fact]
    public void ARosterThatNoLongerListsItClearsTheTombstone()
    {
        using var scope = new Scope(Accepting(), sessions: Session("session_01abc"));
        ClaudeCloudSessions.Tombstone("session_01abc", DateTime.UtcNow);

        ClaudeCloudSessions.Publish(new[] { Session("session_02def") });

        Assert.False(ClaudeCloudSessions.IsTombstoned("session_01abc"));
    }

    // The roster still listing it is the disagreement case, and the tombstone
    // holds — hiding it is still the 2xx's answer until the hold runs out.
    [Fact]
    public void ARosterThatStillListsItKeepsTheTombstone()
    {
        using var scope = new Scope(Accepting(), sessions: Session("session_01abc"));
        ClaudeCloudSessions.Tombstone("session_01abc", DateTime.UtcNow);

        ClaudeCloudSessions.Publish(new[] { Session("session_01abc") });

        Assert.True(ClaudeCloudSessions.IsTombstoned("session_01abc"));
        Assert.Empty(ClaudeCloudSessions.Snapshot());
    }

    // And when the hold runs out, a session the account still lists is shown
    // again rather than hidden for good.
    [Fact]
    public void AnExpiredTombstoneShowsTheSessionAgain()
    {
        using var scope = new Scope(Accepting(), sessions: Session("session_01abc"));
        ClaudeCloudSessions.Tombstone("session_01abc",
            DateTime.UtcNow - ClaudeCloudSessions.TombstoneHold - TimeSpan.FromSeconds(1));

        Assert.Single(ClaudeCloudSessions.Snapshot());
        Assert.False(ClaudeCloudSessions.IsTombstoned("session_01abc"));
    }

    [Fact]
    public void NoTombstonesLeavesTheListUntouched()
    {
        var sessions = new[] { Session() };
        Assert.Same(sessions, ClaudeCloudSessions.WithoutTombstones(sessions, new HashSet<string>()));
    }

    [Fact]
    public void PublishingWithNoTombstonesJustPublishes()
    {
        using var scope = new Scope(Accepting());

        ClaudeCloudSessions.Publish(new[] { Session("session_09xyz") });

        Assert.Equal("session_09xyz", Assert.Single(ClaudeCloudSessions.Snapshot()).Id);
    }

    // --- the action ---------------------------------------------------------------

    // **The owner's login, never "whichever is first"** (CB-221).
    [Fact]
    public async Task TheActionAsksAsTheAccountThatOwnsTheSession()
    {
        var api = Accepting();
        using var scope = new Scope(api, sessions: Session("session_01abc", owner: "/home/u/.claude-board"));

        var result = await ClaudeCloudSessions.RunLifecycleAsync(CloudLifecycleAction.Archive,
            "cloud:session_01abc", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new string?[] { "/home/u/.claude-board" }, scope.RootsAsked);
        Assert.Equal("/v1/code/sessions/session_01abc/archive", Assert.Single(api.Requests).Path);
    }

    [Theory]
    [InlineData(CloudLifecycleAction.Archive)]
    [InlineData(CloudLifecycleAction.Delete)]
    public async Task ASuccessHidesTheOrbsSessionAtOnce(CloudLifecycleAction action)
    {
        using var scope = new Scope(Accepting(), sessions: Session("session_01abc"));

        var result = await ClaudeCloudSessions.RunLifecycleAsync(action, "cloud:session_01abc", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(ClaudeCloudSessions.IsTombstoned("session_01abc"));
        Assert.Empty(ClaudeCloudSessions.Snapshot());
    }

    [Fact]
    public async Task ARefusalLeavesTheOrbWhereItIs()
    {
        var api = new FakeApi(_ => Answer(403, """{"type":"error","error":{"type":"permission_error"},"request_id":"r"}"""));
        using var scope = new Scope(api, sessions: Session("session_01abc"));

        var result = await ClaudeCloudSessions.RunLifecycleAsync(CloudLifecycleAction.Archive,
            "cloud:session_01abc", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(CloudOutcomes.AccountBlockedDetail, result.Detail);
        Assert.False(ClaudeCloudSessions.IsTombstoned("session_01abc"));
        Assert.Single(ClaudeCloudSessions.Snapshot());
    }

    [Fact]
    public async Task NoLoginIsARefusalAndSendsNothing()
    {
        var api = Accepting();
        using var scope = new Scope(api, new NoLogin(), sessions: Session("session_01abc"));

        var result = await ClaudeCloudSessions.RunLifecycleAsync(CloudLifecycleAction.Delete,
            "cloud:session_01abc", CancellationToken.None);

        Assert.Equal(ClaudeCloudLifecycle.NoCredentialDetail, result.Detail);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task WithCloudSessionsSwitchedOffNothingIsAsked()
    {
        var api = Accepting();
        using var scope = new Scope(api, enabled: false, sessions: Session("session_01abc"));

        var result = await ClaudeCloudSessions.RunLifecycleAsync(CloudLifecycleAction.Delete,
            "cloud:session_01abc", CancellationToken.None);

        Assert.Equal(ClaudeCloudSessions.SwitchedOffDetail, result.Detail);
        Assert.Empty(api.Requests);
        Assert.Empty(scope.RootsAsked);
    }

    // A key the roster has no row for — gone since the menu opened, or not a
    // cloud key at all — asks nothing, and says why.
    [Theory]
    [InlineData("cloud:session_09gone")]
    [InlineData("cloud:not-a-session")]
    [InlineData("rc:board:session_01abc")]
    [InlineData(null)]
    public async Task AnUnlistedOrMalformedKeyAsksNothing(string? key)
    {
        var api = Accepting();
        using var scope = new Scope(api, sessions: Session("session_01abc"));

        var result = await ClaudeCloudSessions.RunLifecycleAsync(CloudLifecycleAction.Archive, key, CancellationToken.None);

        Assert.Equal(ClaudeCloudSessions.NotListedDetail, result.Detail);
        Assert.Empty(api.Requests);
    }

    // --- the orb, and an open panel on it ----------------------------------------

    private static Dictionary<string, OrbWindow> Orbs(SessionManager manager) =>
        (Dictionary<string, OrbWindow>)typeof(SessionManager)
            .GetField("_windows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;

    private static SessionManager Manager(string statusDir) =>
        (SessionManager)typeof(SessionManager)
            .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!
            .Invoke(new object[] { statusDir });

    // The whole path a user sees: the orb is there, the archive lands, and on
    // the very next scan the orb has gone and an open panel on it says the
    // session was archived or deleted and offers no composer — CB-199's Gone
    // path, reached because the tombstone makes the roster row disappear.
    [AvaloniaFact]
    public async Task ASucceededArchiveTakesTheOrbAndTurnsAnOpenPanelReadOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-cloudlife-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        using var scope = new Scope(Accepting(), sessions: Session("session_01abc"));
        try
        {
            var manager = Manager(dir);
            manager.UseCloudChatDependenciesForTests(new FakeApi(_ => Answer(401, "")), new NoLogin());
            manager.ScanAndUpdate();
            Assert.Contains("cloud:session_01abc", Orbs(manager).Keys);

            var chat = (ClaudeCloudChatSession)manager.RemoteChatFor("cloud:session_01abc")!;
            Assert.False(chat.IsReadOnly);

            var result = await ClaudeCloudSessions.RunLifecycleAsync(CloudLifecycleAction.Archive,
                "cloud:session_01abc", CancellationToken.None);
            Assert.True(result.Succeeded);

            manager.ScanAndUpdate();

            Assert.DoesNotContain("cloud:session_01abc", Orbs(manager).Keys);
            Assert.True(chat.IsReadOnly);
            Assert.Equal(CloudChatSendability.GoneHint, chat.ComposerHint);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // The control: the same scan with nothing archived keeps the orb and the
    // panel writable, so the test above is about the archive and not the scan.
    [AvaloniaFact]
    public void WithoutAnArchiveTheOrbAndThePanelStay()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-cloudlife-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        using var scope = new Scope(Accepting(), sessions: Session("session_01abc"));
        try
        {
            var manager = Manager(dir);
            manager.UseCloudChatDependenciesForTests(new FakeApi(_ => Answer(401, "")), new NoLogin());
            manager.ScanAndUpdate();
            var chat = (ClaudeCloudChatSession)manager.RemoteChatFor("cloud:session_01abc")!;

            manager.ScanAndUpdate();

            Assert.Contains("cloud:session_01abc", Orbs(manager).Keys);
            Assert.False(chat.IsReadOnly);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
