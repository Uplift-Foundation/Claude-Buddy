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

// --- markdown ---

// The failure mode here is visible rather than silent — you see the asterisks —
// but the mis-fires are not: a rule that eats `snake_case`, or an unclosed
// delimiter in a half-streamed reply swallowing the rest of the paragraph.

static ChatMarkdown.MdBlock[] Blocks(string s) => ChatMarkdown.Parse(s).ToArray();
static ChatMarkdown.MdSpan[] Spans(string s) => ChatMarkdown.Inline(s).ToArray();

var spans = Spans("Fixed **the clamp** in `OrbArrangement.cs` — see *why* below.");
Check("bold is found", spans.Any(s => s.Style == ChatMarkdown.MdStyle.Bold && s.Text == "the clamp"));
Check("inline code is found",
    spans.Any(s => s.Style == ChatMarkdown.MdStyle.Code && s.Text == "OrbArrangement.cs"));
Check("italic is found", spans.Any(s => s.Style == ChatMarkdown.MdStyle.Italic && s.Text == "why"));
Check("delimiters are not in the output", spans.All(s => !s.Text.Contains('*') && !s.Text.Contains('`')));
Check("nothing is lost", string.Concat(spans.Select(s => s.Text))
    == "Fixed the clamp in OrbArrangement.cs — see why below.");

// The reason underscores are not emphasis. This is the single most common
// string in this app's own conversations.
var snake = Spans("the file_path and session_pid fields");
Check("underscores are left alone", snake.Length == 1 && snake[0].Style == ChatMarkdown.MdStyle.Normal,
    string.Join(" | ", snake.Select(s => s.Style + ":" + s.Text)));

// A reply being streamed is half-written by definition.
var unclosed = Spans("this is **not finished");
Check("an unclosed delimiter stays literal",
    unclosed.Length == 1 && unclosed[0].Text == "this is **not finished",
    string.Join(" | ", unclosed.Select(s => s.Style + ":" + s.Text)));

Check("an unclosed backtick stays literal",
    Spans("run `dotnet buil").Single().Text == "run `dotnet buil");

// Markup inside a code span is text, which is why code is matched first.
var literal = Spans("write `**bold**` like that");
Check("markup inside code is literal",
    literal.Any(s => s.Style == ChatMarkdown.MdStyle.Code && s.Text == "**bold**"),
    string.Join(" | ", literal.Select(s => s.Style + ":" + s.Text)));

Check("bold-italic is three stars",
    Spans("***very***").Single().Style == ChatMarkdown.MdStyle.BoldItalic);

var link = Spans("see [the findings](docs/openclaw-findings.md) for more");
Check("a link keeps its label and drops its url",
    link.Any(s => s.Style == ChatMarkdown.MdStyle.Link && s.Text == "the findings")
    && link.All(s => !s.Text.Contains(".md")),
    string.Join(" | ", link.Select(s => s.Style + ":" + s.Text)));

// Blocks.
var fenced = Blocks("""
    Here is the fix:

    ```csharp
    var x = 1;
    if (x > 0) return;
    ```

    That's it.
    """);

Check("a fence becomes one code block", fenced.Count(b => b.Kind == ChatMarkdown.MdKind.Code) == 1);
Check("the code keeps its line breaks",
    fenced.Single(b => b.Kind == ChatMarkdown.MdKind.Code).Text == "var x = 1;\nif (x > 0) return;",
    fenced.Single(b => b.Kind == ChatMarkdown.MdKind.Code).Text.Replace("\n", "\\n"));
Check("the language is kept",
    fenced.Single(b => b.Kind == ChatMarkdown.MdKind.Code).Marker == "csharp");
Check("prose around the fence survives",
    fenced.Count(b => b.Kind == ChatMarkdown.MdKind.Paragraph) == 2);

// Indented code inside an already-indented bubble reads as doubly indented.
Check("a code block is dedented",
    Blocks("```\n    one\n      two\n```").Single().Text == "one\n  two");

// A fence that hasn't closed yet is the normal case mid-reply.
Check("an unclosed fence is still a code block",
    Blocks("```\nvar x = 1;").Single().Kind == ChatMarkdown.MdKind.Code);

var list = Blocks("""
    Three things:

    - extract the geometry
    - add the test
    - delete the old path
    """);
Check("bullets become bullet blocks", list.Count(b => b.Kind == ChatMarkdown.MdKind.Bullet) == 3);
Check("the bullet glyph is not the source dash",
    list.First(b => b.Kind == ChatMarkdown.MdKind.Bullet).Marker == "•");
Check("bullet text loses its marker",
    list.First(b => b.Kind == ChatMarkdown.MdKind.Bullet).Text == "extract the geometry");

var ordered = Blocks("4. fourth\n5. fifth");
Check("an ordered list keeps its own numbering",
    ordered.Length == 2 && ordered[0].Marker == "4." && ordered[1].Marker == "5.",
    string.Join(" | ", ordered.Select(b => b.Marker)));

Check("a heading is a heading",
    Blocks("## Findings").Single() is { Kind: ChatMarkdown.MdKind.Heading, Text: "Findings", Depth: 2 });

Check("a hash with no space is not a heading",
    Blocks("#hashtag not a heading").Single().Kind == ChatMarkdown.MdKind.Paragraph);

// Reflowing a table destroys it; monospace at least keeps the columns.
var table = Blocks("| a | b |\n| - | - |\n| 1 | 2 |");
Check("a table is kept verbatim as code",
    table.Single().Kind == ChatMarkdown.MdKind.Code && table.Single().Text.Split('\n').Length == 3);

Check("a rule draws nothing", Blocks("one\n\n---\n\ntwo").All(b => b.Text != "---"));

Check("wrapped prose joins into one paragraph",
    Blocks("a line\nand its continuation").Single().Text == "a line and its continuation");

Check("a blank line separates paragraphs", Blocks("first\n\nsecond").Length == 2);

Check("empty text is no blocks", Blocks("").Length == 0 && Blocks("   \n  ").Length == 0);

// --- agent colours ---

// The property that matters is stability: an agent that changed colour between
// launches would be worse than every agent sharing one, because the ring would
// be actively misleading rather than merely uninformative. That rules out
// string.GetHashCode(), which is randomised per process — a test can't catch
// that within one process, so the hash is hand-written and pinned below.
var agents = new[] { "main", "lilibeth", "zara", "asher", "main-2", "ops", "scribe", "warden" };

Check("a colour is stable within a run",
    agents.All(a => AgentPalette.HexFor(a) == AgentPalette.HexFor(a)));

// Pinned. If these change, every agent silently changes colour on upgrade —
// which is the thing the whole derived-not-stored design exists to prevent. A
// deliberate change to the palette means updating these on purpose.
Check("the hash is pinned to known values",
    AgentPalette.HexFor("main") == "#5FBFD7"
    && AgentPalette.HexFor("lilibeth") == "#5FD7A7"
    && AgentPalette.HexFor("zara") == "#5F9DD7",
    $"main={AgentPalette.HexFor("main")} lilibeth={AgentPalette.HexFor("lilibeth")} "
    + $"zara={AgentPalette.HexFor("zara")}");

// The reason Assign exists: agents whose hashes land on the same anchor must
// not ship as identical orbs.
var pair = AgentPalette.Assign(new[] { "warden", "main-3" });
Check("colliding agents are separated", pair["warden"] != pair["main-3"],
    $"warden={pair["warden"]} main-3={pair["main-3"]}");

var assigned = AgentPalette.Assign(agents);
Check("every agent in a set gets a colour", assigned.Count == agents.Length);
Check("a set's colours are all distinct", assigned.Values.Distinct().Count() == agents.Length,
    string.Join(" ", assigned.Select(kv => kv.Key + "=" + kv.Value)));

Check("assignment doesn't depend on listing order",
    AgentPalette.Assign(agents.Reverse()).OrderBy(kv => kv.Key).SequenceEqual(
        assigned.OrderBy(kv => kv.Key)));

Check("duplicates in the input are harmless",
    AgentPalette.Assign(new[] { "main", "main", "zara" }).Count == 2);

// Separation has to hold across the wrap: hue 350 and hue 10 are 20 apart.
static int HueOf(string hex)
{
    int r = Convert.ToInt32(hex[1..3], 16), g = Convert.ToInt32(hex[3..5], 16), b = Convert.ToInt32(hex[5..7], 16);
    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
    if (max == min) return 0;
    double h = max == r ? (g - b) / (double)(max - min)
        : max == g ? 2 + (b - r) / (double)(max - min)
        : 4 + (r - g) / (double)(max - min);
    return ((int)Math.Round(h * 60) + 360) % 360;
}

var hues = assigned.Values.Select(HueOf).ToList();
var closest = 360;
for (var a = 0; a < hues.Count; a++)
    for (var b = a + 1; b < hues.Count; b++)
    {
        var d = Math.Abs(hues[a] - hues[b]) % 360;
        closest = Math.Min(closest, Math.Min(d, 360 - d));
    }

// Thirty degrees, not the 45 that dividing the circle by eight suggests:
// placement starts from wherever each id hashes to rather than from an even
// grid, so the achievable separation is well below the theoretical one. What
// matters is that it is a real step — this is the assertion that would have
// caught the 13° pair a person called "the same colour".
Check("eight agents are at least 30° apart", closest >= 30,
    $"closest pair is {closest}° apart in {string.Join(",", hues.OrderBy(h => h))}");

// Separation scales with the count rather than being a fixed number: fewer
// agents must be spread *further*, not the same distance with gaps left over.
var three = AgentPalette.Assign(new[] { "one", "two", "three" }).Values.Select(HueOf).ToList();
var threeClosest = 360;
for (var a = 0; a < three.Count; a++)
    for (var b = a + 1; b < three.Count; b++)
    {
        var d = Math.Abs(three[a] - three[b]) % 360;
        threeClosest = Math.Min(threeClosest, Math.Min(d, 360 - d));
    }

Check("three agents are spread further than eight", threeClosest >= 55,
    $"closest of three is {threeClosest}°");

// Crowded, but every one still distinct and nobody dropped.
var crowd = AgentPalette.Assign(Enumerable.Range(0, 40).Select(n => "agent-" + n));
Check("forty agents are all handled", crowd.Count == 40);
Check("forty agents are all distinct", crowd.Values.Distinct().Count() == 40,
    crowd.Values.Distinct().Count().ToString());

// The whole wheel is in use — not a fixed set of named colours.
var manyHues = crowd.Values.Select(HueOf).ToList();
Check("colours are spread across the spectrum",
    manyHues.Any(h => h < 60) && manyHues.Any(h => h is >= 60 and < 180)
    && manyHues.Any(h => h is >= 180 and < 300) && manyHues.Any(h => h >= 300),
    string.Join(",", manyHues.OrderBy(h => h)));

Check("every colour is a valid #RRGGBB",
    agents.All(a =>
    {
        var hex = AgentPalette.HexFor(a);
        return hex.Length == 7 && hex[0] == '#'
            && hex[1..].All(Uri.IsHexDigit) && hex[1..] == hex[1..].ToUpperInvariant();
    }));

Check("different agents get different colours",
    agents.Select(AgentPalette.HexFor).Distinct().Count() == agents.Length,
    string.Join(" ", agents.Select(a => a + "=" + AgentPalette.HexFor(a))));

// Ids that differ only in their last character are the realistic collision:
// "main", "main-1", "main-2" is exactly the case that made four orbs read "M".
Check("near-identical ids are well separated",
    new[] { "main", "main-1", "main-2", "main-3" }.Select(AgentPalette.HexFor).Distinct().Count() == 4);

// Every generated colour sits on Claude Code's own saturation/value surface, so
// it reads as the same kind of colour as a /color one — see AgentPalette.
foreach (var agent in agents)
{
    var hex = AgentPalette.HexFor(agent);
    var r = Convert.ToInt32(hex[1..3], 16);
    var g = Convert.ToInt32(hex[3..5], 16);
    var b = Convert.ToInt32(hex[5..7], 16);

    var max = Math.Max(r, Math.Max(g, b));
    var min = Math.Min(r, Math.Min(g, b));

    // #D75F5F, #5F87D7 and the rest of Claude's palette are all max 215,
    // min 95 — the same numbers these must land on, within rounding.
    Check($"{agent} sits on Claude's palette surface",
        Math.Abs(max - 215) <= 1 && Math.Abs(min - 95) <= 1,
        $"{hex} -> max {max}, min {min}");
}

Check("an empty id doesn't throw", AgentPalette.HexFor("").StartsWith('#'));

// --- session kinds ---

// The mistake this can make is silent and directional: a channel shown as a
// direct message says a room other people can read is private, and nothing on
// screen contradicts it. So the unrecognised cases assert Unknown rather than a
// guess at the commoner of the two.

static SessionKind Kind(string key, string? chatType = null) =>
    OpenClawSessionKind.From(key, chatType);

// Real keys, from docs/openclaw-findings.md.
Check("a cron job is a cron job",
    Kind("agent:main:cron:2f54203e-1c2f-4a1e-9c0e-2b1d8e5a7c31") == SessionKind.Cron);

Check("cron wins over anything attached to it",
    Kind("agent:main:cron:2f54203e", "direct") == SessionKind.Cron,
    "the key is structural; chatType must not override it");

Check("an agent's own session is Main",
    Kind("agent:alexis:main") == SessionKind.Main);

Check("a DM is Direct",
    Kind("agent:main:discord:direct:246722755112861696") == SessionKind.Direct);

Check("a channel is a Channel",
    Kind("agent:main:discord:channel:1474991965354463274") == SessionKind.Channel);

// origin.chatType is what separates these two when the key says only the
// surface, which is the usual case.
Check("chatType decides when the key only names a surface",
    Kind("agent:main:discord", "channel") == SessionKind.Channel
    && Kind("agent:main:discord", "direct") == SessionKind.Direct);

Check("chatType is preferred over the key's fourth segment",
    Kind("agent:main:discord:direct:2467", "channel") == SessionKind.Channel,
    "origin is the gateway's own word for the conversation");

Check("the surfaces' other words for a group are all Channel",
    new[] { "channel", "group", "guild" }.All(t => Kind("agent:m:slack", t) == SessionKind.Channel));

Check("the surfaces' other words for a DM are all Direct",
    new[] { "direct", "dm", "im" }.All(t => Kind("agent:m:slack", t) == SessionKind.Direct));

Check("case doesn't matter", Kind("agent:m:CRON:x") == SessionKind.Cron
    && Kind("agent:m:slack", "Direct") == SessionKind.Direct);

// Everything below must decline to guess.
Check("an unrecognised chatType is Unknown",
    Kind("agent:main:discord", "thread") == SessionKind.Unknown);
Check("a surface with no chatType is Unknown",
    Kind("agent:main:discord") == SessionKind.Unknown);
Check("an empty key is Unknown", Kind("") == SessionKind.Unknown);
Check("a non-agent key is Unknown", Kind("something:else:entirely") == SessionKind.Unknown);
Check("a null key is Unknown", OpenClawSessionKind.From(null, null) == SessionKind.Unknown);

// --- rooms ---

// Every agent in a channel has to agree on the room key, or a room fragments
// into one orb per agent and the grouping is worse than none.
static string? Room(string key) => OpenClawSessionKind.RoomOf(key);

Check("agents in one channel agree on the room",
    Room("agent:lilibeth:discord:channel:1474991965354463274")
    == Room("agent:zara:discord:channel:1474991965354463274")
    && Room("agent:zara:discord:channel:1474991965354463274") == "discord:1474991965354463274");

Check("different channels are different rooms",
    Room("agent:zara:discord:channel:111") != Room("agent:zara:discord:channel:222"));

// The same channel id on two surfaces is not the same room.
Check("the surface is part of the room",
    Room("agent:z:discord:channel:111") != Room("agent:z:slack:channel:111"));

// A DM is not a room: two people talking privately is not somewhere others
// can be standing.
Check("a direct message is not a room", Room("agent:main:discord:direct:2467") is null);
Check("a cron job is not a room", Room("agent:main:cron:2f54203e") is null);
Check("an agent's own session is not a room", Room("agent:alexis:main") is null);
Check("a malformed key is not a room",
    Room("") is null && Room("agent:z:discord") is null && Room("nonsense") is null);

// A channel id containing a colon must not be truncated into a different room.
Check("a colon in the channel id survives",
    Room("agent:z:matrix:channel:!abc:server.org") == "matrix:!abc:server.org");

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
