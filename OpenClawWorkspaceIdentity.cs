namespace ClaudeBuddy
{
    // The gateway's identity is the published, portable answer; these files are
    // a local refinement for people who keep an agent's personality beside its
    // work.  Do not turn the workspace into a second network surface: every
    // path accepted here is proved to remain inside its canonical root.
    //
    // The grammar and the file handling both moved out from under this class
    // when local sessions started reading their own CLAUDE.md — to
    // PersonaMarkdown and PersonaFiles respectively — because one file format
    // read by two parsers is two grammars that drift apart silently. What is
    // left here is the part that is genuinely OpenClaw's: which files in a
    // workspace directory are asked, and in what order.
    internal static class OpenClawWorkspaceIdentity
    {
        internal sealed record Metadata(string? Name, string? Voice, double? Rate, byte[]? Avatar)
        {
            internal bool IsEmpty => Name is null && Voice is null && Avatar is null;
        }

        internal static Metadata Read(string? workspace)
        {
            var root = PersonaFiles.CanonicalDirectory(workspace);
            if (root is null) return new Metadata(null, null, null, null);

            try
            {
                var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(FileOrder, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string? name = null;
                string? voice = null;
                double? rate = null;
                byte[]? avatar = null;

                foreach (var file in files)
                {
                    var resolved = PersonaFiles.CanonicalFile(file);
                    if (resolved is null || !PersonaFiles.IsWithin(root, resolved)) continue;

                    var fields = Parse(File.ReadAllLines(resolved));
                    name ??= fields.Name;
                    voice ??= fields.Voice;
                    rate ??= fields.Rate;
                    // The whole Fields rather than fields.Avatar, so a value
                    // that named a picture and did not read as one is refused
                    // out loud instead of being indistinguishable from a file
                    // that named none — see PersonaFiles.AvatarAt's own
                    // comment for why the check lives there and not here.
                    avatar ??= PersonaFiles.AvatarAt(root, fields);
                }

                return new Metadata(name, voice, rate, avatar);
            }
            catch (IOException) { return new Metadata(null, null, null, null); }
            catch (UnauthorizedAccessException) { return new Metadata(null, null, null, null); }
            catch (ArgumentException) { return new Metadata(null, null, null, null); }
            catch (NotSupportedException) { return new Metadata(null, null, null, null); }
        }

        // Kept as a forwarder rather than deleted: every caller and every test
        // that already asks a workspace to parse its own metadata is asking the
        // right question, and making them all say PersonaMarkdown instead would
        // be a diff about naming in a change that is about behaviour.
        internal static PersonaMarkdown.Fields Parse(IEnumerable<string> lines) =>
            PersonaMarkdown.Parse(lines);

        private static string FileOrder(string path)
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, "IDENTITY.md", StringComparison.OrdinalIgnoreCase)) return "0";
            if (string.Equals(name, "SOUL.md", StringComparison.OrdinalIgnoreCase)) return "1";
            return "2" + name;
        }
    }
}
