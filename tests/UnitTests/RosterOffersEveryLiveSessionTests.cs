using Xunit;

namespace ClaudeBuddy.UnitTests;

// What a machine tells another machine it has.
//
// **All three rules here were found by installing the app on two real machines
// and watching one of them calmly report that it had no sessions while running
// two.** None could have been found by reading, and none produced an error:
// every one of them is a legitimate-looking answer that happens to be false.
public class RosterOffersEveryLiveSessionTests
{
    private static AgentRoster.Entry Agent(string name, string sessionId, int pid = 0) =>
        new(name, sessionId, pid);

    private static (string SessionId, SessionStatus Status) Session(
        string sessionId, int pid = 0) =>
        (sessionId, new SessionStatus { SessionPid = pid, Cwd = "/somewhere" });

    // --- a name only one session answers to -----------------------------------

    [Fact]
    public void AnUnsharedNameIsOfferedUnchanged()
    {
        // The ordinary case, and the one that must stay quiet: an orb's identity
        // is stable for as long as its name is unambiguous.
        var offered = RemoteMirrorServer.Offer(
            new[] { "jh" }, new[] { Agent("jh", "s1") }, new[] { Session("s1") });

        var only = Assert.Single(offered);

        Assert.Equal("jh", only.Name);
        Assert.Equal("s1", only.Resolved?.SessionId);
    }

    [Fact]
    public void ANameNothingAnswersToIsReportedRatherThanDropped()
    {
        // The caller logs it. Silently omitting it would make "the far machine
        // does not have this" and "the far machine did not mention it"
        // indistinguishable.
        var only = Assert.Single(RemoteMirrorServer.Offer(
            new[] { "ghost" }, Array.Empty<AgentRoster.Entry>(),
            Array.Empty<(string, SessionStatus)>()));

        Assert.Equal("ghost", only.Name);
        Assert.Null(only.Resolved);
    }

    // --- a name two live sessions answer to -----------------------------------

    [Fact]
    public void TwoSessionsSharingANameAreBothOffered()
    {
        // **The case that shipped broken.** One machine with the same session
        // name under two Claude accounts is ordinary — one person, two logins.
        // Refusing both is right for typing, where the wrong terminal is worse
        // than none, and wrong for a roster: the far user got no live view of
        // either and nothing said why.
        var offered = RemoteMirrorServer.Offer(
            new[] { "job-hunter" },
            new[] { Agent("job-hunter", "aaaaaa11"), Agent("job-hunter", "bbbbbb22") },
            new[] { Session("aaaaaa11"), Session("bbbbbb22") });

        Assert.Equal(2, offered.Count);
        Assert.All(offered, o => Assert.NotNull(o.Resolved));
    }

    [Fact]
    public void EachSharedNameCarriesEnoughOfItsSessionIdToTellThemApart()
    {
        var offered = RemoteMirrorServer.Offer(
            new[] { "job-hunter" },
            new[] { Agent("job-hunter", "aaaaaa11"), Agent("job-hunter", "bbbbbb22") },
            new[] { Session("aaaaaa11"), Session("bbbbbb22") });

        Assert.Equal(
            new[] { "job-hunter#aaaaaa", "job-hunter#bbbbbb" },
            offered.Select(o => o.Name).OrderBy(n => n));
    }

    [Fact]
    public void AQualifiedNameResolvesBackToItsOwnSession()
    {
        // The missing half of the same change: a name the roster published has
        // to be a name a fetch can resolve, or every disambiguated session
        // reads as one that vanished between the roster and the click.
        var picked = RemoteMirrorServer.Pick(
            "job-hunter#bbbbbb",
            new[] { Agent("job-hunter", "aaaaaa11"), Agent("job-hunter", "bbbbbb22") },
            new[] { Session("aaaaaa11"), Session("bbbbbb22") });

        Assert.Equal("bbbbbb22", picked?.SessionId);
    }

    [Fact]
    public void AnUnsharedNameStillResolvesWithoutAQualifier()
    {
        var picked = RemoteMirrorServer.Pick(
            "jh", new[] { Agent("jh", "s1") }, new[] { Session("s1") });

        Assert.Equal("s1", picked?.SessionId);
    }

    [Fact]
    public void AQualifierNamingNoSessionResolvesToNothing()
    {
        // A stale orb clicked after its session ended. Better to resolve to
        // nothing than to the *other* session with the same name, which is the
        // wrong-terminal failure the ambiguity rule exists to prevent.
        Assert.Null(RemoteMirrorServer.Pick(
            "job-hunter#cccccc",
            new[] { Agent("job-hunter", "aaaaaa11"), Agent("job-hunter", "bbbbbb22") },
            new[] { Session("aaaaaa11"), Session("bbbbbb22") }));
    }

    // --- splitting a published name back apart --------------------------------

    [Fact]
    public void APlainNameHasNoQualifier()
    {
        Assert.Equal("job-hunter", RemoteMirrorServer.Unqualified("job-hunter", out var id));
        Assert.Null(id);
    }

    [Fact]
    public void AQualifiedNameSplitsIntoItsTwoHalves()
    {
        Assert.Equal("job-hunter", RemoteMirrorServer.Unqualified("job-hunter#aaaaaa", out var id));
        Assert.Equal("aaaaaa", id);
    }

    [Theory]
    [InlineData("#aaaaaa")]
    [InlineData("job-hunter#")]
    public void SomethingThatOnlyLooksQualifiedIsLeftAlone(string name)
    {
        // A leading or trailing mark is not a qualifier, and treating it as one
        // would turn a session genuinely named that into one nothing resolves.
        Assert.Equal(name, RemoteMirrorServer.Unqualified(name, out var id));
        Assert.Null(id);
    }

    [Fact]
    public void AShortSessionIdIsNotQualified()
    {
        // Nothing real produces one, and slicing six characters off a shorter
        // string would throw rather than degrade.
        Assert.Equal("jh", RemoteMirrorServer.Qualified("jh", "abc"));
    }
}
