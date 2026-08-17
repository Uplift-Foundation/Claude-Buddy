using ClaudeBuddy;

// Two things that turn text nobody controls into things the panel shows, and
// both fail quietly when they fail.
//
// The transcript mapper decides what a conversation *was*, and its failure mode
// is a message silently missing — a row type it didn't recognise, a filter that
// caught too much. Nothing on screen says so.
//
// The dialog parser is worse. Its output becomes buttons that send keystrokes
// into a live session, so a mis-parse doesn't show a wrong label, it *presses
// the wrong thing*. Most of the cases below are attempts to make it do that.
//
// Run with `dotnet run --project tests/TranscriptTests`. Non-zero exit means
// something regressed and each failure prints what it expected.

// Given a file, says what the real parsers make of it instead of running the
// suite. A .jsonl is read as a transcript, anything else as a captured pane:
//
//   dotnet run --project tests/TranscriptTests -- ~/.claude/projects/<dir>/<id>.jsonl
//
//   tmux capture-pane -p -t %30 > /tmp/pane.txt
//   dotnet run --project tests/TranscriptTests -- /tmp/pane.txt
//
// Every fixture below was confirmed this way against a live Claude Code pane
// before being written down, which is the only reason they match real output
// rather than plausible output. The first version of the dialog parser was
// written against an invented fixture and got the shape wrong three ways.
if (args.Length > 0)
{
    var path = args[0];

    if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
    {
        // The tail, the same window ClaudeCodeChatSession opens with.
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var from = Math.Max(0, fs.Length - 512 * 1024);
        fs.Seek(from, SeekOrigin.Begin);
        var text = new StreamReader(fs).ReadToEnd();
        if (from > 0 && text.IndexOf('\n') >= 0) text = text[(text.IndexOf('\n') + 1)..];

        var turns = ChatTranscript.Map(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList());
        Console.WriteLine($"{turns.Count} turns from the last {(fs.Length - from) / 1024}KB\n");

        foreach (var t in turns)
        {
            var first = t.Turn.Text.Split('\n')[0];
            if (first.Length > 96) first = first[..96] + "…";
            Console.WriteLine($"{t.Turn.Role,-9} {first}");
        }

        return 0;
    }

    var dialog = ChatTranscript.ParseDialog(File.ReadAllText(path));

    if (dialog is null)
    {
        Console.WriteLine("no dialog — the panel would offer the terminal instead");
        return 0;
    }

    Console.WriteLine($"title: {dialog.Title}");
    foreach (var o in dialog.Options) Console.WriteLine($"  [{o.Key}] {o.Label}");
    return 0;
}

var failures = new List<string>();

void Check(string name, bool ok, string detail = "")
{
    if (ok) return;
    failures.Add(detail.Length > 0 ? $"{name}\n      {detail}" : name);
}

// --- the transcript ---

// Real row shapes, trimmed of the fields none of this reads.
const string UserSaid =
    """{"type":"user","uuid":"u1","timestamp":"2026-08-16T10:00:00Z","message":{"role":"user","content":"fix the arrangement test"}}""";

const string AssistantSaid =
    """{"type":"assistant","uuid":"a1","timestamp":"2026-08-16T10:00:09Z","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the nested-team case."}]}}""";

const string AssistantThought =
    """{"type":"assistant","uuid":"a2","timestamp":"2026-08-16T10:00:04Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"The clamp runs before the flip."}]}}""";

const string AssistantRead =
    """{"type":"assistant","uuid":"a3","timestamp":"2026-08-16T10:00:05Z","message":{"role":"assistant","content":[{"type":"tool_use","name":"Read","input":{"file_path":"/Users/w/Source/Claude-Buddy/OrbArrangement.cs"}}]}}""";

const string ToolResult =
    """{"type":"user","uuid":"u2","timestamp":"2026-08-16T10:00:06Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"1200 lines of file"}]}}""";

const string SidechainChatter =
    """{"type":"assistant","uuid":"s1","isSidechain":true,"timestamp":"2026-08-16T10:00:07Z","message":{"role":"assistant","content":[{"type":"text","text":"subagent progress"}]}}""";

const string SystemReminder =
    """{"type":"user","uuid":"u3","timestamp":"2026-08-16T10:00:08Z","message":{"role":"user","content":"<system-reminder>\nremember the thing\n</system-reminder>"}}""";

const string SlashCommand =
    """{"type":"user","uuid":"u4","timestamp":"2026-08-16T10:00:08Z","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""";

const string MetaRow =
    """{"type":"user","uuid":"u5","isMeta":true,"timestamp":"2026-08-16T10:00:08Z","message":{"role":"user","content":"hook output"}}""";

const string Queued =
    """{"type":"queue-operation","operation":"enqueue","uuid":"q1","timestamp":"2026-08-16T10:00:10Z","content":"and run the tests"}""";

const string Dequeued =
    """{"type":"queue-operation","operation":"dequeue","uuid":"q2","timestamp":"2026-08-16T10:00:11Z"}""";

const string Snapshot =
    """{"type":"file-history-snapshot","uuid":"f1","snapshot":{"a":"lots and lots of bytes"}}""";

var all = ChatTranscript.Map(new[]
{
    UserSaid, AssistantThought, AssistantRead, AssistantSaid, ToolResult,
    SidechainChatter, SystemReminder, SlashCommand, MetaRow, Queued, Dequeued, Snapshot
});

// Five of the twelve rows above are things a person said or watched happen;
// the other seven are scaffolding, and every one of them has a reason to be
// dropped listed against it below.
Check("maps exactly the five displayable rows", all.Count == 5,
    "got " + all.Count + ": " + string.Join(" | ", all.Select(r => r.Turn.Role + ":" + Head(r.Turn.Text))));

Check("keeps file order", all.Count == 5 && all[0].Turn.Text.StartsWith("fix the")
    && all[3].Turn.Text.StartsWith("Fixed the"));

Check("user text becomes a user turn",
    all.Any(r => r.Turn.Role == ChatRole.User && r.Turn.Text == "fix the arrangement test"));

Check("assistant text becomes an assistant turn",
    all.Any(r => r.Turn.Role == ChatRole.Assistant && r.Turn.Text == "Fixed the nested-team case."));

// Thinking is shown, but as a system line rather than as something the
// assistant said — the panel styles the two differently and the difference is
// the point.
Check("thinking becomes a system turn",
    all.Any(r => r.Turn.Role == ChatRole.System && r.Turn.Text == "The clamp runs before the flip."));

Check("tool_use is summarised to its basename",
    all.Any(r => r.Turn.Text == "· Read  OrbArrangement.cs"),
    string.Join(" | ", all.Where(r => r.Turn.Role == ChatRole.System).Select(r => r.Turn.Text)));

Check("queued message is shown",
    all.Any(r => r.Turn.Role == ChatRole.User && r.Turn.Text == "and run the tests"));

// Each of these has cost a version of this panel a screenful of noise.
Check("tool_result is dropped", all.All(r => !r.Turn.Text.Contains("1200 lines")));
Check("sidechain is dropped", all.All(r => r.Turn.Text != "subagent progress"));
Check("system-reminder is dropped", all.All(r => !r.Turn.Text.Contains("remember the thing")));
Check("slash command scaffolding is dropped", all.All(r => !r.Turn.Text.Contains("/clear")));
Check("isMeta row is dropped", all.All(r => r.Turn.Text != "hook output"));
Check("dequeue adds nothing", all.Count(r => r.Turn.Text.Contains("run the tests")) == 1);

// The cheap pre-filter and the real mapper have to agree about which rows
// matter. If IsInteresting ever says no to something MapRow would have mapped,
// that message is dropped and nothing anywhere reports it.
foreach (var (row, label) in new[]
         {
             (UserSaid, "user"), (AssistantSaid, "assistant text"),
             (AssistantThought, "thinking"), (AssistantRead, "tool_use"), (Queued, "enqueue")
         })
{
    Check($"pre-filter admits {label}", ChatTranscript.IsInteresting(row));
    Check($"pre-filter agrees with mapper for {label}",
        ChatTranscript.IsInteresting(row) == ChatTranscript.Map(new[] { row }).Count > 0
        || ChatTranscript.Map(new[] { row }).Count > 0);
}

Check("pre-filter rejects a file-history snapshot", !ChatTranscript.IsInteresting(Snapshot));

// Rows arrive half-written, and Claude Code's format changes between versions.
// Neither may throw — the panel would stop updating for the life of the process.
Check("a truncated row is skipped, not thrown",
    ChatTranscript.Map(new[] { """{"type":"assistant","message":{"content":[{"typ""" }).Count == 0);

Check("an unknown row type is skipped",
    ChatTranscript.Map(new[] { """{"type":"something-new","uuid":"x","payload":42}""" }).Count == 0);

Check("a row with no message is skipped",
    ChatTranscript.Map(new[] { """{"type":"assistant","uuid":"x"}""" }).Count == 0);

Check("empty text produces no turn",
    ChatTranscript.Map(new[]
    {
        """{"type":"assistant","uuid":"x","message":{"content":[{"type":"text","text":"   "}]}}"""
    }).Count == 0);

// One assistant row routinely carries several blocks, and each is its own row
// in the panel.
var multi = ChatTranscript.Map(new[]
{
    """{"type":"assistant","uuid":"m1","message":{"content":[{"type":"thinking","thinking":"weigh it"},{"type":"tool_use","name":"Bash","input":{"command":"dotnet build --nologo /p:Foo=bar"}},{"type":"text","text":"done"}]}}"""
});

Check("one row can become three turns", multi.Count == 3, "got " + multi.Count);
Check("bash summary collapses whitespace and keeps the command",
    multi.Count == 3 && multi[1].Turn.Text == "· Bash  dotnet build --nologo /p:Foo=bar",
    multi.Count == 3 ? multi[1].Turn.Text : "");

var longArg = ChatTranscript.Map(new[]
{
    """{"type":"assistant","uuid":"m2","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}]}}"""
});
Check("a long tool argument is truncated", longArg.Count == 1 && longArg[0].Turn.Text.EndsWith("…")
    && longArg[0].Turn.Text.Length < 70, longArg.Count == 1 ? longArg[0].Turn.Text : "");

Check("a tool with no recognised argument still names itself",
    ChatTranscript.Map(new[]
    {
        """{"type":"assistant","uuid":"m3","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[]}}]}}"""
    })[0].Turn.Text == "· TodoWrite");

// --- the dialog ---

// These two are transcribed from `tmux capture-pane -p` against a real Claude
// Code 2.1.233 pane, not composed here. That distinction earned its keep: the
// first version of this parser was written against an invented fixture with a
// box around it and a question directly above the options, and it failed on
// every real dialog — there is no box, and there are lines *below* the options.
const string BashApproval = """
     Bash command

       pid=$$; for i in 1 2 3 4 5 6 7 8; do line=$(ps -o pid=,ppid= -p $pid)
       Walk the process ancestry of this shell

     Contains simple_expansion

     Do you want to proceed?
     ❯ 1. Yes
       2. No

     Esc to cancel · Tab to amend · ctrl+e to explain
    """;

var bash = ChatTranscript.ParseDialog(BashApproval);
Check("a real Bash approval parses", bash is not null);
Check("it finds both options", bash?.Options.Count == 2, "got " + (bash?.Options.Count.ToString() ?? "null"));
Check("the title is the question", bash?.Title == "Do you want to proceed?", bash?.Title ?? "null");
Check("labels are the dialog's own words", bash?.Options[1].Label == "No", bash?.Options[1].Label ?? "null");
Check("keys are the digits to press",
    bash is not null && bash.Options.Select(o => o.Key).SequenceEqual(new[] { "1", "2" }));

// The footer under the options is the thing that broke reading upward from the
// bottom, so it gets an assertion of its own rather than only being present in
// the fixture above.
Check("a footer below the options doesn't hide them",
    ChatTranscript.ParseDialog(BashApproval + "\n\n\n")?.Options.Count == 2);

// A plan prompt: the question is separated from the options by a blank line,
// and the last option carries an indented continuation underneath it.
const string PlanApproval = """
       ls -ld /tmp/cb-probe-nonexistent   # expect: No such file or directory

    ╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌
    ────────────────────────────────────────────────────────────────────
     Claude has written up a plan and is ready to execute. Would you like to proceed?

     ❯ 1. Yes, and use auto mode
       2. Yes, manually approve edits
       3. Tell Claude what to change
          shift+tab to approve with this feedback

     ctrl+g to edit in nano · ~/.claude/plans/run-the-shell-command.md
    """;

var plan = ChatTranscript.ParseDialog(PlanApproval);
Check("a real plan prompt parses", plan is not null);
Check("it finds all three options", plan?.Options.Count == 3, "got " + (plan?.Options.Count.ToString() ?? "null"));
Check("an indented continuation isn't read as a fourth option",
    plan?.Options.Count == 3 && plan.Options[2].Label == "Tell Claude what to change",
    plan?.Options.Count == 3 ? plan.Options[2].Label : "");
Check("a blank line between question and options doesn't lose the title",
    plan?.Title.StartsWith("Claude has written up a plan") == true, plan?.Title ?? "null");

// Everything below must refuse rather than guess. A wrong answer here presses a
// key in a live session.

Check("an ordinary working pane is not a dialog",
    ChatTranscript.ParseDialog("""
        ⏺ Fixed the nested-team case.

        ✻ Cogitated for 37s
        ────────────────────────────────────────────────────────────────────
        ❯
        ────────────────────────────────────────────────────────────────────
          Opus 5 | ~/probe | 29% | [.claude]
        """) is null);

// The important false positive, and the reason the input box is what's tested
// for: the assistant writes numbered lists all the time. What makes this prose
// rather than a dialog is that the input box is still drawn below it — a real
// dialog replaces the box while it is up.
Check("a numbered list in prose is not a dialog",
    ChatTranscript.ParseDialog("""
        ⏺ Three things worth doing:

          1. Extract the geometry
          2. Add the test
          3. Delete the old path

        ────────────────────────────────────────────────────────────────────
        ❯
        ────────────────────────────────────────────────────────────────────
          Opus 5 | ~/probe | 29% | [.claude]
        """) is null,
    "a numbered list above a live input box must not be read as options");

Check("a single option is not a dialog", ChatTranscript.ParseDialog("""
     Do you want to proceed?
     ❯ 1. Yes
    """) is null);

Check("options that don't start at 1 are refused", ChatTranscript.ParseDialog("""
     Do you want to proceed?
     ❯ 2. Yes
       3. No
    """) is null);

Check("a gap in the numbering is refused", ChatTranscript.ParseDialog("""
     Do you want to proceed?
     ❯ 1. Yes
       2. Maybe
       4. No
    """) is null);

Check("an empty screen is refused", ChatTranscript.ParseDialog("") is null);
Check("whitespace is refused", ChatTranscript.ParseDialog("\n\n   \n") is null);

// A framed dialog with nothing above the options: the frame is not a question.
var noQuestion = ChatTranscript.ParseDialog("""
    ╭─────────────╮
    │ ❯ 1. Yes    │
    │   2. No     │
    ╰─────────────╯
    """);
Check("a frame edge doesn't become the title", noQuestion?.Title == "Waiting for input",
    noQuestion?.Title ?? "null");

// --- report ---

if (failures.Count == 0)
{
    Console.WriteLine("all passed");
    return 0;
}

Console.WriteLine($"{failures.Count} failed:");
foreach (var f in failures) Console.WriteLine("  ✗ " + f);
return 1;

static string Head(string s)
{
    var line = s.Split('\n')[0];
    return line.Length > 40 ? line[..40] + "…" : line;
}
