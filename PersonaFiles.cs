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

        internal const long MaxAvatarBytes = 2 * 1024 * 1024;

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
            if (string.IsNullOrWhiteSpace(avatar) || Path.IsPathRooted(avatar)) return null;

            try
            {
                var combined = Path.GetFullPath(Path.Combine(root, avatar));
                if (!IsWithin(root, combined) || EscapesThroughLink(root, combined)) return null;

                var candidate = CanonicalFile(combined);
                if (candidate is null || !IsWithin(root, candidate)) return null;

                var info = new FileInfo(candidate);
                if (info.Length is <= 0 or > MaxAvatarBytes) return null;

                // Set after the read rather than before it, so a picture that
                // passes every check and then fails to open leaves no path
                // behind for the next scan to watch.
                var bytes = File.ReadAllBytes(candidate);
                path = candidate;
                return bytes;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
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
