using System.Text;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-216: the rules that let an unchanged roster go unsent. A peer asks every
// ten seconds and each answer embedded every persona picture, about 17 MB per
// ask on the machine it was measured on. The round trip itself is covered in
// MirrorRoundTripTests; these are the decisions it rests on.
public class RosterHashTests
{
    private static readonly MirrorProtocol.MirrorRosterEntry[] Roster =
    {
        new("far-session", MirrorProtocol.CliClaudeCode, true, true,
            Persona: new MirrorProtocol.PeerPersona("Ava", null, null, new byte[] { 71, 73, 70, 56 })),
    };

    [Fact]
    public void TheWireRosterIsTheGzipOfTheBytesTheHashIsTakenOver()
    {
        var raw = MirrorProtocol.RosterBytes(Roster);

        Assert.Equal(raw, MirrorProtocol.Gunzip(MirrorProtocol.EncodeRoster(Roster)));
    }

    [Fact]
    public void TheSameRosterAlwaysHashesTheSame()
    {
        // The whole scheme depends on it: an answer is recognised as unchanged
        // only if building the same roster twice produces the same bytes.
        Assert.Equal(
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(Roster)),
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(Roster.ToArray())));
    }

    [Fact]
    public void AChangedPictureChangesTheHash()
    {
        var repainted = new[]
        {
            Roster[0] with { Persona = Roster[0].Persona! with { Avatar = new byte[] { 71, 73, 70, 57 } } },
        };

        Assert.NotEqual(
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(Roster)),
            MirrorProtocol.Hash(MirrorProtocol.RosterBytes(repainted)));
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("ABC", "abc", false)]   // hex from one function; no case games
    [InlineData(null, "abc", false)]    // every first ask, and every older Buddy's
    [InlineData("", "abc", false)]
    public void OnlyAHashTheAskerSentAndThatMatchesIsUnchanged(string? asker, string current, bool unchanged) =>
        Assert.Equal(unchanged, MirrorProtocol.RosterUnchanged(asker, current));

    // --- the gzip memo ------------------------------------------------------------

    [Fact]
    public void AnIdenticalRosterIsCompressedOnce()
    {
        var compressions = 0;
        var memo = new RosterGzipMemo(raw => { compressions++; return MirrorProtocol.Gzip(raw); });
        var raw = Encoding.UTF8.GetBytes("roster");

        var first = memo.For("h1", raw);
        var second = memo.For("h1", raw);

        Assert.Equal(1, compressions);
        Assert.Same(first, second);
    }

    [Fact]
    public void ADifferentRosterIsCompressedAndReplacesTheOldOne()
    {
        var compressions = 0;
        var memo = new RosterGzipMemo(raw => { compressions++; return MirrorProtocol.Gzip(raw); });

        memo.For("h1", Encoding.UTF8.GetBytes("one"));
        var two = memo.For("h2", Encoding.UTF8.GetBytes("two"));

        Assert.Equal(2, compressions);
        Assert.Equal("two", Encoding.UTF8.GetString(MirrorProtocol.Gunzip(two)));

        // One slot: going back to the first roster compresses it again.
        memo.For("h1", Encoding.UTF8.GetBytes("one"));
        Assert.Equal(3, compressions);
    }

    [Fact]
    public async Task PeersAskingAtOnceAllGetTheSameBytes()
    {
        var memo = new RosterGzipMemo(MirrorProtocol.Gzip);
        var raw = Encoding.UTF8.GetBytes(new string('x', 100_000));

        var answers = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => memo.For("same", raw))));

        Assert.All(answers, answer => Assert.Equal(raw, MirrorProtocol.Gunzip(answer)));
    }
}
