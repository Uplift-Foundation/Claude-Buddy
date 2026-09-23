using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// A scan with Claude Code's cloud sessions in it, and what the orbs it makes
// for them look like.
//
// Same harness as GatewayScanTests next door — SessionManager's internal
// constructor takes a scratch status directory and Start() is never called —
// and the snapshot arrives through ClaudeCloudSessions.SetSnapshotForTests,
// because the only thing that publishes one in production is the poll loop,
// which needs a real credential and a real endpoint and is excluded from
// coverage for exactly that reason.
//
// What is being tested here is mostly the *absence* of things. A cloud session
// has no process, no terminal, no transcript and no working directory, and
// nearly every rule in SessionManager and OrbWindow was written for a session
// that has all four. So the interesting assertions are that the path row is not
// there, that the reset item refuses, that no CLI mark is drawn, and that a
// click does not go looking for a pane.
//
// No clicks anywhere, per the rule this whole suite keeps: OrbWindow's pointer
// handling reaches TerminalFocuser, which fires real tmux/ps/osascript
// processes off-thread on whatever machine runs the suite.
[Collection("Settings")]
public class CloudScanTests
{
    // The real clock, not a fixed date — ScanAndUpdate reads DateTime.UtcNow for
    // its lifetime check, so a fixture pinned to a made-up "now" produces
    // sessions that look hours stale and are expired before an orb exists. That
    // is how the first draft of GatewayScanTests failed every case, and the note
    // is repeated here because this file's stale-session control deliberately
    // relies on the same machinery working.
    private static DateTime Now => DateTime.UtcNow;

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cb-cloudscan-" + Guid.NewGuid());

        public Scratch() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static Dictionary<string, OrbWindow> Orbs(SessionManager manager)
    {
        var field = typeof(SessionManager).GetField(
            "_windows", BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (Dictionary<string, OrbWindow>)field.GetValue(manager)!;
    }

    private static SessionManager Manager(string statusDir)
    {
        var ctor = typeof(SessionManager).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string) })!;

        return (SessionManager)ctor.Invoke(new object[] { statusDir });
    }

    private static ClaudeCloudSessions.Session Session(
        string id = "session_01abc",
        string state = "idle",
        DateTime? lastActivity = null,
        int? contextPercent = null,
        bool needsAction = false,
        string? statusDetail = null,
        string? recentAction = null) =>
        new(
            Id: id,
            Title: "Refactor the parser",
            State: state,
            LastActivity: lastActivity ?? Now.AddSeconds(-5),
            Url: "https://claude.ai/code/" + id,
            StatusBucket: "idle",
            NeedsAction: needsAction,
            Model: "claude-opus-5",
            ContextPercent: contextPercent,
            StatusDetail: statusDetail,
            RecentAction: recentAction);

    private static void Publish(params ClaudeCloudSessions.Session[] sessions)
    {
        ClaudeBuddySettings.ClaudeCloudEnabled = true;
        ClaudeCloudSessions.SetSnapshotForTests(sessions);
    }

    private static void PublishNothing()
    {
        ClaudeCloudSessions.SetSnapshotForTests(Array.Empty<ClaudeCloudSessions.Session>());
        ClaudeBuddySettings.ClaudeCloudEnabled = false;
    }

    private static SessionStatus CloudStatus(
        int? contextPercent = null,
        string? statusDetail = null,
        string? recentAction = null) => new()
        {
            Source = SessionSource.ClaudeCloud,
            Kind = SessionKind.Cloud,
            State = "idle",
            Title = "Refactor the parser",
            Cwd = "",
            Url = "https://claude.ai/code/session_01abc",
            ContextPercent = contextPercent,
            StatusDetail = statusDetail,
            RecentAction = recentAction,
        };

    // --- the scan ---

    [AvaloniaFact]
    public void ACloudSessionGetsAnOrb()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01abc"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("cloud:session_01abc", Orbs(manager).Keys);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Namespaced for the reason the gateway's ids are: these share a dictionary
    // with Claude Code's own uuids, and an id that is not obviously foreign is
    // an id something eventually tries to open a status file for.
    [AvaloniaFact]
    public void CloudOrbIdsAreNamespaced()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01abc"), Session("session_02def"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var orbs = Orbs(manager);
            Assert.Equal(2, orbs.Count);
            Assert.All(orbs.Keys, id => Assert.StartsWith("cloud:", id));
        }
        finally
        {
            PublishNothing();
        }
    }

    // Off means off, and it is held one level down — Snapshot() itself returns
    // nothing rather than the scan filtering afterwards. Asserted here anyway,
    // because the promise the settings copy makes is about what the user sees.
    [AvaloniaFact]
    public void NoOrbsWhenTheSwitchIsOff()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01abc"));
            ClaudeBuddySettings.ClaudeCloudEnabled = false;

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Empty(Orbs(manager));
        }
        finally
        {
            PublishNothing();
        }
    }

    // **CB-182, through the scan rather than through the rule.**
    //
    // This case used to assert the opposite — that a cloud session ten minutes
    // quiet against a one-minute lifetime got no orb — and that was the bug
    // rather than the contract. "Keep orbs for" is about local sessions, where
    // silence means the process is probably gone; a cloud session has no process
    // to have exited, so the only thing its `updated_at` going quiet says is that
    // nobody has typed into it lately. Nineteen hours is the age measured on the
    // live account, where it was the only non-archived cloud session there was.
    //
    // Idle deliberately, not generating: a generating fixture was exempt from the
    // staleness check before this change too, so it would have passed either way
    // and proved nothing.
    [AvaloniaFact]
    public void ALongIdleCloudSessionStillGetsAnOrb()
    {
        using var scratch = new Scratch();
        var wasLifetime = ClaudeBuddySettings.OrbLifetimeMinutes;
        try
        {
            // The shortest the picker offers, so nothing longer can be what
            // carried this.
            ClaudeBuddySettings.OrbLifetimeMinutes = 1;
            Publish(Session("session_01old", state: "idle", lastActivity: Now.AddMinutes(-1146)));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("cloud:session_01old", Orbs(manager).Keys);
        }
        finally
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = wasLifetime;
            PublishNothing();
        }
    }

    // **The negative control, in one scan, which is what makes the case above
    // mean anything.** An exemption a clause too wide stops the clock for
    // everybody, and a suite that only ever asks about cloud orbs is green
    // either way. So: one cloud session and one local status file, the same age,
    // in the same pass, against the same setting. The cloud orb is drawn and the
    // local one is not — the sweep is still running, it just no longer reaches
    // the cloud.
    [AvaloniaFact]
    public void ALocalSessionOfTheSameAgeStillExpiresInTheSameScan()
    {
        using var scratch = new Scratch();
        var wasLifetime = ClaudeBuddySettings.OrbLifetimeMinutes;
        var wasClaudeCode = ClaudeBuddySettings.ClaudeCodeEnabled;
        try
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = 1;
            ClaudeBuddySettings.ClaudeCodeEnabled = true;
            Publish(Session("session_01old", state: "idle", lastActivity: Now.AddMinutes(-1146)));

            // The hooks' own shape: a live pid (this process, the one pid on the
            // machine certainly alive, so ProcessGone cannot be what drops it)
            // and a terminal, with an mtime as old as the cloud session's
            // last activity.
            var local = Path.Combine(scratch.Dir, "local-session.txt");
            File.WriteAllText(local, System.Text.Json.JsonSerializer.Serialize(new SessionStatus
            {
                State = "idle",
                Cwd = "/Users/user/project",
                SessionPid = Environment.ProcessId,
                TermProgram = "iTerm.app",
                Tty = "/dev/ttys004",
            }));
            File.SetLastWriteTimeUtc(local, Now.AddMinutes(-1146));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("cloud:session_01old", Orbs(manager).Keys);
            Assert.DoesNotContain("local-session", Orbs(manager).Keys);
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeEnabled = wasClaudeCode;
            ClaudeBuddySettings.OrbLifetimeMinutes = wasLifetime;
            PublishNothing();
        }
    }

    // **The other negative control: archiving is the retention policy, and it is
    // ClaudeCloudRoster.Keep that applies it — not the clock.**
    //
    // Both rows go through the real roster parse rather than being hand-built as
    // sessions, because that is the only way to show *which* rule dropped the
    // archived one. The archived row is recent and the kept one is nineteen hours
    // quiet, so a clock would have taken exactly the wrong one: the orb that
    // survives is the old one and the orb that never appears is the fresh one.
    [AvaloniaFact]
    public void AnArchivedCloudSessionDrawsNoOrbAndTheFilterIsWhatDroppedIt()
    {
        using var scratch = new Scratch();
        var wasLifetime = ClaudeBuddySettings.OrbLifetimeMinutes;
        try
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = 1;

            var reduction = ClaudeCloudRoster.Reduce(new[]
            {
                ClaudeCloudRoster.ParsePage(
                    "{\"data\":["
                    + RosterRow("session_01gone", "archived", Now.AddMinutes(-1))
                    + "," + RosterRow("session_01kept", "idle", Now.AddMinutes(-1146))
                    + "],\"has_more\":false}")
            }, truncated: false);

            // Said before the scan, so a scan drawing one orb cannot be read as
            // the filter working when it was really the roster arriving empty.
            Assert.Equal("session_01kept", Assert.Single(reduction.Sessions).Id);

            Publish(reduction.Sessions.ToArray());

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("cloud:session_01kept", Orbs(manager).Keys);
            Assert.DoesNotContain("cloud:session_01gone", Orbs(manager).Keys);
        }
        finally
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = wasLifetime;
            PublishNothing();
        }
    }

    // A roster row as the account API writes one, reduced to the fields these two
    // cases turn on. `session_url` and `session_context.cwd` are empty because
    // they were empty on every row measured — see ClaudeCloudRosterTests, whose
    // fixture this follows.
    private static string RosterRow(string id, string status, DateTime updated) =>
        "{\"id\":\"" + id + "\",\"environment_kind\":\"anthropic_cloud\""
        + ",\"session_status\":\"" + status + "\",\"status_bucket\":\"idle\""
        + ",\"title\":\"a session\",\"updated_at\":\""
        + updated.ToString("yyyy-MM-ddTHH:mm:ssZ")
        + "\",\"created_at\":\"2026-09-01T00:00:00Z\",\"session_url\":\"\""
        + ",\"session_context\":{\"cwd\":\"\"}}";

    // A recent cloud session on the same one-minute lifetime. It says less than
    // it did before CB-182 — both halves of the pair are Keep now — but it is
    // still the case that fails if the mapping stops producing a scan entry at
    // all, which is the only way the archived-session control above could pass
    // for the wrong reason.
    [AvaloniaFact]
    public void ARecentCloudSessionSurvivesTheSameLifetime()
    {
        using var scratch = new Scratch();
        var wasLifetime = ClaudeBuddySettings.OrbLifetimeMinutes;
        try
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = 1;
            Publish(Session("session_01new", state: "idle", lastActivity: Now.AddSeconds(-5)));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("cloud:session_01new", Orbs(manager).Keys);
        }
        finally
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = wasLifetime;
            PublishNothing();
        }
    }

    // No cwd, deliberately, exactly as the remote-control mapping leaves it:
    // ApplyPersona returns early on an empty one, so nothing builds a local
    // candidate path for a session that has no directory on this machine.
    [AvaloniaFact]
    public void ACloudSessionCarriesNoWorkingDirectory()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01abc"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            var status = manager.StatusFor("cloud:session_01abc");

            Assert.NotNull(status);
            Assert.Equal(SessionSource.ClaudeCloud, status!.Source);
            Assert.Equal(SessionKind.Cloud, status.Kind);
            Assert.True(string.IsNullOrEmpty(status.Cwd), "a cloud session has no directory here");
        }
        finally
        {
            PublishNothing();
        }
    }

    // The address survives the mapping, because it is what a click needs and the
    // orb is the only thing still holding it by then.
    [AvaloniaFact]
    public void TheSessionUrlReachesTheStatus()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01abc"));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal(
                "https://claude.ai/code/session_01abc",
                manager.StatusFor("cloud:session_01abc")!.Url);
        }
        finally
        {
            PublishNothing();
        }
    }

    // The roster's "this one wants you" flag is spent on the presence channel,
    // which already means exactly that — so the orb wears the same "?" a local
    // background job holding a question wears, rather than a second mark saying
    // nearly the same thing in a different shape.
    [AvaloniaFact]
    public void ACloudSessionWantingAttentionSaysSoOnThePresenceChannel()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01ask", needsAction: true));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal(
                OrbPresence.NeedsInput,
                manager.StatusFor("cloud:session_01ask")!.Presence);
        }
        finally
        {
            PublishNothing();
        }
    }

    // ...and one that does not is ordinary, which is the arm that would
    // otherwise never run — a flag only ever asserted true is a flag nothing
    // proves is read.
    [AvaloniaFact]
    public void ACloudSessionWantingNothingIsPresent()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01calm", needsAction: false));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Equal(
                OrbPresence.Present,
                manager.StatusFor("cloud:session_01calm")!.Presence);
        }
        finally
        {
            PublishNothing();
        }
    }

    // A working cloud session that has gone quiet. This was the *only* cloud
    // exemption before CB-182 — the reason the idle cases above are written idle
    // — and it is kept because a roster read on a timer cannot tell "still
    // working" from "nothing heard for a while", so this is the state where
    // hiding the orb would be worst if the broader exemption were ever narrowed.
    [AvaloniaFact]
    public void AWorkingCloudSessionSurvivesGoingQuiet()
    {
        using var scratch = new Scratch();
        var wasLifetime = ClaudeBuddySettings.OrbLifetimeMinutes;
        try
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = 1;
            Publish(Session("session_01busy", state: "generating",
                lastActivity: Now.AddMinutes(-10)));

            var manager = Manager(scratch.Dir);
            manager.ScanAndUpdate();

            Assert.Contains("cloud:session_01busy", Orbs(manager).Keys);
        }
        finally
        {
            ClaudeBuddySettings.OrbLifetimeMinutes = wasLifetime;
            PublishNothing();
        }
    }

    // --- what the orb looks like ---

    [AvaloniaFact]
    public void ACloudOrbWearsTheCloudBadge()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus());

        Assert.Equal("☁", orb.KindGlyphText);
        Assert.Equal("in the cloud", orb.KindLabel);
    }

    // No CLI mark, and that is the point rather than an omission. A Claude spark
    // reads as "local Claude Code" from across a room, which is the one thing
    // this session is not — the cloud badge is already saying where it lives.
    [AvaloniaFact]
    public void ACloudOrbWearsNoCliMark()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus());

        Assert.Null(orb.CliMarkName);
    }

    // The menu item is already disabled for anything that is not a local CLI.
    // What this asserts is the *wording*, because a disabled row still reads as
    // a promise: left on the default text it would say "Reset this session to
    // idle" about a session whose state this machine has no say in.
    [AvaloniaFact]
    public void TheResetItemRefusesAndSaysWhy()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus());

        Assert.False(orb.ResetIdleItem.IsEnabled);
        Assert.Contains("cloud", (string)orb.ResetIdleItem.Header!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reset this session to idle", (string)orb.ResetIdleItem.Header!);
    }

    // The path row hides itself when there is nothing to put in it, which for a
    // cloud session is always.
    [AvaloniaFact]
    public void ThePathRowIsHidden()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus());

        Assert.False(orb.SessionPathItem.IsVisible);
    }

    // --- the context ring ---

    [AvaloniaFact]
    public void AContextPercentDrawsARing()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(contextPercent: 40));

        Assert.True(orb.ContextRingLayer.IsVisible);
        Assert.NotNull(orb.ContextArc.Data);
    }

    // Nobody reporting a number is not a session at zero, and a track drawn
    // around an orb with no reading claims a measurement that was never taken.
    [AvaloniaFact]
    public void NoContextPercentDrawsNoRingAtAll()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(contextPercent: null));

        Assert.False(orb.ContextRingLayer.IsVisible);
        Assert.Null(orb.ContextArc.Data);
        Assert.Null(orb.ContextRingColour);
    }

    // The bands are UsageRingGeometry's, shared with the account orbs, so a ring
    // at 90% is the same red wherever it is drawn. Asserted through the colour
    // the window reports rather than by reading a brush back off a shape.
    [AvaloniaTheory]
    [InlineData(10, AccountOrbWindow.CalmHex)]
    [InlineData(70, AccountOrbWindow.WarnHex)]
    [InlineData(95, AccountOrbWindow.DangerHex)]
    public void TheRingUsesTheSharedColourBands(int percent, string expected)
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(contextPercent: percent));

        Assert.Equal(expected, orb.ContextRingColour);
    }

    // A full context window is an ellipse rather than an arc — an arc sweeping
    // 360 degrees has coincident endpoints and renders as nothing, so the one
    // session that has actually run out would be the one drawing no ring.
    [AvaloniaFact]
    public void AFullContextWindowStillDrawsSomething()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(contextPercent: 100));

        Assert.True(orb.ContextRingLayer.IsVisible);
        Assert.IsType<Avalonia.Media.EllipseGeometry>(orb.ContextArc.Data);
    }

    // A ring survives being handed to an orb that is not a cloud session, since
    // nothing about the drawing is source-specific — the only reason no other
    // orb draws one today is that nothing else reports the number.
    [AvaloniaFact]
    public void TheRingIsClearedWhenAReadingGoesAway()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(contextPercent: 40));
        orb.UpdateFrom(CloudStatus(contextPercent: null));

        Assert.False(orb.ContextRingLayer.IsVisible);
        Assert.Null(orb.ContextArc.Data);
    }

    // --- the hover line ---

    [AvaloniaTheory]
    [InlineData(null, null, null)]
    [InlineData("Editing files", null, "Editing files")]
    [InlineData(null, "Ran the tests", "Ran the tests")]
    [InlineData("Editing files", "Ran the tests", "Editing files · Ran the tests")]
    [InlineData("  ", "Ran the tests", "Ran the tests")]
    public void TheCloudHoverLineToleratesAbsence(string? detail, string? recent, string? expected)
    {
        Assert.Equal(expected, OrbWindow.CloudTipDetail(detail, recent));
    }

    // And it actually reaches the bubble, rather than being a pure function
    // nothing calls — the failure a helper like this has most often.
    [AvaloniaFact]
    public void TheHoverBubbleCarriesTheCloudDetail()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        orb.UpdateFrom(CloudStatus(statusDetail: "Editing files", recentAction: "Ran the tests"));

        var bubble = orb.CurrentThoughtBubble;
        Assert.NotNull(bubble);

        var lines = bubble!.GetLogicalDescendants()
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .ToList();

        Assert.Contains("Editing files · Ran the tests", lines);
    }

    // --- opening a conversation with one -----------------------------------------

    // RemoteChatFor's ClaudeCloud arm, driven through the manager rather than by
    // constructing a session directly.
    //
    // Constructing ClaudeCloudChatSession by hand — which is what the panel suite
    // next door does — tests the session and never enters this branch at all, so
    // the four outcomes below were unmeasured while the class they produce was
    // well covered. They are reached here through the real dictionary and the
    // real roster lookup, with the network and the Keychain swapped out at the
    // seam SessionManager exposes for it.

    private sealed class SilentApi : ICloudApi
    {
        public Task<CloudApiResult> GetAsync(CloudRequestContext context, CancellationToken token) =>
            Task.FromResult(new CloudApiResult(CloudOutcomes.OutcomeFor(401, ""), null));
    }

    private sealed class NoCredentials : ICloudCredentialSource
    {
        public string? Stamp() => null;

        public CredentialRead Read() =>
            new(CredentialOutcome.NotLoggedIn, null, null, "no credential in a test");
    }

    // The fake reports "not logged in", so the load StartCloudLoad kicks off ends
    // immediately and without a socket. What is under test is which session comes
    // back, not what it manages to read.
    private static SessionManager CloudManager(string statusDir)
    {
        var manager = Manager(statusDir);
        manager.UseCloudChatDependenciesForTests(new SilentApi(), new NoCredentials());
        return manager;
    }

    [AvaloniaFact]
    public void RemoteChatForACloudSessionBuildsOneFromTheRosterRow()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01abc"));

            var manager = CloudManager(scratch.Dir);
            manager.ScanAndUpdate();

            var chat = manager.RemoteChatFor("cloud:session_01abc");

            Assert.NotNull(chat);

            // Built from the row and not from the orb: the title is the roster's,
            // and the id is the bare cloud id rather than the namespaced orb key.
            Assert.Equal("Refactor the parser", chat!.DisplayName);
            Assert.Equal("session_01abc", chat.SessionId);
        }
        finally
        {
            PublishNothing();
        }
    }

    // Cached, for the reason the branch's own comment gives: there is no file on
    // this machine to rebuild the transcript from, so a second construction would
    // be a second network round trip and an emptier panel.
    [AvaloniaFact]
    public void RemoteChatForACloudSessionIsCachedOnceCreated()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01abc"));

            var manager = CloudManager(scratch.Dir);
            manager.ScanAndUpdate();

            var first = manager.RemoteChatFor("cloud:session_01abc");
            Assert.NotNull(first);

            Assert.Same(first, manager.RemoteChatFor("cloud:session_01abc"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // A row that has left the roster while the orb is still on screen. The scan
    // publishes it once so the status exists, then publishes an empty roster
    // underneath it — which is exactly the shape of a session being archived
    // between a scan and a click.
    //
    // Null rather than an empty session: there is nothing to read, and a panel
    // that opened on nothing would look identical to one whose read had failed.
    [AvaloniaFact]
    public void RemoteChatForACloudSessionWhoseRowHasGoneIsNull()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01abc"));

            var manager = CloudManager(scratch.Dir);
            manager.ScanAndUpdate();

            ClaudeCloudSessions.SetSnapshotForTests(Array.Empty<ClaudeCloudSessions.Session>());

            Assert.Null(manager.RemoteChatFor("cloud:session_01abc"));
        }
        finally
        {
            PublishNothing();
        }
    }

    // ...but a session already built survives its row leaving, which is the
    // negative control for the case above. The cache is checked before the row is,
    // deliberately: a conversation someone has open must not empty itself because
    // the roster moved on.
    [AvaloniaFact]
    public void ACachedCloudSessionOutlivesItsRosterRow()
    {
        using var scratch = new Scratch();
        try
        {
            Publish(Session("session_01abc"));

            var manager = CloudManager(scratch.Dir);
            manager.ScanAndUpdate();

            var first = manager.RemoteChatFor("cloud:session_01abc");
            Assert.NotNull(first);

            ClaudeCloudSessions.SetSnapshotForTests(Array.Empty<ClaudeCloudSessions.Session>());

            Assert.Same(first, manager.RemoteChatFor("cloud:session_01abc"));
        }
        finally
        {
            PublishNothing();
        }
    }
}
