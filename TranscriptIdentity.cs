using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ClaudeBuddy
{
    // What a session calls itself and what colour it has been given, read out of
    // its own transcript.
    //
    // The hook already does this and writes both into the status file, which is
    // where the app normally reads them. This exists for the case where that
    // answer is missing and never gets corrected: the hook only runs when
    // something happens, so a session that goes quiet immediately after being
    // named keeps whatever the status file caught at the time — forever.
    //
    // Measured on a real machine, and the reason this file exists. A background
    // job forked from an interactive session (`{"type":"history-suppression",
    // "cause":"fork_inherit"}` as its first row, 34 of its 35 message uuids
    // shared with the parent) had `{"type":"custom-title","customTitle":
    // "evidence (2)"}` as its *second* row. Its status file recorded an empty
    // title, mtimed to the same second as the fork's own timestamp: the hook
    // fired once, at fork creation, lost the race with Claude Code's append of
    // that row, and never fired again because the job went idle. So the orb drew
    // the letters of its directory — the same two its parent was already
    // wearing — and there was no later message to fix it.
    //
    // Pure, and its own file, for the reason OrbGlyph and the two transcript
    // parsers are: the thing with a right answer is separated from the thing
    // that reads bytes off a disk, so the right answer can be asserted. The
    // precedence below is not invented here either — it mirrors
    // ClaudeBuddyHook.sh's, deliberately, so the orb does not change identity
    // depending on which of the two happened to answer.
    //
    // One deliberate divergence from the hook, and the only one. The hook,
    // finding none of these records in the last 256KB, falls back to grepping
    // the whole file; this does not. That fallback is a multi-megabyte read, and
    // the hook pays it once per tool call where the scan would pay it every two
    // seconds for every session that has no name — which is exactly the set this
    // is asked about. The cost of going without it is no worse than the
    // behaviour this replaces: Claude Code re-emits these records as a session
    // goes, so a long-running session keeps one near the end, and a session long
    // enough to push its only name past 256KB of tool output is not the quiet
    // fork this exists for. When it does happen the orb falls back to its folder
    // name, which is what it did before any of this. Asserted, so the limit is
    // stated rather than discovered — see TranscriptIdentityWindowTests.
    internal readonly record struct TranscriptIdentity(string? Title, string? Color)
    {
        internal static TranscriptIdentity None => new(null, null);

        internal bool IsEmpty => string.IsNullOrEmpty(Title) && string.IsNullOrEmpty(Color);

        // The three record types Claude Code re-emits as a session goes:
        //
        //   {"type":"custom-title","customTitle":"claude-buddy",...}   <- /rename
        //   {"type":"ai-title","aiTitle":"Package app with a tray",...} <- auto-named
        //   {"type":"agent-color","agentColor":"green",...}             <- /color
        //
        // Matched by anchored prefix rather than by parsing every row, which is
        // both the cheap way and the safe one: a transcript is mostly message
        // text, and text inside a message is JSON-escaped, so only a real record
        // can begin this way. Same reasoning, and the same three prefixes, as the
        // hook's grep.
        private const string TitlePrefix = "{\"type\":\"custom-title\"";
        private const string AiTitlePrefix = "{\"type\":\"ai-title\"";
        private const string ColorPrefix = "{\"type\":\"agent-color\"";

        // Newest of each type wins, which is why this walks forward and keeps
        // overwriting rather than stopping at the first hit: /rename and /color
        // append, they do not rewrite, so a transcript holds every name a session
        // has ever had and the last one is the current one.
        //
        // A name set with /rename outranks a generated one regardless of which
        // was written last. Claude Code keeps auto-naming a session after you
        // have named it by hand, so "most recent record" alone would let the
        // generated name win back a name the user chose.
        internal static TranscriptIdentity From(IEnumerable<string> lines)
        {
            string? custom = null, ai = null, color = null;

            foreach (var line in lines)
            {
                if (line is null) continue;

                if (line.StartsWith(TitlePrefix, StringComparison.Ordinal))
                    custom = Value(line, "customTitle") ?? custom;
                else if (line.StartsWith(AiTitlePrefix, StringComparison.Ordinal))
                    ai = Value(line, "aiTitle") ?? ai;
                else if (line.StartsWith(ColorPrefix, StringComparison.Ordinal))
                    color = Letters(Value(line, "agentColor")) ?? color;
            }

            return new TranscriptIdentity(custom ?? ai, color);
        }

        // Parsed rather than pattern-matched, once a row is known to be one of
        // the three. The hook uses sed because it is a shell script and has no
        // parser; this does, and a title is user text — it can hold anything a
        // person can type, including the quotes and backslashes the hook has to
        // strip to protect its own hand-rolled JSON.
        //
        // A row that will not parse is skipped rather than throwing. The format
        // belongs to somebody else, and one malformed row is not a reason to
        // lose a name that a later row may carry anyway.
        private static string? Value(string line, string name)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty(name, out var property)) return null;
                if (property.ValueKind != JsonValueKind.String) return null;

                var text = property.GetString()?.Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Colour names only — letters, nothing else. Same narrowing the hook
        // applies, and for the same reason: this ends up in a lookup of names
        // the app knows how to draw, and anything else is not a colour.
        private static string? Letters(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            var letters = new string(value.Where(char.IsAsciiLetter).ToArray());
            return letters.Length == 0 ? null : letters;
        }
    }
}
