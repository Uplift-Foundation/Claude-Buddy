namespace Orbweaver
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

        // What an engine build can do, as a number the app can read without
        // running it. 1 = honours VolumeEnvVar. Bump when the engine learns
        // something the app has to know it learned.
        //
        // Needed because the engine's directory is keyed by the app's version,
        // and a rebuild that keeps the version — every branch build, and any
        // re-release of the same number — puts an engine that ignores the
        // level at exactly the path the app expects. CB-200's second review
        // measured precisely that on a real machine: the released 0.5.9-beta
        // engine sat at speech-engine/0.5.9-beta, the branch build of
        // 0.5.9-beta took it for its own, and the slider silently did nothing.
        // A path says where an engine is, not what it can do.
        public const int ContractVersion = 1;

        // The file that carries ContractVersion beside the engine executable.
        // Written by the engine's own csproj on build and on publish (so both
        // release scripts and a plain `dotnet build` produce it, and it rides
        // inside the downloaded zip), holding the number above — which the
        // csproj reads out of this file, so the two cannot disagree. An engine
        // folder without it is from before CB-200 and is treated as ignoring
        // the level.
        public const string StampFileName = "engine-contract.txt";
    }
}
