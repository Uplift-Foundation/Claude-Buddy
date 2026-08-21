using ClaudeBuddy;

// The two or three letters an orb wears, and the ones the chat panel's header
// wears beside it.
//
// Run it with `dotnet run --project tests/GlyphTests`. Non-zero exit means
// something regressed, and each failure prints the input and both answers.
//
// This suite exists because the initials were wrong for a year without anyone
// noticing: every kebab-case directory drew two letters off the front of its
// first word, so "claude-buddy" was "Cl" when it should have been "Cb". It was
// invisible partly because it only looks wrong when the two halves start with
// different letters, and partly because the only way to see the answer was to
// look at the screen. The rules below are cheap to assert and were not being
// asserted at all.
//
// Cases are grouped by the reason each one is here rather than by input, so a
// failure says which rule broke and not merely which string moved.

var cases = new (string Group, string Input, bool TwoLetter, string Want)[]
{
    // Kebab and snake case — a session is named for its directory, and this is
    // what directories look like. The bug that prompted the suite.
    ("word breaks",   "claude-buddy",       true,  "Cb"),
    ("word breaks",   "Claude-Buddy",       true,  "Cb"),
    ("word breaks",   "my_cool_project",    true,  "Mc"),
    ("word breaks",   "e-commerce",         true,  "Ec"),

    // All three dashes, spaced or not. Spaced already worked; unspaced did not.
    ("dashes",        "Lilibeth — wtvamp",  true,  "Lw"),
    ("dashes",        "Lilibeth—wtvamp",    true,  "Lw"),
    ("dashes",        "Lilibeth–wtvamp",    true,  "Lw"),

    // A separator that leads or repeats contributes no word of its own.
    ("empty words",   "-leading",           true,  "Le"),
    ("empty words",   "a--b",               true,  "Ab"),
    ("empty words",   "  spaced  out  ",    true,  "So"),

    // Not every punctuation mark is a word break. '.' and '/' appear in
    // versions and paths, where what follows is not a word to take from.
    ("not separators","v1.2.3",             true,  "V1"),
    ("not separators","a/b",                true,  "A/"),

    // Two real words: one letter each, the initials a person would write.
    ("two words",     "Menu UX",            true,  "Mu"),
    ("two words",     "Ada Lovelace",       true,  "Al"),

    // One word: two letters from itself, which is still right for "Menu".
    ("one word",      "Menu",               true,  "Me"),
    ("one word",      "x",                  true,  "X"),

    // Leading punctuation is skipped, in both branches. A room orb is named
    // for its channel, so "#" leads far more often than not.
    ("punctuation",   "#kubernetes",        true,  "Ku"),
    ("punctuation",   "#arch",              true,  "Ar"),
    ("punctuation",   "#dev ops",           true,  "Do"),
    ("punctuation",   "!!!",                true,  "!!"),

    // Nothing to draw. The orb shows a dot rather than an empty circle.
    ("empty",         "",                   true,  "•"),
    ("empty",         "   ",                true,  "•"),

    // An emoji is a surrogate pair; slicing one in half draws a tofu box.
    ("emoji",         "\U0001F680 rocket",  true,  "\U0001F680r"),
    ("emoji",         "\U0001F680",         false, "\U0001F680"),

    // With the setting off, one letter, and the same skipping does NOT apply —
    // this branch has always taken the very first character. Asserted so the
    // difference is a decision on the record rather than a surprise later.
    ("single letter", "claude-buddy",       false, "C"),
    ("single letter", "Menu UX",            false, "M"),
    ("single letter", "#arch",              false, "#"),
};

// The chat panel's header. Same word breaks, both letters capitalised, and no
// bullet for the empty case — an empty circle is correct there.
var initialsCases = new (string Input, string Want)[]
{
    ("claude-buddy",    "CB"),
    ("Ada Lovelace",    "AL"),
    ("Annabel Lee",     "AL"),
    ("Lilibeth",        "LI"),
    ("my_cool_project", "MC"),
    ("x",               "X"),
    ("",                ""),
    ("   ",             ""),
};

// Who a chip belongs to. Same suite because it is the same question one step
// earlier — these letters are drawn from whatever this resolves to, and both
// were wrong in the same way: read once, too early, from the wrong place.
var speakerCases = new (string Why, string? Identity, string? Title, string? Previous, string? Want)[]
{
    // The identity wins wherever there is one. A room's title is the room.
    ("agent in a room",      "Lilibeth", "#openclaw-management", null, "Lilibeth"),
    ("agent, no title",      "Lilibeth", "",                     null, "Lilibeth"),

    // A terminal session has no identity; its title is genuinely its agent.
    ("terminal session",     null,       "claude-buddy",         null, "claude-buddy"),
    ("terminal, blank id",   "",         "claude-buddy",         null, "claude-buddy"),
    ("whitespace identity",  "   ",      "claude-buddy",         null, "claude-buddy"),

    // The whole point: not knowing is never an answer. A gateway reconnect
    // empties the agent list, and a status update in that window used to wipe
    // the chips off a transcript that had been showing them.
    ("identity list dropped", null,      "",                     "Lilibeth", "Lilibeth"),
    ("everything dropped",    null,      null,                   "Lilibeth", "Lilibeth"),
    ("blank over known",      "",        "   ",                  "Lilibeth", "Lilibeth"),

    // But a real change is a real change — a rebind to another session.
    ("rebound elsewhere",     "Alexis",  "#general",             "Lilibeth", "Alexis"),
    ("title changed",         null,      "codex-work",           "claude-buddy", "codex-work"),

    // Nothing known yet, and nothing known before. Bare bubbles are correct
    // here: inventing a name would be worse than showing none.
    ("nothing at all",        null,      null,                   null, null),
    ("all blank",             "",        "",                     "",   ""),
};

var failures = new List<string>();

foreach (var (why, identity, title, previous, want) in speakerCases)
{
    var got = ChatSpeaker.Resolve(identity, title, previous);
    if (got != want)
        failures.Add($"speaker ({why}): Resolve({Show(identity)}, {Show(title)}, {Show(previous)}) = {Show(got)}, wanted {Show(want)}");
}

static string Show(string? s) => s is null ? "null" : $"\"{s}\"";

foreach (var (group, input, twoLetter, want) in cases)
{
    var got = OrbGlyph.For(input, twoLetter);
    if (got != want)
        failures.Add($"{group}: For(\"{input}\", twoLetter: {twoLetter.ToString().ToLowerInvariant()}) = \"{got}\", wanted \"{want}\"");
}

foreach (var (input, want) in initialsCases)
{
    var got = OrbGlyph.Initials(input);
    if (got != want)
        failures.Add($"header initials: Initials(\"{input}\") = \"{got}\", wanted \"{want}\"");
}

// Null is a separate call rather than a row, because the table is not nullable.
if (OrbGlyph.Initials(null) != "")
    failures.Add($"header initials: Initials(null) = \"{OrbGlyph.Initials(null)}\", wanted \"\"");

var total = cases.Length + initialsCases.Length + speakerCases.Length + 1;

if (failures.Count == 0)
{
    Console.WriteLine($"{total} cases, all passed");
    return 0;
}

Console.WriteLine($"{total} cases, {failures.Count} failed\n");
foreach (var failure in failures) Console.WriteLine($"  {failure}");
return 1;
