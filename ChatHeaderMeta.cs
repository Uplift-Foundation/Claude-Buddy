namespace ClaudeBuddy
{
    // The bottom of a chat panel's header: which session this is, where it is
    // working, and which machine it is running on.
    //
    // Pure, and separate from the panel, for the same reason OrbGlyph and
    // OrbArrangement are: every one of the rules below is a judgement about
    // what a person should read, and a judgement that can only be checked by
    // opening the app is a judgement nobody checks. The panel does no deciding
    // — it asks this what the header says and draws the answer.
    //
    // Two rows rather than three, and rather than one. Three labelled rows
    // would have made the card taller than the conversation under it, and the
    // facts are read together anyway ("the buddy session, in Source/Claude-Buddy,
    // on the mini") rather than looked up one at a time. One row was the
    // original plan and did not survive being drawn: a panel opens 340 wide,
    // which leaves this column about 180 for three facts that want nearer 400,
    // so the ellipsis ate the machine — the one fact the design says is always
    // shown. The portrait beside these rows is 68 tall and all three text rows
    // come to about 53, so the row the machine now has to itself costs no
    // header height at all. ChatPanel.axaml has the arrangements that were
    // tried in between.
    internal static class ChatHeaderMeta
    {
        // Middle dot with hair spaces around it, the same separator the kind
        // chip already uses for "generating · needs input", so the header reads
        // as one voice rather than two conventions.
        private const string Separator = " · ";

        // What the line says, and what it says when you hover it.
        //
        // Lead and Machine are the two rows the panel draws — the machine has
        // one to itself because at the width a panel opens at, one row cannot
        // hold both legibly (ChatPanel.axaml's own comment has the widths and
        // the three arrangements that were drawn and rejected). Machine is a
        // row of its own rather than a token on the end of one, so it carries
        // no separator: the dot between the session name and the folder is
        // inside Lead, where both of the things it separates are.
        //
        // Text is those two rows read as one string, joined the way they would
        // have been on a single line. It is what the tests assert on and what
        // makes "what does this header say" a question with one answer —
        // Text == Lead, Machine and the separator between them, whenever both
        // are there.
        //
        // Tooltip carries the *untrimmed* facts — in particular the whole
        // working directory rather than the home-relative one — because
        // CharacterEllipsis is what happens to a row too narrow for it and the
        // tooltip is then the only place the path exists.
        internal sealed record Meta(string Text, string Tooltip, bool IsVisible, bool RemoteMachine)
        {
            internal string Lead { get; init; } = "";

            internal string Machine { get; init; } = "";
        }

        // A header that knows nothing: the line collapses rather than drawing an
        // empty row, so a panel with no facts stays exactly as tall as it was
        // before this existed. That is the whole reason IsVisible is part of the
        // answer instead of being inferred from an empty string at the call
        // site.
        internal static readonly Meta Nothing = new("", "", false, false);

        // Which machine to name, and whether it is this one.
        //
        // Kept here rather than in the panel so the comparison is a rule with a
        // test: the interesting case — a far session that happens to report the
        // same name as this machine — cannot be arranged on a real machine at
        // all, and a bool computed inline at the call site would have been
        // asserted by nobody.
        //
        // A session that reports no machine is on this one. That is not a guess:
        // IRemoteChatMachine is implemented only by the mirror, so anything
        // silent about a machine is a local CLI session or a gateway
        // conversation, and both of those are being read on the machine they are
        // being read on.
        internal static (string Name, bool IsLocal) MachineFor(string? reported, string? mine)
        {
            var far = (reported ?? "").Trim();
            var here = (mine ?? "").Trim();

            return far.Length == 0
                ? (here, true)
                : (far, string.Equals(far, here, StringComparison.OrdinalIgnoreCase));
        }

        // The line itself.
        //
        // title is what the header's first line already says, so the session
        // name can be suppressed when it would only repeat it.
        internal static Meta Compose(
            string? title,
            string? sessionName,
            string? cwd,
            string? home,
            string? machine,
            bool isLocalMachine)
        {
            var name = Unrepeated(title, sessionName);
            var shown = HomeRelative(cwd, home);
            var whole = (cwd ?? "").Trim();
            var box = (machine ?? "").Trim();

            // Facts in the order they answer questions: who, where, and — last,
            // because it is the one you check rather than the one you look for —
            // which machine.
            var lead = new List<string>(2);
            if (name.Length > 0) lead.Add(name);
            if (shown.Length > 0) lead.Add(shown);

            if (lead.Count == 0 && box.Length == 0) return Nothing;

            // Joined for Text only. The rows are drawn separately and neither
            // carries the other's separator, so nothing here has to be undone
            // by the panel.
            var leadText = string.Join(Separator, lead);

            var tips = new List<string>(3);
            if (name.Length > 0) tips.Add(name);
            if (whole.Length > 0) tips.Add(whole);
            if (box.Length > 0) tips.Add(box);

            return new Meta(
                string.Join(Separator, new[] { leadText, box }.Where(part => part.Length > 0)),
                string.Join(Separator, tips),
                true,
                box.Length > 0 && !isLocalMachine)
            {
                Lead = leadText,
                Machine = box,
            };
        }

        // The session's own name, unless the line above is already wearing it.
        //
        // A local session's title *is* the header's title most of the time —
        // LocalCliChatSession builds DisplayName straight out of it — so without
        // this the ordinary case would be a panel saying "haunted-mansion" twice,
        // one line under the other. The name only earns its place when something
        // else took the title line: a CLAUDE.md persona, or a room.
        //
        // Compared case-insensitively because a repeat that differs only in case
        // is still a repeat, and trimmed because a title arriving from a hook
        // write has whatever whitespace the writer left on it.
        private static string Unrepeated(string? title, string? sessionName)
        {
            var name = (sessionName ?? "").Trim();
            if (name.Length == 0) return "";

            return string.Equals(name, (title ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                ? ""
                : name;
        }

        // `/Users/warren/Source/Claude-Buddy` → `~/Source/Claude-Buddy`.
        //
        // Trimming from the left instead would have been the obvious way to fit
        // a path into a header and is the wrong one: the leaf is the informative
        // part, and `…/Source/Claude-Buddy` throws away the half a person can
        // reconstruct in order to keep the half they cannot. Collapsing the home
        // prefix removes the part that is the same for every session this user
        // will ever open, which is the only part worth removing at all.
        //
        // The native separator is kept rather than normalised, so a Windows
        // panel reads `~\Source\Claude-Buddy`. A path is a thing a person pastes
        // into their own shell, and rewriting the slashes would make it one they
        // cannot.
        //
        // OrdinalIgnoreCase on both platforms, deliberately. Windows paths are
        // case-insensitive and macOS's default volume is too, but the real
        // reason is that the alternative — asking OperatingSystem which rule to
        // use — would make this function answer differently depending on the
        // machine the test suite runs on, and the Windows path shapes are tested
        // from a Mac.
        internal static string HomeRelative(string? cwd, string? home)
        {
            var path = (cwd ?? "").Trim();
            if (path.Length == 0) return "";

            var root = (home ?? "").Trim().TrimEnd('/', '\\');
            if (root.Length == 0) return path;

            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path;

            var rest = path[root.Length..];
            if (rest.Length == 0) return "~";

            // A prefix match is not a path match: home `/Users/w` against cwd
            // `/Users/warren/Source` starts with it and is not under it, and the
            // answer `~arren/Source` would be a path that does not exist. Only a
            // separator at the boundary makes it a parent.
            return rest[0] is '/' or '\\' ? "~" + rest : path;
        }
    }
}
