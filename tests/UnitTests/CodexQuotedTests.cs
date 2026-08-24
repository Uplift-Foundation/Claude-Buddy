using Xunit;

namespace ClaudeBuddy.UnitTests;

// CodexTranscript.Quoted: pulling one `"name":"…"` value out of the head of a
// rollout row, by hand rather than by parsing the whole thing.
//
// By hand on purpose — this runs against the first slice of every row in a file
// that reaches tens of megabytes, and parsing each one to read a timestamp would
// be most of the cost of showing a transcript at all. Which means it owns its own
// escape handling, and hand-rolled unescaping is exactly the sort of thing that
// works on every row anyone tried and then eats a command containing a quote.
public class CodexQuotedTests
{
    [Fact]
    public void APlainValueIsRead()
    {
        Assert.Equal("dotnet build",
            CodexTranscript.Quoted("""{"cmd":"dotnet build","x":1}""", "cmd"));
    }

    // The named key, not the first string in the row — a row has several.
    [Fact]
    public void TheNamedKeyIsTheOneRead()
    {
        const string head = """{"type":"exec","cmd":"git status","phase":"final"}""";

        Assert.Equal("git status", CodexTranscript.Quoted(head, "cmd"));
        Assert.Equal("exec", CodexTranscript.Quoted(head, "type"));
        Assert.Equal("final", CodexTranscript.Quoted(head, "phase"));
    }

    [Fact]
    public void AKeyThatIsNotThereIsNothing()
    {
        Assert.Null(CodexTranscript.Quoted("""{"cmd":"ls"}""", "missing"));
    }

    // ---- escapes ------------------------------------------------------------

    // \n and \t become the characters they stand for, since a command line can
    // carry either and showing a literal backslash-n in the panel would be wrong.
    [Fact]
    public void NewlineAndTabEscapesAreDecoded()
    {
        Assert.Equal("a\nb", CodexTranscript.Quoted("""{"cmd":"a\nb"}""", "cmd"));
        Assert.Equal("a\tb", CodexTranscript.Quoted("""{"cmd":"a\tb"}""", "cmd"));
    }

    // An escaped quote does NOT end the value — which is the whole reason this
    // cannot be a simple search for the next quote character, and the failure
    // would be a command silently cut in half.
    [Fact]
    public void AnEscapedQuoteDoesNotEndTheValue()
    {
        // Written with ordinary escapes rather than a raw literal: the expected
        // value ends in a quote, and a raw string whose content ends with one
        // needs a longer delimiter. Third time that has bitten on this branch.
        Assert.Equal("echo \"hi",
            CodexTranscript.Quoted("{\"cmd\":\"echo \\\"hi\"}", "cmd"));
    }

    // An escaped backslash is one backslash, not an escape for whatever follows.
    [Fact]
    public void AnEscapedBackslashIsOneBackslash()
    {
        Assert.Equal(@"C:\Users", CodexTranscript.Quoted("""{"cmd":"C:\\Users"}""", "cmd"));
    }

    // Any other escape yields the character itself rather than being dropped, so
    // an escape this decoder has not heard of degrades to something readable
    // instead of to nothing.
    [Fact]
    public void AnUnknownEscapeYieldsTheCharacterItself()
    {
        Assert.Equal("a/b", CodexTranscript.Quoted("""{"cmd":"a\/b"}""", "cmd"));
    }

    // ---- malformed ----------------------------------------------------------

    // A value with no closing quote — the row was cut off mid-write, which
    // happens because the file is appended to live — yields nothing rather than
    // the rest of the buffer.
    [Fact]
    public void AnUnterminatedValueYieldsNothing()
    {
        Assert.Null(CodexTranscript.Quoted("""{"cmd":"dotnet build""", "cmd"));
    }

    [Fact]
    public void AKeyWithNoValueAtAllYieldsNothing()
    {
        Assert.Null(CodexTranscript.Quoted("""{"cmd":""", "cmd"));
    }

    // A trailing lone backslash must not read past the end of the buffer.
    [Fact]
    public void ATrailingBackslashDoesNotRunOffTheEnd()
    {
        Assert.Null(CodexTranscript.Quoted("""{"cmd":"abc\""", "cmd"));
    }

    [Fact]
    public void AnEmptyValueIsAnEmptyString()
    {
        Assert.Equal("", CodexTranscript.Quoted("""{"cmd":""}""", "cmd"));
    }
}
