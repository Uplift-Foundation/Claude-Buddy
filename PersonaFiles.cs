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
        // It is no longer retained at all: a local persona carries AvatarPath
        // and the decode reads the file itself (ReadAvatarFile below, through
        // OpenClawAvatars.ForFile), so what stays resident is the 144 px frame
        // cache that was always the real cost, and this cap now bounds a
        // transient read. That is what makes eight defensible where two was
        // not — 2 MiB is a perfectly ordinary size for a portrait exported
        // from a phone, and this repository's own `cto.png` cleared the old cap
        // by 57 KB.
        internal const long MaxAvatarBytes = 8 * 1024 * 1024;

        // Why a picture named in markdown was not drawn. Four categories
        // rather than a message per site, because the useful question when
        // reading persona.log is which *kind* of thing went wrong: a rooted
        // path and a symlink out of the tree are somebody writing a path this
        // app will not follow, an oversized file is somebody's camera, and
        // unreadable is the filesystem.
        internal enum AvatarRejection { Rooted, EscapesRoot, TooLarge, Unreadable }

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

        internal static string RejectionMessage(AvatarRejection reason, string picture, long bytes)
        {
            var detail = reason switch
            {
                AvatarRejection.TooLarge =>
                    "too large — " + bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes",
                AvatarRejection.EscapesRoot =>
                    "escapes root — it resolves outside the directory of the markdown that named it",
                AvatarRejection.Rooted =>
                    "rooted path — a persona picture is relative to the markdown that named it",
                _ => "unreadable — it is missing, empty, or this process may not open it",
            };

            return "persona picture ignored: \"" + Quoted(picture) + "\" (" + detail + "); cap is "
                + MaxAvatarBytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
        }

        private static void Reject(AvatarRejection reason, string picture, long bytes = 0) =>
            PersonaLog.Record(RejectionMessage(reason, picture, bytes));

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
        internal static byte[]? AvatarAt(string root, string? avatar, out string? path)
        {
            path = null;

            // A blank field is a field nobody filled in, and is not a
            // rejection worth writing down; a rooted one is somebody being
            // refused and is.
            if (string.IsNullOrWhiteSpace(avatar)) return null;
            if (Path.IsPathRooted(avatar))
            {
                Reject(AvatarRejection.Rooted, avatar);
                return null;
            }

            try
            {
                var combined = Path.GetFullPath(Path.Combine(root, avatar));
                if (!IsWithin(root, combined) || EscapesThroughLink(root, combined))
                {
                    // A picture that is simply not there fails the link walk
                    // too: it cannot resolve a component that does not exist,
                    // and it deliberately fails closed rather than guessing.
                    // The refusal is right either way, but the *reason* is the
                    // whole value of the log line, and "escapes root" sends
                    // somebody looking for a symlink they do not have.
                    Reject(
                        File.Exists(combined) || Directory.Exists(combined)
                            ? AvatarRejection.EscapesRoot
                            : AvatarRejection.Unreadable,
                        avatar);
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
                    Reject(
                        candidate is null ? AvatarRejection.Unreadable : AvatarRejection.EscapesRoot,
                        avatar);
                    return null;
                }

                var info = new FileInfo(candidate);
                if (info.Length <= 0)
                {
                    Reject(AvatarRejection.Unreadable, avatar);
                    return null;
                }

                if (info.Length > MaxAvatarBytes)
                {
                    Reject(AvatarRejection.TooLarge, avatar, info.Length);
                    return null;
                }

                // Set after the read rather than before it, so a picture that
                // passes every check and then fails to open leaves no path
                // behind for the next scan to watch.
                var bytes = File.ReadAllBytes(candidate);
                path = candidate;
                return bytes;
            }
            catch (IOException) { Reject(AvatarRejection.Unreadable, avatar); return null; }
            catch (UnauthorizedAccessException) { Reject(AvatarRejection.Unreadable, avatar); return null; }
            catch (ArgumentException) { Reject(AvatarRejection.Unreadable, avatar); return null; }
            catch (NotSupportedException) { Reject(AvatarRejection.Unreadable, avatar); return null; }
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
