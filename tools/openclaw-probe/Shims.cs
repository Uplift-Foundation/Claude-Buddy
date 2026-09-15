namespace ClaudeBuddy
{
    // ClaudeBuddySettings is compiled in for its gateway fields, and reaches
    // for a default voice name from each speech engine on the way past. The
    // real ones pull in Whisper, PvRecorder and the platform audio stack, none
    // of which a probe that reads JSON off a socket has any use for.
    //
    // Stand-ins rather than compiling those files: the values are only ever
    // read as a fallback for a setting the probe never touches.
    internal static class TextToSpeech
    {
        public const string DefaultVoice = "";
    }

    internal static class NeuralSpeech
    {
        public const string DefaultVoiceName = "";
    }

    // Only PortToBind's default reaches for this, and the probe never binds a
    // peer listener. The real PeerLink is 800 lines with the whole peer
    // protocol behind it, so the constant is restated rather than dragged in.
    internal static class PeerLink
    {
        public const int DefaultPort = 7677;
    }
}
