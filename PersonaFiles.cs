using System.Globalization;

namespace ClaudeBuddy
{
    // Every read a persona makes of the disk, and the proofs that go with it.
    //
    // Shared with OpenClawWorkspaceIdentity for the same reason the grammar is:
    // these are the security half of the feature, and a second copy of them is
    // a second place for one of them to be quietly dropped. Nothing here is new
    // — it is OpenClawWorkspaceIdentity's own file handling, moved rather than
    // rewritten, so the guarantee it already made ("every path accepted here is
    // proved to remain inside its canonical root") is the same guarantee and
    // not a restatement of it.
    //
    // What *is* new is ReadMarkdown's size cap. A workspace read a directory
    // listing of .md files somebody deliberately put there; a local session
    // reads whatever CLAUDE.md happens to sit above its working directory,
    // which is a much less deliberate set of files.
    internal static class PersonaFiles
    {
        // A CLAUDE.md is prose someone wrote. A quarter of a megabyte of it is
        // already several times the largest one in this repository, and a file
        // past that is generated, concatenated or not what we think it is —
        // none of which is worth stalling a two-second scan over.
        internal const long MaxMarkdownBytes = 256 * 1024;

        // Eight mebibytes, raised from two by CB-135 — and the raise was
        // conditional on the change directly below it, not a number picked on
        // its own.
        //
        // Two was quietly load-bearing. `Persona.Avatar` used to hold the raw
        // file bytes, `LocalPersonas` holds a Persona per session for the
        // session's life, and two sessions in one repository are two entries by
        // design — so N agents in one checkout held N copies of the same
        // portrait's *source* bytes, and this cap was the only thing bounding
        // that. On a machine that routinely runs twenty or thirty agents, a
        // four-fold raise would have been hundreds of megabytes resident for
        // nothing.
        //
        // That resident-bytes argument is gone, and has been since CB-135
        // itself: a local persona carries AvatarPath rather than a byte array,
        // and the decode reads the file itself (ReadAvatarFile below, through
        // OpenClawAvatars.ForFile) rather than being handed bytes that outlive
        // the read. What this cap bounds today is a *transient* read — one
        // buffer, alive for the length of a single resolve, freed the moment
        // AvatarAt returns — not a buffer that sits in `LocalPersonas` for a
        // session's whole life. 2 MiB was a perfectly ordinary size for a
        // portrait exported from a phone, and this repository's own `cto.png`
        // cleared it by 57 KB, but the number that actually bounds anything
        // resident is a different one entirely, below.
        //
        // CB-140 measured a real animated persona against this cap and left it
        // where it was, and the reasoning was wrong for this constant even
        // though the arithmetic in it was correct. The still was 1,363,624
        // bytes — comfortably under — and the animated GIF was 8,391,801,
        // exactly 3,193 bytes over; CB-140 computed that GIF's *resident* cost
        // if admitted (64 frames × 144×144×4 = 5,308,416 bytes per session,
        // sixty-four times the still) and declined to raise this cap on the
        // strength of that number. But `MaxAvatarBytes` does not gate resident
        // cost any more — see the paragraph above — so that computation was an
        // argument about a different budget, wearing this constant's name.
        // Raising *this* cap changes nothing about what stays resident: a
        // 9 MiB 64-frame GIF costs exactly what an 8 MiB 64-frame GIF costs,
        // because the resident cost is `frameCount × 144 × 144 × 4` bytes, a
        // function of frame count rather than file size. What actually bounds
        // it is `DecodeFrames`' own 120-frame ceiling in OpenClawAvatars.cs,
        // untouched by this change and doing the whole of that job on its own.
        //
        // CB-146 is what that conflation cost: the persona from CB-140's own
        // measurement — 8,391,801 bytes, 3,193 over the old cap, a 0.04%
        // overshoot — named no still at all, so refusing its animation left it
        // with no avatar whatsoever rather than a lesser one. Sixteen mebibytes
        // is not "shave the cap down to fit the one file that failed" — a cap
        // set 3 KB above today's failure is a cap that fails again on the next
        // GIF anyone exports — it is a plain doubling, and a single 16 MiB read
        // during a scan that already reads markdown files up a directory tree
        // is not a cost worth defending against, since nothing stays resident
        // from it once the read returns.
        internal const long MaxAvatarBytes = 16 * 1024 * 1024;

        // Why a picture named in markdown was not drawn. Categories rather
        // than a message per site, because the useful question when reading
        // persona.log is which *kind* of thing went wrong: escaping the root —
        // by `..`, by a symlink, or simply by naming a directory that is not
        // it — is somebody writing a path this app will not follow, an
        // oversized file is somebody's camera, and unreadable is the
        // filesystem.
        //
        // NotAPicturePath is CB-139's NotRelativePath, renamed by CB-140 for
        // the reason its own message changed: it used to mean "not a *relative*
        // path", and an absolute one is legal now, provided it stays inside the
        // root — see AvatarAt. The category is otherwise the same, and it was
        // added because the absence of it was itself a defect. A `data:` URI, a
        // URL and a value the grammar could not read as a path all used to
        // arrive here as an empty avatar and were reported — when they were
        // reported at all — as "unreadable — it is missing", which sends
        // somebody looking on disk for a file they never wrote. Twenty-four
        // lines on one Mac mini said that, and not one of them was about a
        // missing file. A wrong reason is worse than no reason, because it is
        // actionable and the action is wasted.
        //
        // Rooted is gone rather than kept around as a second name for the same
        // thing: CB-140 removed the string-level "does this begin with a
        // separator" refusal from both PersonaMarkdown and here, in favour of
        // the containment check (IsWithin) the filesystem already had to run.
        // A caller cannot reach the old category any more, from either seam.
        internal enum AvatarRejection { EscapesRoot, TooLarge, Unreadable, NotAPicturePath }

        // The lines of a markdown file, or null for every reason there might
        // not be any: it does not exist, it is too big to be one, or this
        // process cannot read it. All three mean the same thing to every
        // caller — there is no persona here — so none of them is an error.
        internal static string[]? ReadMarkdown(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > MaxMarkdownBytes) return null;
                return File.ReadAllLines(file.FullName);
            }
            // The file being unreadable through the filesystem's own permissions
            // is the common one and is covered; a path the constructor refuses
            // outright is next. The IOException arm is for a file that goes away
            // between the stat and the read, which no test on either runner can
            // arrange, and it is named in the PR body rather than pretended
            // about.
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
        }

        internal static byte[]? AvatarAt(string root, string? avatar) => AvatarAt(root, avatar, out _);

        // The two entry points the resolvers actually use, and the reason they
        // take the whole parsed Fields rather than the one string they need
        // from it.
        //
        // A picture that was *named* and could not be read as a path has to be
        // written down, and by the time the resolver holds `fields.Avatar` that
        // fact is gone: null there means both "no picture in this file" and
        // "somebody wrote a data: URI after Profile picture:". Passing Fields
        // is what keeps the two apart at the only place that can tell the
        // difference — and passing it rather than asking each caller to check
        // is what stops the check being dropped at one of the two call sites,
        // which is the shape of every drift bug this feature has already had.
        //
        // Silent when nothing was named: a field nobody filled in is not a
        // refusal, and a log that says so once per markdown file up a directory
        // tree is a log nobody reads.
        internal static byte[]? AvatarAt(string root, PersonaMarkdown.Fields fields)
        {
            if (fields.Avatar is null) { RejectUnusableValue(fields); return null; }
            return AvatarAt(root, fields.Avatar);
        }

        internal static string? AvatarPathAt(string root, PersonaMarkdown.Fields fields)
        {
            if (fields.Avatar is null) { RejectUnusableValue(fields); return null; }
            return AvatarPathAt(root, fields.Avatar);
        }

        // Note what this deliberately does *not* cover: a value that normalises
        // to a perfectly good relative path naming a file that is not there
        // still comes out of AvatarAt as Unreadable, because that is the honest
        // answer and it is the one somebody can act on. The new category is for
        // a value that was never a path at all.
        private static void RejectUnusableValue(PersonaMarkdown.Fields fields)
        {
            if (fields.RawAvatar is not null)
                Reject(AvatarRejection.NotAPicturePath, fields.RawAvatar);
        }

        // The line persona.log gets. Pure and separate from the writing of it,
        // so what it says can be asserted without a filesystem — and it is
        // worth asserting, because a log line nobody can read the meaning of
        // is the same as no log line.
        //
        // The cap is in every message, not only in the too-large one. Somebody
        // reading this file is deciding what to do about their picture, and
        // "how big is it allowed to be" is the question they have next
        // whichever refusal they hit.
        // How much of the offending value the line is allowed to quote.
        //
        // Not a style choice — a real one, found by reading this log on a
        // machine that had been running the build for half an hour. The value
        // is whatever somebody wrote after a picture label, and the explicit
        // bullet grammar happily accepts a `data:` URI: one line in that log
        // was a five-kilobyte base64 WebP, quoted in full, against a 64 KiB
        // ceiling for the whole file. A handful of those and the log is spent
        // on one embedded image. A hundred and twenty characters is enough to
        // recognise any real path and enough of a `data:` URI to see what it
        // is.
        private const int MaxQuotedValue = 120;

        // Cut from the middle, not the end, and that correction came from the
        // tests rather than from taste: a picture resolved out of a temp tree
        // has a long directory in front of it and the *filename* on the end,
        // so trimming the tail throws away the one part a reader recognises.
        // The head says which tree, the tail says which file, and the length
        // tells a truncated monster from a path that is merely long.
        private const int QuotedHead = 70;
        private const int QuotedTail = 40;

        internal static string Quoted(string picture) =>
            picture.Length <= MaxQuotedValue
                ? picture
                : picture[..QuotedHead] + "…" + picture[^QuotedTail..]
                    + " (" + picture.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters)";

        // bothRootsSearched defaults to false so every existing call site and
        // every existing asserted string in the test suite is byte-identical
        // (CB-147, D6) — this only ever reads true when AvatarAt has actually
        // run the guard chain against two distinct roots and both refused the
        // value, which is the one case where naming just one of them would be
        // telling half the story. TooLarge and NotAPicturePath do not vary on
        // it: TooLarge already names the file that was found, and
        // NotAPicturePath is decided before any root is tried at all.
        internal static string RejectionMessage(
            AvatarRejection reason, string picture, long bytes, bool bothRootsSearched = false)
        {
            var detail = reason switch
            {
                AvatarRejection.TooLarge =>
                    "too large — " + bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes",
                AvatarRejection.EscapesRoot => bothRootsSearched
                    ? "escapes root — it resolves outside both the directory of the markdown that named it "
                        + "and the workspace"
                    : "escapes root — it resolves outside the directory of the markdown that named it",
                AvatarRejection.NotAPicturePath =>
                    "not a picture path — a persona picture is a relative or absolute path ending in "
                    + ".png, .jpg, .jpeg, .gif or .webp, never a URL or a data: URI",
                _ => bothRootsSearched
                    ? "unreadable — it is missing under both the directory of the markdown that named it "
                        + "and the workspace, empty, or this process may not open it"
                    : "unreadable — it is missing, empty, or this process may not open it",
            };

            return "persona picture ignored: \"" + Quoted(picture) + "\" (" + detail + "); cap is "
                + MaxAvatarBytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
        }

        private static void Reject(
            AvatarRejection reason, string picture, long bytes = 0, bool bothRootsSearched = false) =>
            PersonaLog.Record(RejectionMessage(reason, picture, bytes, bothRootsSearched));

        // Where a picture named in markdown actually lives, with every guard
        // applied and no bytes kept.
        //
        // The read still happens — AvatarAt opens the file, because a picture
        // that passes every check and then fails to open must not leave a path
        // behind for the next scan to watch — and the bytes are dropped on the
        // way out. That is the whole of what "the cap bounds a transient read"
        // means: one buffer, alive for the length of a resolve, rather than one
        // buffer per session for the life of the session.
        //
        // Deliberately not a second copy of the resolution. Recomputing the
        // guards here to avoid the read would be two implementations of the
        // security half of this feature, agreeing only until one of them
        // changed.
        internal static string? AvatarPathAt(string root, string? avatar)
        {
            AvatarAt(root, avatar, out var path);
            return path;
        }

        // The CB-147 counterparts, taking a second candidate root: the
        // session's workspace root, alongside the directory of the markdown
        // that named the picture. Everything said about the single-root
        // overloads above applies unchanged to whichever root actually
        // resolves it — these exist so LocalPersona.ResolveFrom can offer a
        // workspace-relative convention without a second implementation of
        // the resolution.
        internal static string? AvatarPathAt(string fileDirectory, string? workspaceRoot, PersonaMarkdown.Fields fields)
        {
            if (fields.Avatar is null) { RejectUnusableValue(fields); return null; }
            return AvatarPathAt(fileDirectory, workspaceRoot, fields.Avatar);
        }

        internal static string? AvatarPathAt(string fileDirectory, string? workspaceRoot, string? avatar)
        {
            AvatarAt(fileDirectory, workspaceRoot, avatar, out var path);
            return path;
        }

        // The bytes again, at decode time, for a path AvatarPathAt already
        // proved.
        //
        // The proof it cannot repeat is containment: the root that path was
        // measured against is the directory of the markdown that named it, and
        // what is carried forward is the canonical file it resolved to. So the
        // guard here is that the path must *still be its own canonical form* —
        // if the file has since been replaced by a symlink pointing somewhere
        // else, CanonicalFile answers with that somewhere else, the two stop
        // matching, and the read is refused rather than following it. A
        // portrait swapped for another real file at the same path is read, and
        // should be: that is a user replacing their picture, which the scan
        // already watches for.
        internal static byte[]? ReadAvatarFile(string path)
        {
            try
            {
                var canonical = CanonicalFile(path);
                if (canonical is null)
                {
                    Reject(AvatarRejection.Unreadable, path);
                    return null;
                }

                if (!string.Equals(canonical, Trim(path), StringComparison.Ordinal))
                {
                    Reject(AvatarRejection.EscapesRoot, path);
                    return null;
                }

                var info = new FileInfo(canonical);
                if (info.Length <= 0)
                {
                    Reject(AvatarRejection.Unreadable, path);
                    return null;
                }

                if (info.Length > MaxAvatarBytes)
                {
                    Reject(AvatarRejection.TooLarge, path, info.Length);
                    return null;
                }

                return File.ReadAllBytes(canonical);
            }
            // Two arms rather than the four AvatarAt keeps, because two of
            // those cannot arise here: the path has already been through
            // GetFullPath and a FileInfo by the time this runs, so a string
            // the constructor refuses outright has been refused already.
            // UnauthorizedAccessException is the file's own permissions and is
            // covered; IOException is the file going away between the stat and
            // the read, which no test on either runner can arrange, and it is
            // named in the PR body rather than pretended about.
            catch (IOException) { Reject(AvatarRejection.Unreadable, path); return null; }
            catch (UnauthorizedAccessException) { Reject(AvatarRejection.Unreadable, path); return null; }
        }

        // The bytes, and where they were actually read from.
        //
        // The path is an output rather than something the caller can work out
        // for itself, and that is the point: the string in the markdown is
        // relative, may run through a subdirectory, and is only accepted after
        // being canonicalised and proved to stay inside its root. Recomputing
        // it outside this function would be a second copy of that resolution,
        // and a second copy that agreed with this one only until one of them
        // changed. The one caller that wants it wants it in order to *stat*
        // the file again on the next scan, so it has to be the same file this
        // read, not a path that resolves to it today.
        internal static byte[]? AvatarAt(string root, string? avatar, out string? path) =>
            AvatarAt(root, null, avatar, out path);

        // CB-147: a picture may be named relative to two different places —
        // the directory of the markdown file that named it, or the session's
        // workspace root — and both conventions are real, so both are tried.
        //
        // D1/D2: fileDirectory is always tried first and, if it resolves to
        // an existing, canonical, contained, non-empty, under-cap file, wins
        // outright — workspaceRoot is not even consulted. That is not a
        // tie-break of convenience: it is what keeps a user-level picture
        // from being shadowable by a same-named file in whatever repository
        // a session happens to have open (see LocalPersona.Resolve's own
        // comment). D3: each root gets the whole guard chain run against
        // itself alone, in TryAvatarAt below — there is no "contained in
        // root1 union root2" check anywhere, and a value that escapes both
        // roots is refused exactly as it was refused under one. D4: an
        // absolute avatar value resolves to the same file under either root
        // (Path.Combine returns a rooted second argument unchanged) and
        // differs only in which root's containment it passes, so it is
        // accepted if it is contained in *either* — a deliberate widening
        // from today's single-root behaviour, named in the PR body rather
        // than hidden in this diff.
        internal static byte[]? AvatarAt(
            string fileDirectory, string? workspaceRoot, string? avatar, out string? path)
        {
            path = null;

            // A blank field is a field nobody filled in, and is not a
            // rejection worth writing down.
            if (string.IsNullOrWhiteSpace(avatar)) return null;

            var roots = CandidateRoots(fileDirectory, workspaceRoot);

            var haveRejection = false;
            var bestRejection = AvatarRejection.Unreadable;
            var bestLength = 0L;

            foreach (var root in roots)
            {
                var bytes = TryAvatarAt(root, avatar, out var candidatePath, out var rejection, out var length);
                if (bytes is not null)
                {
                    path = candidatePath;
                    return bytes;
                }

                // D5: one rejection logged per resolve, never two. Rank by
                // how much the value was proved about — Unreadable (nothing
                // was there) < EscapesRoot (something real was found outside)
                // < TooLarge (the file was found and measured) — and report
                // whichever root got strictly further. Ties go to the first,
                // narrower root, which is what leaving `>` rather than `>=`
                // below does.
                if (!haveRejection || Rank(rejection) > Rank(bestRejection))
                {
                    bestRejection = rejection;
                    bestLength = length;
                    haveRejection = true;
                }
            }

            // bothRootsSearched is true only when there were genuinely two
            // distinct roots to search (CandidateRoots already deduped a
            // workspace root that equals fileDirectory down to one) — see D6.
            Reject(bestRejection, avatar, bestLength, bothRootsSearched: roots.Count > 1);
            return null;
        }

        // D8: the candidate roots for a resolve, deduplicated so the
        // overwhelmingly common case — a workspace root that is itself the
        // directory of the markdown file, e.g. a CLAUDE.md sitting in the
        // session's cwd — tries once rather than twice. A workspace root of
        // null (no cwd, or one that does not canonicalise — see
        // PersonaFiles.CanonicalDirectory) degrades to exactly one candidate
        // root and therefore byte-identical behaviour to before this ticket.
        // Ordinal on the trimmed strings, consistent with IsWithin and Trim,
        // since those are the same strings this is comparing.
        internal static IReadOnlyList<string> CandidateRoots(string fileDirectory, string? workspaceRoot) =>
            workspaceRoot is null || string.Equals(Trim(fileDirectory), Trim(workspaceRoot), StringComparison.Ordinal)
                ? new[] { fileDirectory }
                : new[] { fileDirectory, workspaceRoot };

        // D5's ranking, as a number: how far a rejection got toward finding
        // the file. NotAPicturePath cannot arise here — it is decided in
        // RejectUnusableValue, before any root is tried at all — so it has no
        // meaningful rank; its arm exists to keep the switch exhaustive and
        // has to rank below every real outcome so it can never win the "got
        // furthest" comparison in AvatarAt, if some future caller ever did
        // manage to hand it in.
        //
        // Internal rather than private so a unit test can assert the ranking
        // contract directly, the same reason SessionManager.Superseded and
        // InheritTerminalInfo are internal — a decision worth a case per
        // outcome is a seam to open up, not a reason to test it only through
        // whatever real files happen to reach it.
        internal static int Rank(AvatarRejection reason) => reason switch
        {
            AvatarRejection.Unreadable => 0,
            AvatarRejection.EscapesRoot => 1,
            AvatarRejection.TooLarge => 2,
            _ => -1,
        };

        // The guard chain against exactly one root — this is the whole of
        // what AvatarAt used to do directly, before CB-147 needed to run it
        // more than once per resolve. Returns its outcome rather than logging
        // it: a naive per-root loop that logged from in here would write one
        // line per failing root, which is the "which root failed, twice"
        // shape D5 exists to rule out. AvatarAt is the only caller, and it is
        // the only place that logs.
        private static byte[]? TryAvatarAt(
            string root, string avatar, out string? path, out AvatarRejection rejection, out long length)
        {
            path = null;
            rejection = AvatarRejection.Unreadable;
            length = 0;

            try
            {
                var combined = Path.GetFullPath(Path.Combine(root, avatar));

                // The one check that decides whether an absolute value is
                // legal, and the only one that ever should have: `Combine`
                // returns a rooted second argument unchanged, so a relative
                // path and an absolute one arrive here exactly the same way,
                // and containment is asked of both alike. Before CB-140 a
                // separate "does this begin with a separator" test ran ahead
                // of this and refused every absolute path outright — which
                // is also what let a rooted path with a space in a directory
                // component slip past PersonaMarkdown's own guard, since the
                // two rules were answering different questions about the
                // same string. This is the only question worth asking:
                // where does it resolve.
                if (!IsWithin(root, combined))
                {
                    rejection = AvatarRejection.EscapesRoot;
                    return null;
                }

                if (EscapesThroughLink(root, combined))
                {
                    // A picture that is simply not there fails the link walk
                    // too: it cannot resolve a component that does not exist,
                    // and it deliberately fails closed rather than guessing.
                    // The refusal is right either way, but the *reason* is the
                    // whole value of the log line, and "escapes root" sends
                    // somebody looking for a symlink they do not have.
                    rejection = File.Exists(combined) || Directory.Exists(combined)
                        ? AvatarRejection.EscapesRoot
                        : AvatarRejection.Unreadable;
                    return null;
                }

                // One refusal rather than two, because both halves mean the
                // same thing — this string could not be turned into a file
                // inside the root — and because both are all but unreachable
                // from here in the first place: the link walk above runs
                // first and fails closed, so a missing file and a link out of
                // the tree have already been refused by the time this asks.
                // What is left is a file that changes between the two calls,
                // which no test on either runner can arrange. The category
                // still distinguishes them, so the log stays useful if one
                // ever does happen.
                var candidate = CanonicalFile(combined);
                if (candidate is null || !IsWithin(root, candidate))
                {
                    rejection = candidate is null ? AvatarRejection.Unreadable : AvatarRejection.EscapesRoot;
                    return null;
                }

                var info = new FileInfo(candidate);
                if (info.Length <= 0)
                {
                    rejection = AvatarRejection.Unreadable;
                    return null;
                }

                if (info.Length > MaxAvatarBytes)
                {
                    rejection = AvatarRejection.TooLarge;
                    length = info.Length;
                    return null;
                }

                // Set after the read rather than before it, so a picture that
                // passes every check and then fails to open leaves no path
                // behind for the next scan to watch.
                var bytes = File.ReadAllBytes(candidate);
                path = candidate;
                return bytes;
            }
            catch (IOException) { rejection = AvatarRejection.Unreadable; return null; }
            catch (UnauthorizedAccessException) { rejection = AvatarRejection.Unreadable; return null; }
            catch (ArgumentException) { rejection = AvatarRejection.Unreadable; return null; }
            catch (NotSupportedException) { rejection = AvatarRejection.Unreadable; return null; }
        }

        internal static string? CanonicalDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var directory = new DirectoryInfo(Path.GetFullPath(path));
                if (!directory.Exists) return null;
                return Trim((directory.ResolveLinkTarget(true) ?? directory).FullName);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        internal static string? CanonicalFile(string path)
        {
            try
            {
                var file = new FileInfo(Path.GetFullPath(path));
                if (!file.Exists) return null;
                return (file.ResolveLinkTarget(true) ?? file).FullName;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        internal static bool IsWithin(string root, string path) =>
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || string.Equals(root, Trim(path), StringComparison.Ordinal);

        // ResolveLinkTarget on a file only reports a link on that file, not a
        // link in one of its parent directories. Walk those components too: a
        // harmless-looking `avatars/me.png` can otherwise leave the workspace
        // through an `avatars` symlink.
        internal static bool EscapesThroughLink(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            var current = root;

            foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                try
                {
                    FileSystemInfo info = Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : new FileInfo(current);
                    var target = info.ResolveLinkTarget(true);
                    if (target is not null && !IsWithin(root, target.FullName)) return true;
                }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
                catch (ArgumentException) { return true; }
                catch (NotSupportedException) { return true; }
            }

            return false;
        }

        internal static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
    }
}
