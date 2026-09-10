namespace ClaudeBuddy
{
    // Every read a persona makes of the disk, and the proofs that go with it.
    //
    // Shared with OpenClawWorkspaceIdentity for the same reason the grammar is:
    // the guarantees here are the security half of this feature, and a second
    // copy of them is a second place for one of them to be quietly dropped.
    internal static class PersonaFiles
    {
        // A CLAUDE.md is prose someone writes; it is not a data file, and one
        // larger than this is either generated or not what we think it is.
        internal const long MaxMarkdownBytes = 256 * 1024;

        internal const long MaxAvatarBytes = 2 * 1024 * 1024;

        internal static string[]? ReadMarkdown(string path) => throw new NotImplementedException();

        internal static byte[]? AvatarAt(string root, string? avatar) => throw new NotImplementedException();

        internal static string? CanonicalDirectory(string? path) => throw new NotImplementedException();

        internal static string? CanonicalFile(string path) => throw new NotImplementedException();

        internal static bool IsWithin(string root, string path) => throw new NotImplementedException();

        internal static bool EscapesThroughLink(string root, string path) => throw new NotImplementedException();

        internal static string Trim(string path) => throw new NotImplementedException();
    }
}
