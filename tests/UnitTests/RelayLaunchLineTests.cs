using Xunit;

namespace ClaudeBuddy.Tests;

// The line a relay is started with.
//
// Every flag on it is a measured failure rather than a preference, and none of
// them is visible from reading the string — which is how CB-40 happened: the
// launch line named the two tools a relay uses and forbade nothing, so the
// relay's model could reach for `gunzip` on a mirror frame, land on a
// permission prompt, and stop an unattended machine serving until somebody
// pressed a key.
//
// A test cannot start a relay, so before LaunchLine was split out the only way
// to check a flag was still there was to run one. These cases are the cheap
// version of that.
public class RelayLaunchLineTests
{
    private static string Line() => RemoteControlBridge.LaunchLine(
        "/usr/local/bin/claude",
        "/tmp/cb rc/scratch",
        "/Users/someone/.claude-board",
        "claude-buddy-rc--claude-board-machine");

    [Fact]
    public void Names_the_two_tools_a_relay_actually_calls()
    {
        Assert.Contains("--allowedTools SendMessage ListAgents", Line());
    }

    // The mode is not the permissive one on purpose — see the launch line's own
    // comment about the auto-mode classifier reading a base64 blob relayed to
    // another agent as exfiltration, which is a fair reading.
    [Fact]
    public void Uses_accept_edits_rather_than_skipping_permissions()
    {
        var line = Line();

        Assert.Contains("--permission-mode acceptEdits", line);
        Assert.DoesNotContain("--dangerously-skip-permissions", line);
    }

    // CB-40's first half. Bash is the one the jam was measured on; the rest are
    // the tools a relay has no business calling either, and each is one more
    // prompt it cannot block on.
    [Theory]
    [InlineData("Bash")]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("WebFetch")]
    [InlineData("WebSearch")]
    [InlineData("Glob")]
    [InlineData("Grep")]
    [InlineData("Task")]
    public void Forbids_the_tools_a_relay_never_needs(string tool)
    {
        var line = Line();
        var disallowed = line[line.IndexOf("--disallowedTools", StringComparison.Ordinal)..];

        Assert.Contains(tool, disallowed);
    }

    // CB-40's second half, and the one that keeps working when a tool name this
    // list has never heard of turns up.
    [Fact]
    public void Tells_the_relay_what_a_frame_is()
    {
        Assert.Contains("--append-system-prompt", Line());
    }

    // Quoted as one shell word, or the shell takes the second word of the
    // prompt as the next argument and the relay never starts.
    [Fact]
    public void Quotes_the_system_prompt_and_every_path()
    {
        var line = Line();

        Assert.Contains("'You are a relay for Claude Buddy.", line);
        Assert.Contains("TMPDIR='/tmp/cb rc/scratch'", line);
        Assert.Contains("CLAUDE_CONFIG_DIR='/Users/someone/.claude-board'", line);
        Assert.Contains("'/usr/local/bin/claude'", line);
    }

    [Fact]
    public void Carries_the_account_and_the_relay_name()
    {
        var line = Line();

        Assert.Contains(".claude-board", line);
        Assert.Contains("--remote-control 'claude-buddy-rc--claude-board-machine'", line);

        // The private TMPDIR is what keeps the relay out of Buddy's own orb
        // scan — the hook writes its status file under it.
        Assert.StartsWith("TMPDIR=", line);
    }
}

// What the relay is told, as prose rather than as a flag.
public class RelaySystemPromptTests
{
    [Fact]
    public void Names_both_kinds_of_line_the_app_reads_for_itself()
    {
        Assert.Contains("CB-MIRROR:", RemoteControlBridge.RelaySystemPrompt);
        Assert.Contains("CB-INFO:", RemoteControlBridge.RelaySystemPrompt);
    }

    // The measured jam was the model *investigating* a frame, so saying "don't
    // reply" alone would not have prevented it.
    [Fact]
    public void Rules_out_investigating_a_frame_and_not_merely_answering_one()
    {
        var prompt = RemoteControlBridge.RelaySystemPrompt;

        Assert.Contains("decode", prompt);
        Assert.Contains("run commands", prompt);
        Assert.Contains("suspicious", prompt);
    }

    // A relay carries real messages between people as well. Teaching it to
    // ignore a frame must not teach it to ignore those — that would trade a
    // stalled mirror for a dropped conversation, which is worse.
    [Fact]
    public void Leaves_ordinary_messages_alone()
    {
        Assert.Contains("Every other message", RemoteControlBridge.RelaySystemPrompt);
    }
}
