using System.Text.RegularExpressions;

namespace ClaudeBuddy
{
    // The Markdown grammar an agent's identity is written in, shared by the
    // two places that read one: an OpenClaw workspace's IDENTITY.md, and a
    // local session's CLAUDE.md.
    //
    // Lifted out of OpenClawWorkspaceIdentity rather than copied. Two parsers
    // for one file format is two grammars that drift, and the drift is
    // invisible — a field a user writes once and sees honoured on one orb and
    // ignored on another reads as a bug in the app, which it would be.
    internal static class PersonaMarkdown
    {
        internal sealed record Fields(string? Name, string? Voice, double? Rate, string? Avatar);

        // Which field a prose sentence named, if it named one at all.
        internal enum ProseKind { None, Name, Voice, Avatar }

        internal static Fields Parse(IEnumerable<string> lines) => throw new NotImplementedException();

        internal static bool ProseField(string trimmed, out ProseKind kind, out string value) =>
            throw new NotImplementedException();

        internal static bool VoiceLabel(string label) => throw new NotImplementedException();

        internal static bool AvatarLabel(string label) => throw new NotImplementedException();

        internal static (string? Voice, double? Rate) VoiceValue(string value) =>
            throw new NotImplementedException();
    }
}
