using Xunit;

namespace Orbweaver.Tests;

// CB-266: when is a cloud roster row the same conversation as a local orb? Only
// when a local Claude Desktop chat is bridged to exactly that session. Each of
// the negative cases below would also be "true" under a looser rule (any
// Desktop chat, any bridged chat, a prefix match), so they are the controls.
// Invented ids.
public class CloudShadowedByLocalTests
{
    private const string Bridge = "session_01ShadowFixtureFFFF";

    private static SessionStatus Chat(
        string bridge, string entrypoint = SessionStatus.DesktopEntrypoint,
        SessionSource source = SessionSource.ClaudeCode) =>
        new() { Entrypoint = entrypoint, Source = source, BridgeSessionId = bridge };

    [Fact]
    public void ABridgedDesktopChatShadowsItsOwnCloudRow() =>
        Assert.True(SessionManager.CloudShadowedByLocal(Bridge, new[] { Chat(Bridge) }));

    [Fact]
    public void AnyOneOfSeveralLocalChatsCanShadow() =>
        Assert.True(SessionManager.CloudShadowedByLocal(
            Bridge, new[] { Chat("session_01Other"), Chat(""), Chat(Bridge) }));

    [Fact]
    public void ADifferentBridgeAnUnbridgedChatOrNoLocalsShadowNothing()
    {
        Assert.False(SessionManager.CloudShadowedByLocal(Bridge, new[] { Chat("session_01Other") }));
        Assert.False(SessionManager.CloudShadowedByLocal(Bridge, new[] { Chat("") }));
        Assert.False(SessionManager.CloudShadowedByLocal(Bridge, Array.Empty<SessionStatus>()));
    }

    [Fact]
    public void ATerminalSessionOrAnotherCliWithTheSameIdDoesNotShadow()
    {
        Assert.False(SessionManager.CloudShadowedByLocal(Bridge, new[] { Chat(Bridge, entrypoint: "cli") }));
        Assert.False(SessionManager.CloudShadowedByLocal(
            Bridge, new[] { Chat(Bridge, source: SessionSource.Codex) }));
    }

    [Fact]
    public void TheComparisonIsCaseSensitiveBecauseSessionIdsAre() =>
        Assert.False(SessionManager.CloudShadowedByLocal(Bridge, new[] { Chat(Bridge.ToUpperInvariant()) }));
}
