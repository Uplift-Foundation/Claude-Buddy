using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ClaudeBuddy
{
    // The persona a local CLI session wears, read from the CLAUDE.md files that
    // are already beside its work.
    //
    // Pure given its inputs: every entry point takes the working directory, the
    // session's source and the user-level config directories rather than asking
    // the machine for them, so the whole of the resolution — which files, in
    // which order, which field wins — is a plain unit test over a temp tree.
    // ResolveForSession is the one wrapper that asks the machine, and it does
    // nothing else.
    //
    // Nearest-first, and that is the interesting half. A CLAUDE.md in the
    // directory you are working in is about *this* project; one three levels up
    // is about the family of projects; the one in ~/.claude is about you. So
    // the first file to name a field wins it, and a name set at the top of a
    // monorepo is a default rather than an override — the same way Claude Code
    // itself layers them.
    internal static class LocalPersona
    {
        // AvatarSource and AvatarPath are two different files and both are
        // needed. AvatarSource is the markdown that *named* the picture, which
        // is what says whose picture it is and which directory a relative path
        // was resolved against; AvatarPath is the picture itself. The scan
        // watches both, because they move independently: a portrait replaced
        // in place — same filename, new bytes, CLAUDE.md untouched — changes
        // nothing about any markdown file, and watching the markdown alone
        // left the old face on the orb until something else in the tree
        // happened to move or the app was restarted.
        // Deliberately no `byte[] Avatar` field, and that is CB-135's change
        // rather than an omission. This registry holds one Persona per session
        // for the life of the session, and two sessions in one repository are
        // two entries by design — so a retained picture is one copy of the
        // source bytes *per agent*, on a machine that routinely runs twenty or
        // thirty of them, all of the same file. What an orb actually draws is
        // a 144 px frame out of OpenClawAvatars' cache; the source bytes were
        // only ever the thing those were decoded from, and AvatarPath is
        // enough to decode them again. See OpenClawAvatars.ForFile, which does.
        //
        // Nulling the field after the first decode was considered and
        // rejected: a byte[] that is populated sometimes is a worse record to
        // reason about than one that does not exist, and every caller would
        // have had to know which half of the session's life it was in.
        internal sealed record Persona(
            string? Name,
            string? Voice,
            double? Rate,
            string? AvatarSource,
            string? AvatarPath,
            IReadOnlyList<string> Files)
        {
            internal bool IsEmpty => Name is null && Voice is null && AvatarPath is null;

            // The files this persona's answer depends on: what was read, plus
            // the picture if there is one. Here rather than at the call site
            // because forgetting the picture is exactly the bug this exists to
            // have fixed, and a caller that assembles the set by hand is a
            // caller that can forget it again.
            internal IEnumerable<string> Watched =>
                AvatarPath is null ? Files : Files.Append(AvatarPath);
        }

        internal static readonly Persona Empty =
            new(null, null, null, null, null, Array.Empty<string>());

        // How far up the tree to look. Not a security bound — the walk
        // terminates at the root on its own — but a bound on what the scan
        // costs: these paths are stat'd every two seconds, four per level, and
        // a working directory forty levels deep is already somebody's build
        // artifact rather than somebody's project.
        private const int MaxDepth = 40;

        // What an import may reach, and how much of it. A CLAUDE.md that
        // imports a file that imports a file is ordinary; one five deep is a
        // structure, and twenty files in is more markdown than any persona
        // needs to state a name.
        private const int MaxImportHops = 5;
        private const int MaxFiles = 20;

        // The files a session's persona could be written in, nearest first, as
        // paths — no IO at all, so the caller can stat them for a signature
        // before deciding whether anything is worth reading.
        //
        // The four names per directory are Claude Code's own set: CLAUDE.md and
        // its untracked CLAUDE.local.md sibling, the .claude/CLAUDE.md a
        // project keeps out of its root, and AGENTS.md — which this repository
        // itself keeps beside CLAUDE.md for the CLI that does not read the
        // other one.
        //
        // The user-level file is asked only for Claude Code. Codex and Grok
        // have their own config directories with their own layouts, and
        // guessing that ~/.claude describes a Codex session would be a persona
        // taken from the wrong CLI — the ticket calls that a non-goal, and a
        // wrong name is worse than no name. OpenClaw and RemoteControl have no
        // local directory to walk in the first place: the first is a
        // conversation on a gateway, and the second is a session whose cwd is
        // on somebody else's machine, where a path that happens to exist here
        // too would be a different directory wearing the same string.
        internal static IReadOnlyList<string> CandidateFiles(
            string? cwd, IEnumerable<string> userConfigDirs, SessionSource source)
        {
            var files = new List<string>();
            if (source is SessionSource.OpenClaw or SessionSource.RemoteControl) return files;

            // OrdinalIgnoreCase for the reason BackgroundJobs.ExtraAccountDirs
            // is: Windows paths are, and one file reached under two
            // capitalizations is one file whose second reading cannot say
            // anything the first did not.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string path)
            {
                if (seen.Add(path)) files.Add(path);
            }

            var directory = FullPathOrNull(cwd);
            for (var depth = 0; directory is not null && depth < MaxDepth; depth++)
            {
                Add(Path.Combine(directory, "CLAUDE.md"));
                Add(Path.Combine(directory, "CLAUDE.local.md"));
                Add(Path.Combine(directory, ".claude", "CLAUDE.md"));
                Add(Path.Combine(directory, "AGENTS.md"));
                directory = Path.GetDirectoryName(directory);
            }

            if (source != SessionSource.ClaudeCode) return files;

            foreach (var configDir in userConfigDirs)
            {
                if (string.IsNullOrWhiteSpace(configDir)) continue;
                Add(Path.Combine(configDir.Trim(), "CLAUDE.md"));
            }

            return files;
        }

        private static string? FullPathOrNull(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
            catch (ArgumentException) { return null; }
            // Windows refuses a path past its own length limit, and an import
            // token is only as short as the line it was written on. Not
            // reachable from a test that has to pass on both runners — macOS
            // has no such limit and returns the path — so it is named in the
            // PR body rather than covered.
            catch (IOException) { return null; }
        }

        // The candidates that exist, in order, each with its lines — plus
        // whatever they import.
        //
        // Claude Code's `@path.md` import is part of how people write these:
        // the persona is often in a file the CLAUDE.md pulls in rather than in
        // the CLAUDE.md itself, and refusing to follow one would mean reading
        // the half of the document that says the least. A file's own lines are
        // read before the files it imports, so an import is a default the
        // importing file may override — the same rule the directory walk
        // already uses, one level down.
        //
        // The visited set is keyed on the canonical path, so a cycle (two files
        // importing each other, or two paths reaching one file through a link)
        // is read once and not twice.
        internal static IReadOnlyList<(string Path, string[] Lines)> Load(IReadOnlyList<string> candidates)
        {
            var read = new List<(string Path, string[] Lines)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (read.Count >= MaxFiles) break;
                ReadInto(candidate, 0, read, visited);
            }

            return read;
        }

        private static void ReadInto(
            string path, int hop, List<(string Path, string[] Lines)> read, HashSet<string> visited)
        {
            if (read.Count >= MaxFiles) return;

            var canonical = PersonaFiles.CanonicalFile(path);
            if (canonical is null || !visited.Add(canonical)) return;

            var lines = PersonaFiles.ReadMarkdown(canonical);
            if (lines is null) return;

            read.Add((canonical, lines));
            if (hop >= MaxImportHops) return;

            // Never null: canonical is a FileInfo.FullName for a file that
            // exists, so it is absolute and has a parent.
            foreach (var import in ImportsIn(lines, Path.GetDirectoryName(canonical)!))
                ReadInto(import, hop + 1, read, visited);
        }

        // An import is a bare `@something.md` token: either the whole line or a
        // word in it. Bare deliberately — `@notes.md.` with a full stop after
        // it is a sentence mentioning the file, and a sentence mentioning a
        // file is not an instruction to read it.
        //
        // Resolved against the importing file's own directory, which is what
        // makes a `docs/persona.md` beside a CLAUDE.md mean the obvious thing.
        // Path.Combine already lets an absolute import be itself, so there is
        // no separate arm for one.
        private static IEnumerable<string> ImportsIn(string[] lines, string directory)
        {
            foreach (var line in lines)
            {
                foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length <= 1 || token[0] != '@') continue;

                    var relative = token[1..];
                    if (!relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;

                    var combined = FullPathOrNull(Path.Combine(directory, relative));
                    if (combined is not null) yield return combined;
                }
            }
        }

        // The fold. First file to state a field owns it, and the picture is
        // resolved against the directory of the file that named it — not
        // against the session's cwd, so a `portrait.png` written in
        // ~/.claude/CLAUDE.md means the one in ~/.claude and cannot be
        // shadowed by a file of that name in whatever directory the session
        // happens to be sitting in.
        internal static Persona Resolve(string? cwd, SessionSource source, IEnumerable<string> userConfigDirs)
        {
            var candidates = CandidateFiles(cwd, userConfigDirs, source);
            if (candidates.Count == 0) return Empty;

            string? name = null;
            string? voice = null;
            double? rate = null;
            string? avatarSource = null;
            string? avatarPath = null;
            var files = new List<string>();

            foreach (var (path, lines) in Load(candidates))
            {
                files.Add(path);

                var fields = PersonaMarkdown.Parse(lines);
                name ??= fields.Name;
                voice ??= fields.Voice;
                rate ??= fields.Rate;

                if (avatarPath is not null || fields.Avatar is null) continue;

                // The directory of the file that named the picture, taken from
                // the canonical path Load already resolved — never null, for
                // the reason ReadInto's own call says. Not re-canonicalised:
                // if that directory is itself a link, the picture beside the
                // markdown is on the other end of the same link the markdown
                // was, so resolving it could only refuse a file we have
                // already read from, never admit one from anywhere new.
                var root = Path.GetDirectoryName(path)!;

                // The path only. The bytes are read to prove the file opens
                // and then dropped — see PersonaFiles.AvatarPathAt for why
                // that read is not skipped.
                var picture = PersonaFiles.AvatarPathAt(root, fields.Avatar);
                if (picture is null) continue;

                avatarSource = path;
                avatarPath = picture;
            }

            return new Persona(name, voice, rate, avatarSource, avatarPath, files);
        }

        // What the scan compares to decide whether anything is worth re-reading:
        // for each path, whether it exists, how long it is and when it was last
        // written. Stats only — no file is opened — because this runs on the UI
        // thread every two seconds for every local session, and reading a
        // handful of markdown files at that cadence is a cost with nothing to
        // show for it.
        //
        // Takes paths rather than a session so the caller decides which set. A
        // candidate list alone will not notice an edit to an *imported* file,
        // which is not a candidate; a caller that wants it to should pass the
        // previous Persona.Files alongside, which is what that list is for.
        internal static string Signature(IEnumerable<string> paths)
        {
            // ASCII's own separators rather than a printable delimiter: a path
            // on macOS or Linux may legally contain a newline or a pipe, and a
            // signature two different file sets can both produce is not a
            // signature.
            const char Unit = '\u001f';
            const char Record = '\u001e';

            var signature = new StringBuilder();

            foreach (var path in paths)
            {
                signature.Append(path).Append(Unit);

                // One catch, and only around the constructor: FileInfo caches
                // its stat the first time anything asks, so Length and
                // LastWriteTimeUtc read that cache rather than the disk once
                // Exists has been answered — neither can throw for a file that
                // has gone away in between. What is left is a path the
                // constructor itself refuses.
                try
                {
                    var file = new FileInfo(path);
                    if (file.Exists) signature.Append(file.Length).Append('@').Append(file.LastWriteTimeUtc.Ticks);
                    else signature.Append('-');
                }
                catch (ArgumentException) { signature.Append('?'); }

                signature.Append(Record);
            }

            return signature.ToString();
        }

        // The production wrapper, and the only thing here that asks the machine
        // anything.
        internal static Persona ResolveForSession(SessionStatus status) =>
            status.IsLocalCli && !string.IsNullOrWhiteSpace(status.Cwd)
                ? Resolve(status.Cwd, status.Source, UserConfigDirs())
                : Empty;

        // The Claude Code config directories this app knows about, in the order
        // they should be asked.
        //
        // CLAUDE_CONFIG_DIR first when this process has one, because a Claude
        // Code launched under that variable *is* running out of that directory
        // and its own CLAUDE.md is the user-level file for it. Then
        // ClaudeConfigRoots, which is already the answer to "every config
        // directory on this machine" — ~/.claude plus the configured extras —
        // and exists precisely so nothing new builds that list a fourth time.
        internal static IReadOnlyList<string> UserConfigDirs(string? configDirEnv = null, string? home = null)
        {
            configDirEnv ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

            var dirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(configDirEnv)) dirs.Add(configDirEnv.Trim());

            foreach (var root in ClaudeConfigRoots.All(home))
            {
                if (!dirs.Contains(root, StringComparer.OrdinalIgnoreCase)) dirs.Add(root);
            }

            return dirs;
        }

        // Agent > persona > title > folder.
        //
        // Pure, and here rather than in OrbWindow because "what is this orb
        // called" is a rule with a right answer and OrbWindow is a place you
        // have to look at to check one — the same argument that moved the orb's
        // initials into OrbGlyph after a year of them being wrong.
        //
        // The persona sits below the agent name and above the title on
        // purpose. An agent name is what a team member is called *right now*
        // and changes per session; a persona is what the project says its
        // assistant is called, which is more specific than the chat's
        // generated title but less specific than a role someone assigned. The
        // title is not lost either way — the orb's tooltip still says it.
        internal static string OrbLabel(string? agent, string? personaName, string? title, string? folder)
        {
            if (!string.IsNullOrEmpty(agent)) return agent;
            if (!string.IsNullOrEmpty(personaName)) return personaName;
            if (!string.IsNullOrEmpty(title)) return title;
            return folder ?? "";
        }
    }

    // Which persona belongs to which live session, for the drawing code to ask.
    //
    // Mirrors OpenClawSessions' identity registry deliberately, down to the
    // lock and the test seam: the scan writes, everything that draws reads, and
    // an orb that wants to know who it is asks one question of one place
    // whether the session is a gateway conversation or a terminal.
    internal static class LocalPersonas
    {
        private static readonly object Gate = new();

        private static readonly Dictionary<string, LocalPersona.Persona> Registry =
            new(StringComparer.OrdinalIgnoreCase);

        // The key an avatar is cached under. Prefixed rather than bare, because
        // OpenClawAvatars' cache is shared with the gateway's agents and a
        // session id and an agent id are two namespaces that must not collide.
        internal static string AvatarKey(string sessionId) => "local:" + sessionId;

        // Setting a *different* persona object drops any bitmap decoded from
        // the old one. Identity rather than content: the scan keeps one persona
        // object per session and hands the same one back until something it
        // watches moves, so this costs a dictionary removal per change and
        // nothing per tick.
        //
        // That makes this half of a two-part invariant, and the other half is
        // the scan's. A picture is only re-decoded here if the scan hands over
        // a new object, and it only does that if it noticed the picture change
        // — which for a portrait overwritten in place, with the markdown left
        // alone, means it has to be watching the picture file itself. It is:
        // see Persona.Watched and SessionManager.ApplyPersona. Neither half is
        // sufficient alone, and the version of this comment that claimed an
        // edited picture was re-decoded said so while the scan was still
        // watching only the markdown.
        internal static void Set(string sessionId, LocalPersona.Persona persona)
        {
            lock (Gate)
            {
                if (Registry.TryGetValue(sessionId, out var existing) && ReferenceEquals(existing, persona)) return;
                Registry[sessionId] = persona;
            }

            OpenClawAvatars.Forget(AvatarKey(sessionId));
        }

        internal static void Forget(string sessionId)
        {
            lock (Gate) Registry.Remove(sessionId);
            OpenClawAvatars.Forget(AvatarKey(sessionId));
        }

        internal static LocalPersona.Persona? For(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            lock (Gate) return Registry.GetValueOrDefault(sessionId);
        }

        // A test seam, matching OpenClawSessions.SetIdentitiesForTests: the only
        // thing that fills this table in production is SessionManager's scan,
        // which needs a status file on disk and a two-second timer. Without
        // this, everything that draws a local session's name or picture is
        // unreachable for a reason that has nothing to do with the code.
        internal static void SetForTests(IReadOnlyDictionary<string, LocalPersona.Persona> personas)
        {
            lock (Gate)
            {
                foreach (var id in Registry.Keys.ToList()) OpenClawAvatars.Forget(AvatarKey(id));
                Registry.Clear();
                foreach (var (id, persona) in personas) Registry[id] = persona;
            }

            foreach (var id in personas.Keys) OpenClawAvatars.Forget(AvatarKey(id));
        }

        // The persona's voice resolved over the options actually available.
        // Null when the session has no persona, when its persona names no
        // voice, or when the name it does give matches nothing here — all three
        // meaning "use the user's own voice", which is what the caller does
        // with a null.
        internal static TextToSpeech.VoiceOption? VoiceForSession(
            string? sessionId, IEnumerable<TextToSpeech.VoiceOption> options) =>
            TextToSpeech.MatchVoiceOption(For(sessionId)?.Voice, options);

        // Excluded from coverage: AllVoiceOptions asks the neural engine to
        // enumerate itself and runs the user's listing command, which is two
        // process launches on the machine running the tests. The decision this
        // wraps — which option a persona's voice resolves to, and what happens
        // when it resolves to none — is the overload above, and it is tested.
        [ExcludeFromCodeCoverage]
        internal static TextToSpeech.VoiceOption? VoiceForSession(string? sessionId) =>
            VoiceForSession(sessionId, TextToSpeech.AllVoiceOptions());

        // Only the neural (Kokoro) engine has a speaking rate to set, so a rate
        // resolved for a system voice or a custom command is simply never read
        // — same as OpenClawSessions.RateForSession, and kept separate from the
        // voice for the same reason: VoiceOption is also the shape of every row
        // in the settings picker, where a per-utterance rate means nothing.
        internal static double? RateForSession(string? sessionId) => For(sessionId)?.Rate;
    }
}
