namespace ClaudeBuddy
{
    // The gateway's identity is the published, portable answer; these files are
    // a local refinement for people who keep an agent's personality beside its
    // work.  Do not turn the workspace into a second network surface: every
    // path accepted here is proved to remain inside its canonical root.
    internal static class OpenClawWorkspaceIdentity
    {
        internal sealed record Metadata(string? Name, string? Voice, byte[]? Avatar)
        {
            internal bool IsEmpty => Name is null && Voice is null && Avatar is null;
        }

        private const long MaxAvatarBytes = 2 * 1024 * 1024;

        internal static Metadata Read(string? workspace)
        {
            var root = CanonicalDirectory(workspace);
            if (root is null) return new Metadata(null, null, null);

            try
            {
                var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(FileOrder, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string? name = null;
                string? voice = null;
                byte[]? avatar = null;

                foreach (var file in files)
                {
                    var resolved = CanonicalFile(file);
                    if (resolved is null || !IsWithin(root, resolved)) continue;

                    var fields = Parse(File.ReadAllLines(resolved));
                    name ??= fields.Name;
                    voice ??= fields.Voice;
                    avatar ??= AvatarAt(root, fields.Avatar);
                }

                return new Metadata(name, voice, avatar);
            }
            catch (IOException) { return new Metadata(null, null, null); }
            catch (UnauthorizedAccessException) { return new Metadata(null, null, null); }
            catch (ArgumentException) { return new Metadata(null, null, null); }
            catch (NotSupportedException) { return new Metadata(null, null, null); }
        }

        // OpenClaw's IDENTITY.md format is deliberately Markdown, not a second
        // config language: `- Name: Aurora`.  Keep this grammar equally small
        // for Voice so prose mentioning "voice:" cannot silently change speech.
        internal static Fields Parse(IEnumerable<string> lines)
        {
            string? name = null;
            string? voice = null;
            string? avatar = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("-", StringComparison.Ordinal)) continue;

                var colon = trimmed.IndexOf(':');
                if (colon < 2) continue;

                var label = trimmed[1..colon].Trim();
                var value = trimmed[(colon + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(value) || IsPlaceholder(value)) continue;

                if (name is null && string.Equals(label, "Name", StringComparison.OrdinalIgnoreCase))
                    name = value;
                else if (voice is null && string.Equals(label, "Voice", StringComparison.OrdinalIgnoreCase))
                    voice = value;
                else if (avatar is null && string.Equals(label, "Avatar", StringComparison.OrdinalIgnoreCase))
                    avatar = value;
            }

            return new Fields(name, voice, avatar);
        }

        internal sealed record Fields(string? Name, string? Voice, string? Avatar);

        private static string FileOrder(string path)
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, "IDENTITY.md", StringComparison.OrdinalIgnoreCase)) return "0";
            if (string.Equals(name, "SOUL.md", StringComparison.OrdinalIgnoreCase)) return "1";
            return "2" + name;
        }

        private static bool IsPlaceholder(string value) =>
            value.StartsWith("<", StringComparison.Ordinal) && value.EndsWith(">", StringComparison.Ordinal);

        private static byte[]? AvatarAt(string root, string? avatar)
        {
            if (string.IsNullOrWhiteSpace(avatar) || Path.IsPathRooted(avatar)) return null;

            try
            {
                var combined = Path.GetFullPath(Path.Combine(root, avatar));
                if (!IsWithin(root, combined) || EscapesThroughLink(root, combined)) return null;

                var candidate = CanonicalFile(combined);
                if (candidate is null || !IsWithin(root, candidate)) return null;

                var info = new FileInfo(candidate);
                return info.Length is > 0 and <= MaxAvatarBytes ? File.ReadAllBytes(candidate) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        private static string? CanonicalDirectory(string? path)
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

        private static string? CanonicalFile(string path)
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

        private static bool IsWithin(string root, string path) =>
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || string.Equals(root, Trim(path), StringComparison.Ordinal);

        // ResolveLinkTarget on a file only reports a link on that file, not a
        // link in one of its parent directories. Walk those components too: a
        // harmless-looking `avatars/me.png` can otherwise leave the workspace
        // through an `avatars` symlink.
        private static bool EscapesThroughLink(string root, string path)
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

        private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
    }
}
