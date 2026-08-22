using Xunit;

namespace ClaudeBuddy.Tests;

// Covers AgentTeam.cs ~110-145. Both values come from a process this app
// doesn't own (a team member's own command line), read via ps/tasklist, so
// both get sanitized before they're compared or shown — but differently:
// Sanitize is for values that get matched (a session id, a colour name) and
// rejects the whole thing on any surprise; SanitizeName is for a value that's
// only ever shown, so it keeps what it can instead.
public class AgentTeamSanitizeTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("my-session-42", "my-session-42")]
    public void Sanitize_PassesThroughOrEmpties(string? input, string expected)
    {
        Assert.Equal(expected, AgentTeam.Sanitize(input));
    }

    [Fact]
    public void Sanitize_RejectsValueLongerThan64Chars()
    {
        var tooLong = new string('a', 65);
        Assert.Equal("", AgentTeam.Sanitize(tooLong));

        var exactly64 = new string('a', 64);
        Assert.Equal(exactly64, AgentTeam.Sanitize(exactly64));
    }

    [Theory]
    [InlineData("my session")]      // embedded space
    [InlineData("$(rm -rf ~)")]     // shell substitution
    [InlineData("`whoami`")]        // backtick
    [InlineData("it's-a-trap")]     // quote
    [InlineData("line1\nline2")]    // newline
    [InlineData("café")]            // non-ASCII
    public void Sanitize_RejectsTheWholeValueOnAnySurprisingCharacter(string input)
    {
        // "A session id or a colour name and nothing else... but they come
        // from a process this app doesn't own" — AgentTeam.cs. One bad
        // character anywhere throws away the whole value; it does not strip
        // just the offending characters.
        Assert.Equal("", AgentTeam.Sanitize(input));
    }

    [Theory]
    [InlineData("a<b>c", "abc")]
    public void SanitizeName_DropsDisallowedCharactersButKeepsTheRest(string input, string expected)
    {
        // Unlike Sanitize, this "keeps what it can rather than rejecting the
        // whole value" — AgentTeam.cs.
        Assert.Equal(expected, AgentTeam.SanitizeName(input));
    }

    [Fact]
    public void SanitizeName_AllJunkInputTrimsToEmpty()
    {
        Assert.Equal("", AgentTeam.SanitizeName("<<<>>>@@@###"));
    }

    [Fact]
    public void SanitizeName_TruncatesTo48CharsAndTrimsTrailingWhitespaceOffTheCut()
    {
        // 47 letters + a space landing exactly on the 48th character + more
        // letters after it. Truncating at 48 chars leaves that space as the
        // last character, which TrimEnd then removes.
        var input = new string('a', 47) + " " + new string('a', 10);

        var result = AgentTeam.SanitizeName(input);

        Assert.Equal(new string('a', 47), result);
    }

    [Fact]
    public void SanitizeName_NullOrEmptyIsEmpty()
    {
        Assert.Equal("", AgentTeam.SanitizeName(null));
        Assert.Equal("", AgentTeam.SanitizeName(""));
    }
}
