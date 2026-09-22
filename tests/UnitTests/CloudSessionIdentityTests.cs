using Xunit;

namespace ClaudeBuddy.UnitTests;

// SessionIdentity.IsCloud — the discriminator for a session that lives in
// Anthropic's cloud rather than on this machine.
//
// It is here rather than beside its two siblings in the UI suite because it
// needs nothing: no panel, no orb, no persona registry. The siblings are tested
// there because the cases that matter for them go on to ask a registry for a
// name, and the only meaningful assertion about this one is the prefix itself.
//
// CB-164 added it for the panel to tell a cloud id apart from a local one, and
// the panel ended up not needing to ask — an unrecognised id already borrows the
// orb's letters, which is the answer this would have given. It was kept rather
// than deleted because the prefix it encodes is real and is minted by the scan,
// and a case here is what stops the next person reading "cloud:" out of
// SessionManager and writing their own.
//
// It has a production caller as of CB-165: SessionIdentity.VoiceSourceFor asks
// it, because a cloud session has no persona registry on this disk and so keeps
// the user's own voice rather than falling through to the local one. That arm
// is covered in SpeechVoiceRoutingTests; this file still owns the prefix rule
// itself.
public class CloudSessionIdentityTests
{
    [Theory]
    [InlineData("cloud:session_01abc", true)]
    [InlineData("cloud:", true)]
    [InlineData("openclaw:agent:main", false)]
    [InlineData("rc:.claude:mac-mini", false)]
    [InlineData("", false)]
    // The prefix is matched at the front and ordinally — an id that merely
    // contains it is not one, which is the mistake a Contains would make.
    [InlineData("session_01abc:cloud:", false)]
    [InlineData("CLOUD:session_01abc", false)]
    public void ACloudIdIsTheOneCarryingThePrefix(string sessionId, bool expected) =>
        Assert.Equal(expected, SessionIdentity.IsCloud(sessionId));

    // Null is not an error. Every caller of this family reaches it from a
    // `_session?.SessionId`, and a panel between binds has no session at all.
    [Fact]
    public void NoSessionIsNotACloudSession() =>
        Assert.False(SessionIdentity.IsCloud(null));
}
