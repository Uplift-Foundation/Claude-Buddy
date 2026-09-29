namespace ClaudeBuddy
{
    // The names both halves of the app-to-engine contract spell, in one file
    // compiled into both: the side-car engine picks it up from its own folder,
    // and ClaudeBuddy.csproj links it in explicitly past its tools\** removal.
    // One constant rather than two copies and a test that they agree, because
    // a copy that drifts does not fail loudly — the engine just ignores the
    // new name and speaks at full volume, which sounds like the slider being
    // broken rather than like a typo (CB-200 QA).
    //
    // Plain constants only. The engine builds without the app, so nothing here
    // may reach for anything but the BCL.
    internal static class SpeechEngineContract
    {
        // How the Speech level reaches the engine: an invariant decimal in
        // 0..1, unset at full volume. AudioVolume.SpeechVolumeEnvVar says why
        // an environment variable rather than an argument. A user's custom
        // speak command gets the same name (always set there, "1" included —
        // see TextToSpeech.CustomCommandStartInfo), which is part of why the
        // name must never drift: the README promises it to them too.
        public const string VolumeEnvVar = "CLAUDEBUDDY_SPEECH_VOLUME";
    }
}
