using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-170: what the event stream does to an orb after its conversation is
// interrupted or ended — the two things the new menu rows cause, as the
// gateway reports them to every subscriber.
//
// The sequences below are the ones recorded live against OpenClaw 2026.9.2 on
// a throwaway session, trimmed to the fields the classifier reads and replayed
// in the order they arrived. Driven through OnEvent and read back through
// Parse, the real pair, the same way OpenClawEventStateTests does.
[Collection("Settings")]
public class OpenClawAbortAndArchiveEventTests : IDisposable
{
    private const string Run = "ae906e0d-36b1-4c32-8e7b-ba7d43a83a43";

    public void Dispose() =>
        OpenClawSessions.SetSnapshotForTests(Array.Empty<OpenClawSessions.Session>());

    private static string Key() => $"agent:a{Guid.NewGuid():N}:dashboard:1";

    private static void Fire(string name, string json) =>
        OpenClawSessions.OnEvent(name, JsonDocument.Parse(json).RootElement);

    // The session as the poll sees it, not archived, reporting nothing about
    // running: "generating" can only come from what the events recorded.
    private static string StateOf(string key)
    {
        ClaudeBuddySettings.OpenClawEnabled = true;
        ClaudeBuddySettings.OpenClawHeartbeatMode = ClusterMode.WithChats;
        ClaudeBuddySettings.OpenClawActiveWithinMinutes = ClaudeBuddySettings.OpenClawActiveWithinAll;

        var at = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-1)).ToUnixTimeMilliseconds();
        var json = "{\"sessions\":[{\"key\":" + JsonSerializer.Serialize(key) + ",\"lastActivityAt\":" + at + "}]}";

        return OpenClawSessions.Parse(JsonDocument.Parse(json).RootElement, DateTime.UtcNow)
            .Sessions.Single(s => s.Key == key).State;
    }

    // --- an interrupted run ---

    // The whole measured sequence: the run opens, chat.abort lands, and the
    // gateway reports the end three ways — then, a second later, a trailing
    // phase "error" for the same run. The orb goes dark at the first terminal
    // event and stays dark through the trailing one.
    [Fact]
    public void AnAbortedRunEndsAndTheTrailingErrorDoesNotRelightIt()
    {
        var key = Key();
        var k = JsonSerializer.Serialize(key);

        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"phase":"start","runId":"{{Run}}","status":"running","hasActiveRun":true}""");
        Fire("agent", $$$"""{"sessionKey":{{{k}}},"runId":"{{{Run}}}","stream":"lifecycle","data":{"phase":"start"}}""");
        Fire("agent", $$$"""{"sessionKey":{{{k}}},"runId":"{{{Run}}}","stream":"run_status","data":{"phase":"starting_model"}}""");
        Assert.Equal("generating", StateOf(key));

        Fire("chat", $$"""{"sessionKey":{{k}},"runId":"{{Run}}","state":"aborted"}""");
        Assert.NotEqual("generating", StateOf(key));

        Fire("agent", $$$"""{"sessionKey":{{{k}}},"runId":"{{{Run}}}","stream":"lifecycle","data":{"phase":"end"}}""");
        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"phase":"end","runId":"{{Run}}","status":"killed","abortedLastRun":true,"hasActiveRun":false}""");
        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"reason":"chat.run.settled","status":"killed"}""");
        Assert.NotEqual("generating", StateOf(key));

        // The trailing pair, about 1.1 s later on the wire.
        Fire("agent", $$$"""{"sessionKey":{{{k}}},"runId":"{{{Run}}}","stream":"lifecycle","data":{"phase":"error"}}""");
        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"phase":"error","runId":"{{Run}}","status":"killed","hasActiveRun":false}""");
        Assert.NotEqual("generating", StateOf(key));
    }

    // If the "end" row is the one that went missing, the trailing "error" on
    // its own still ends the run — the reason it counts as terminal at all.
    [Fact]
    public void ARosterErrorAloneEndsTheRun()
    {
        var key = Key();
        var k = JsonSerializer.Serialize(key);

        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"phase":"start","runId":"{{Run}}"}""");
        Assert.Equal("generating", StateOf(key));

        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"phase":"error","runId":"{{Run}}","status":"killed"}""");
        Assert.NotEqual("generating", StateOf(key));
    }

    // --- an archived conversation, arriving by event ---

    private static OpenClawSessions.Session Session(string key) =>
        new(key, "t", "", "idle", DateTime.UtcNow, null, SessionKind.Direct, false, "sid-1");

    // The row an archive sends every subscriber (measured: reason "patch", the
    // whole row, archived:true). The orb leaves at once rather than waiting for
    // the next poll to stop listing it — whichever client archived it.
    [Fact]
    public void AnArchivedRowOnTheEventStreamTakesTheOrbOffNow()
    {
        ClaudeBuddySettings.OpenClawEnabled = true;
        var key = Key();
        OpenClawSessions.SetSnapshotForTests(new[] { Session(key) });

        Fire("sessions.changed", $$"""{"sessionKey":{{JsonSerializer.Serialize(key)}},"reason":"patch","archived":true,"archivedAt":1790399423646,"archiveReason":"manual"}""");

        Assert.DoesNotContain(OpenClawSessions.Snapshot(), s => s.Key == key);
    }

    // ...and takes its running record with it, so nothing recorded before the
    // archive can hold the orb up if the key is ever listed again.
    [Fact]
    public void AnArchivedRowClearsWhatWasRecordedAsRunning()
    {
        var key = Key();
        var k = JsonSerializer.Serialize(key);

        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"phase":"start","runId":"{{Run}}"}""");
        Assert.Equal("generating", StateOf(key));

        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"reason":"patch","archived":true}""");

        Assert.Equal("idle", StateOf(key));
    }

    // The control: the same event un-archiving (archived:false, measured) or
    // saying nothing about it leaves the orb and its run alone.
    [Theory]
    [InlineData(""","archived":false""")]
    [InlineData("")]
    public void ARowThatIsNotArchivedChangesNothing(string archived)
    {
        ClaudeBuddySettings.OpenClawEnabled = true;
        var key = Key();
        var k = JsonSerializer.Serialize(key);
        OpenClawSessions.SetSnapshotForTests(new[] { Session(key) });
        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"phase":"start","runId":"{{Run}}"}""");

        Fire("sessions.changed", $$"""{"sessionKey":{{k}},"reason":"patch"{{archived}}}""");

        Assert.Contains(OpenClawSessions.Snapshot(), s => s.Key == key);
        Assert.Equal("generating", StateOf(key));
    }
}
