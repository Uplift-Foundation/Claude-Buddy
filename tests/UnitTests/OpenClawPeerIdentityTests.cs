using Xunit;

namespace ClaudeBuddy.UnitTests;

[Collection("Settings")]
public class OpenClawPeerIdentityTests
{
    private static readonly string Pin = new('a', 64);

    [Fact]
    public void ABoundedRequestAndResponseRoundTripThroughThePeerMessageBody()
    {
        var request = new OpenClawPeerIdentity.Request(Pin, new[] { "main", "planner_2" });
        var response = new OpenClawPeerIdentity.Response(Pin,
            new[] { new OpenClawPeerIdentity.Row("main", "af_bella") });

        var parsedRequest = Assert.IsType<OpenClawPeerIdentity.Request>(
            OpenClawPeerIdentity.RequestFrom(PeerProtocol.BodyOf(request)));
        var parsedResponse = Assert.IsType<OpenClawPeerIdentity.Response>(
            OpenClawPeerIdentity.ResponseFrom(PeerProtocol.BodyOf(response)));
        Assert.Equal(request.GatewayPin, parsedRequest.GatewayPin);
        Assert.Equal(request.AgentIds, parsedRequest.AgentIds);
        Assert.Equal(response.GatewayPin, parsedResponse.GatewayPin);
        Assert.Equal(response.Voices, parsedResponse.Voices);
    }

    [Theory]
    [InlineData("{\"gatewayPin\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"agentIds\":[\"main\"],\"workspace\":\"/private\"}")]
    [InlineData("{\"gatewayPin\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"agentIds\":[\"main\",\"MAIN\"]}")]
    [InlineData("{\"gatewayPin\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"agentIds\":[\"../main\"]}")]
    public void ARequestWithAnythingBeyondTheBoundedSchemaIsRefused(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Null(OpenClawPeerIdentity.RequestFrom(document.RootElement));
    }

    [Theory]
    [InlineData("{\"gatewayPin\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"voices\":[{\"agentId\":\"main\",\"voice\":\"af_bella\",\"path\":\"identity.md\"}]}")]
    [InlineData("{\"gatewayPin\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"voices\":[{\"agentId\":\"main\",\"voice\":\"line\\nbreak\"}]}")]
    public void AResponseThatCouldCarryMoreThanAnAgentVoicePairIsRefused(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Null(OpenClawPeerIdentity.ResponseFrom(document.RootElement));
    }

    [Fact]
    public void APeerVoiceIsBoundToTheCurrentGatewayPinAndRemovedOnDisconnect()
    {
        var savedPin = ClaudeBuddySettings.OpenClawFingerprint;
        const string agent = "main";
        var option = new TextToSpeech.VoiceOption(TextToSpeech.SpeakEngine.Neural, "af_bella", "Bella");
        try
        {
            ClaudeBuddySettings.OpenClawFingerprint = Pin;
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                [agent] = new("Main", null, null),
            });

            OpenClawSessions.ApplyPeerProfileVoices("mini", Pin,
                new[] { new OpenClawPeerIdentity.Row(agent, "af_bella") });
            Assert.Equal(option, OpenClawSessions.VoiceForSession(
                "openclaw:agent:main:discord:channel:1", new[] { option }));

            ClaudeBuddySettings.OpenClawFingerprint = new string('b', 64);
            Assert.Null(OpenClawSessions.VoiceForSession(
                "openclaw:agent:main:discord:channel:1", new[] { option }));

            ClaudeBuddySettings.OpenClawFingerprint = Pin;
            OpenClawSessions.ForgetPeerProfileVoices("mini");
            Assert.Null(OpenClawSessions.VoiceForSession(
                "openclaw:agent:main:discord:channel:1", new[] { option }));
        }
        finally
        {
            ClaudeBuddySettings.OpenClawFingerprint = savedPin;
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }
}
