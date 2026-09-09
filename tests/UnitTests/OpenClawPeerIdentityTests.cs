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
    // Rate accepted only as a bounded number: out of range, wrong JSON kind,
    // or a value dressed up as a number string are all refused wholesale
    // rather than silently dropping just the rate.
    [InlineData("{\"gatewayPin\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"voices\":[{\"agentId\":\"main\",\"voice\":\"af_bella\",\"rate\":9.9}]}")]
    [InlineData("{\"gatewayPin\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"voices\":[{\"agentId\":\"main\",\"voice\":\"af_bella\",\"rate\":\"1.3\"}]}")]
    public void AResponseThatCouldCarryMoreThanAnAgentVoicePairIsRefused(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Null(OpenClawPeerIdentity.ResponseFrom(document.RootElement));
    }

    [Fact]
    public void ABoundedRateRoundTripsAlongsideItsVoice()
    {
        var response = new OpenClawPeerIdentity.Response(Pin,
            new[] { new OpenClawPeerIdentity.Row("main", "af_nicole", 1.3) });

        var parsed = Assert.IsType<OpenClawPeerIdentity.Response>(
            OpenClawPeerIdentity.ResponseFrom(PeerProtocol.BodyOf(response)));

        Assert.Equal(1.3, Assert.Single(parsed.Voices).Rate);
    }

    [Fact]
    public void AResponseWithNoRateAtAllStillParsesWithRateAbsent()
    {
        var json = "{\"gatewayPin\":\"" + Pin + "\",\"voices\":[{\"agentId\":\"main\",\"voice\":\"af_bella\"}]}";
        using var document = System.Text.Json.JsonDocument.Parse(json);

        var parsed = OpenClawPeerIdentity.ResponseFrom(document.RootElement);

        Assert.NotNull(parsed);
        Assert.Null(Assert.Single(parsed!.Voices).Rate);
    }

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(2.0, true)]
    [InlineData(0.49, false)]
    [InlineData(2.01, false)]
    public void ValidRateAcceptsOnlyTheDocumentedBounds(double rate, bool valid) =>
        Assert.Equal(valid, OpenClawPeerIdentity.ValidRate(rate));

    [Fact]
    public void ValidRateAcceptsAbsentAsValid() => Assert.True(OpenClawPeerIdentity.ValidRate(null));

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

    [Fact]
    public void ResolvingAKnownAgentWithARateIncludesItInTheRow()
    {
        var savedPin = ClaudeBuddySettings.OpenClawFingerprint;
        try
        {
            ClaudeBuddySettings.OpenClawFingerprint = Pin;
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                ["main"] = new("Main", null, null, "af_nicole", 1.3),
                ["other"] = new("Other", null, null, "af_bella"),
            });

            var rows = OpenClawSessions.PeerProfileVoices(Pin, new[] { "main", "other" });

            var main = Assert.Single(rows, r => r.AgentId == "main");
            Assert.Equal("af_nicole", main.Voice);
            Assert.Equal(1.3, main.Rate);
            var other = Assert.Single(rows, r => r.AgentId == "other");
            Assert.Null(other.Rate);
        }
        finally
        {
            ClaudeBuddySettings.OpenClawFingerprint = savedPin;
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }

    [Fact]
    public void APeerRateIsBoundToTheCurrentGatewayPinAndRemovedOnDisconnect()
    {
        var savedPin = ClaudeBuddySettings.OpenClawFingerprint;
        const string agent = "main";
        try
        {
            ClaudeBuddySettings.OpenClawFingerprint = Pin;
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                [agent] = new("Main", null, null),
            });

            OpenClawSessions.ApplyPeerProfileVoices("mini", Pin,
                new[] { new OpenClawPeerIdentity.Row(agent, "af_bella", 1.3) });
            Assert.Equal(1.3, OpenClawSessions.RateForSession("openclaw:agent:main:discord:channel:1"));

            ClaudeBuddySettings.OpenClawFingerprint = new string('b', 64);
            Assert.Null(OpenClawSessions.RateForSession("openclaw:agent:main:discord:channel:1"));

            ClaudeBuddySettings.OpenClawFingerprint = Pin;
            OpenClawSessions.ForgetPeerProfileVoices("mini");
            Assert.Null(OpenClawSessions.RateForSession("openclaw:agent:main:discord:channel:1"));
        }
        finally
        {
            ClaudeBuddySettings.OpenClawFingerprint = savedPin;
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }

    [Fact]
    public void APeerVoiceWithNoRateResolvesTheVoiceWithoutARate()
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
            Assert.Null(OpenClawSessions.RateForSession("openclaw:agent:main:discord:channel:1"));
        }
        finally
        {
            ClaudeBuddySettings.OpenClawFingerprint = savedPin;
            OpenClawSessions.SetIdentitiesForTests(new Dictionary<string, OpenClawSessions.AgentIdentity>());
        }
    }
}
