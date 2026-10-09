using Xunit;

namespace Orbweaver.Tests;

// CB-217: `claude agents --json` is run once per account every ten seconds,
// about 19 runs a minute on the MacBook this was measured on. The listing is only
// ever consulted by session id, and a session belongs to one account, so only the
// accounts that own a session the scan can see are asked. These pin that
// attribution, and above all that it falls back to asking everything whenever
// it cannot attribute a session.
public class BackgroundJobsAccountFilterTests
{
    private const string Default = "/Users/w/.claude";
    private const string Board = "/Users/w/.claude-board";
    private const string Work = "/Users/w/.claude-work";
    private static readonly string[] All = { Default, Board, Work };

    private static string Transcript(string account, string id = "abc") =>
        $"{account}/projects/-Users-w-Source-app/{id}.jsonl";

    [Fact]
    public void OnlyTheAccountsThatOwnASessionAreAsked()
    {
        var ask = BackgroundJobs.AccountsToAsk(All, new[] { Transcript(Board), Transcript(Board, "def") });

        Assert.Equal(new[] { Board }, ask);
    }

    [Fact]
    public void SeveralOwningAccountsAreAskedInTheirOriginalOrder()
    {
        var ask = BackgroundJobs.AccountsToAsk(All, new[] { Transcript(Work), Transcript(Default) });

        Assert.Equal(new[] { Default, Work }, ask);
    }

    [Fact]
    public void BeforeTheFirstScanEveryAccountIsAsked() =>
        Assert.Equal(All, BackgroundJobs.AccountsToAsk(All, null));

    [Theory]
    [InlineData(null)]                              // a session with no transcript recorded
    [InlineData("")]
    [InlineData("/somewhere/else/abc.jsonl")]       // under no known account
    public void OneSessionThatCannotBeAttributedMeansAskingEveryAccount(string? stray)
    {
        var ask = BackgroundJobs.AccountsToAsk(All, new[] { Transcript(Board), stray });

        Assert.Equal(All, ask);
    }

    [Fact]
    public void NoSessionsAtAllMeansNoAccountIsAsked() =>
        Assert.Empty(BackgroundJobs.AccountsToAsk(All, Array.Empty<string?>()));

    [Fact]
    public void AnAccountWhoseNameIsAPrefixOfAnotherDoesNotClaimItsSessions()
    {
        // ".claude" is a string prefix of ".claude-board"; only a real path
        // boundary counts.
        var ask = BackgroundJobs.AccountsToAsk(All, new[] { Transcript(Board) });

        Assert.DoesNotContain(Default, ask);
    }

    [Theory]
    [InlineData("/Users/w/.claude/projects/x.jsonl", "/Users/w/.claude", true)]
    [InlineData("/Users/w/.claude/projects/x.jsonl", "/Users/w/.claude/", true)]    // trailing slash on the dir
    [InlineData("/USERS/W/.CLAUDE/projects/x.jsonl", "/Users/w/.claude", true)]    // case-insensitive, as on macOS
    [InlineData("/Users/w/.claude-board/projects/x.jsonl", "/Users/w/.claude", false)]
    [InlineData("/Users/w/.claude", "/Users/w/.claude", false)]                     // the dir itself is not under it
    public void IsUnderNeedsAPathBoundary(string path, string dir, bool under) =>
        Assert.Equal(under, BackgroundJobs.IsUnder(path, dir));

    [Fact]
    public void TheScanHandsOverOnlyClaudeCodeTranscripts()
    {
        var found = new List<SessionManager.ScanEntry>
        {
            new("a", new SessionStatus { Source = SessionSource.ClaudeCode, TranscriptPath = Transcript(Board) }, DateTime.UtcNow),
            new("b", new SessionStatus { Source = SessionSource.Codex, TranscriptPath = "/Users/w/.codex/sessions/r.jsonl" }, DateTime.UtcNow),
            new("c", new SessionStatus { Source = SessionSource.ClaudeCode, TranscriptPath = "" }, DateTime.UtcNow),
        };

        Assert.Equal(new[] { Transcript(Board), "" }, SessionManager.ClaudeTranscripts(found));
    }
}
