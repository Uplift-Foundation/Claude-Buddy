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
    internal static class LocalPersona
    {
        internal sealed record Persona(
            string? Name,
            string? Voice,
            double? Rate,
            byte[]? Avatar,
            string? AvatarSource,
            IReadOnlyList<string> Files)
        {
            internal bool IsEmpty => Name is null && Voice is null && Avatar is null;
        }

        internal static readonly Persona Empty =
            new(null, null, null, null, null, Array.Empty<string>());

        internal static IReadOnlyList<string> CandidateFiles(
            string? cwd, IEnumerable<string> userConfigDirs, SessionSource source) =>
            throw new NotImplementedException();

        internal static IReadOnlyList<(string Path, string[] Lines)> Load(IReadOnlyList<string> candidates) =>
            throw new NotImplementedException();

        internal static Persona Resolve(string? cwd, SessionSource source, IEnumerable<string> userConfigDirs) =>
            throw new NotImplementedException();

        internal static string Signature(IEnumerable<string> paths) => throw new NotImplementedException();

        internal static Persona ResolveForSession(SessionStatus status) => throw new NotImplementedException();

        // Agent > persona > title > folder. Pure, and here rather than in
        // OrbWindow because "what is this orb called" is a rule with a right
        // answer and OrbWindow is a place you have to look at to check one.
        internal static string OrbLabel(string? agent, string? personaName, string? title, string? folder) =>
            throw new NotImplementedException();
    }

    // Which persona belongs to which live session, for the drawing code to ask.
    // Mirrors OpenClawSessions' identity registry: the scan writes, everything
    // that draws reads, and a lock keeps the two apart.
    internal static class LocalPersonas
    {
        internal static void Set(string sessionId, LocalPersona.Persona persona) =>
            throw new NotImplementedException();

        internal static void Forget(string sessionId) => throw new NotImplementedException();

        internal static LocalPersona.Persona? For(string? sessionId) => throw new NotImplementedException();

        internal static void SetForTests(IReadOnlyDictionary<string, LocalPersona.Persona> personas) =>
            throw new NotImplementedException();

        internal static string AvatarKey(string sessionId) => throw new NotImplementedException();

        internal static TextToSpeech.VoiceOption? VoiceForSession(
            string? sessionId, IEnumerable<TextToSpeech.VoiceOption> options) =>
            throw new NotImplementedException();

        internal static TextToSpeech.VoiceOption? VoiceForSession(string? sessionId) =>
            throw new NotImplementedException();

        internal static double? RateForSession(string? sessionId) => throw new NotImplementedException();
    }
}
