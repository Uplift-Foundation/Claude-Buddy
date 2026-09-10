using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace ClaudeBuddy.Tests;

// The seam CB-136 actually added: reading real `.npy` files off a disk,
// averaging them, and writing the result where the engine will find it.
//
// A unit test over bytes says the parser and the arithmetic are right; it
// cannot say that the file lands in the directory the engine is passed, that
// the second speak does not rewrite it, or that a blend nobody can build
// leaves nothing behind. Those are the three ways this fails in a way the
// user sees, and they are all about a filesystem.
//
// [Collection("LogDir")] for two reasons at once. This class points
// CLAUDE_BUDDY_LOG_DIR at a scratch directory, which is what that collection
// exists to serialise; and VoiceBlends' materialised-slug table and its paths
// override are process-wide statics, which need serialising too. One
// collection covers both because this is the only class in this assembly that
// touches the second.
[Collection("LogDir")]
public class VoiceBlendFileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-blend-" + Guid.NewGuid());

    private readonly string _engineVoices;
    private readonly string _userVoices;
    private readonly string _logDir;
    private readonly string? _logWas;

    public VoiceBlendFileTests()
    {
        _engineVoices = Path.Combine(_root, "engine", "voices");
        _userVoices = Path.Combine(_root, "voices");
        _logDir = Path.Combine(_root, "log");

        Directory.CreateDirectory(_engineVoices);
        Directory.CreateDirectory(_userVoices);

        _logWas = Environment.GetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _logDir);
        PersonaLog.ResetForTests();

        VoiceBlends.SetPathsForTests(
            new VoiceBlends.Paths(new[] { _engineVoices, _userVoices }, _userVoices));
    }

    public void Dispose()
    {
        VoiceBlends.SetPathsForTests(null);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_LOG_DIR", _logWas);
        PersonaLog.ResetForTests();

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // --- fixtures -----------------------------------------------------------

    // A voice file of the shape every real one has — a 128-byte version 1.0
    // prelude over `(510, 1, 256)` little-endian float32 — with every element
    // set to one value, so a weighted average has an answer this test can
    // state in one number rather than in half a million.
    //
    // The real shape rather than a two-element toy, deliberately: the header
    // this writes is the header the engine's own files carry, and a reader
    // that only ever met `(2,)` would not have been asked the question that
    // matters.
    private const int Elements = 510 * 256;

    private void WriteVoice(string directory, string name, float value)
    {
        var dictionary = "{'descr': '<f4', 'fortran_order': False, 'shape': (510, 1, 256), }";
        var padding = (64 - ((10 + dictionary.Length + 1) % 64)) % 64;
        var header = dictionary + new string(' ', padding) + "\n";

        var bytes = new byte[10 + header.Length + Elements * 4];
        bytes[0] = 0x93;
        Encoding.ASCII.GetBytes("NUMPY").CopyTo(bytes, 1);
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)header.Length);
        Encoding.ASCII.GetBytes(header).CopyTo(bytes, 10);

        var body = bytes.AsSpan(10 + header.Length);
        for (var i = 0; i < Elements; i++) BinaryPrimitives.WriteSingleLittleEndian(body.Slice(i * 4, 4), value);

        File.WriteAllBytes(Path.Combine(directory, name + ".npy"), bytes);
    }

    private static float[] ValuesOf(string path)
    {
        var tensor = NumpyVoices.Read(File.ReadAllBytes(path), out var refusal);
        Assert.Null(refusal);
        return tensor!.Values;
    }

    private static TextToSpeech.VoiceOption Neural(string name) =>
        new(TextToSpeech.SpeakEngine.Neural, name, $"{name} (Kokoro)");

    private static readonly TextToSpeech.VoiceOption[] Installed =
        { Neural("af_sky"), Neural("af_nicole"), Neural("af_bella") };

    private string Blended(string path) => Path.Combine(_userVoices, path + ".npy");

    private string[] LogLines() =>
        File.Exists(PersonaLog.Path_)
            ? File.ReadAllLines(PersonaLog.Path_).Where(l => l.Contains("blend", StringComparison.Ordinal)).ToArray()
            : Array.Empty<string>();

    // --- the ticket's own case -----------------------------------------------

    [Fact]
    public void AFiftyFiftyBlendIsWrittenIntoTheUserVoicesDirectoryAsTheAverage()
    {
        WriteVoice(_engineVoices, "af_sky", 1f);
        WriteVoice(_engineVoices, "af_nicole", 3f);

        var option = TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed);

        Assert.NotNull(option);
        Assert.Equal(TextToSpeech.SpeakEngine.Neural, option!.Engine);
        Assert.Equal("af_blend_sky50-nicole50", option.Name);

        var written = Blended("af_blend_sky50-nicole50");
        Assert.True(File.Exists(written));

        // Every element, not a sample: an averaging bug that lands on one
        // slice of the tensor is exactly the kind that reads as a slightly
        // odd voice rather than as a failure.
        var values = ValuesOf(written);
        Assert.Equal(Elements, values.Length);
        Assert.All(values, v => Assert.Equal(2f, v));

        // ...and it is byte-for-byte the size of a real voice file, which is
        // what says the header was written the way the engine's own are.
        Assert.Equal(522_368, new FileInfo(written).Length);
    }

    [Fact]
    public void AnUnevenBlendLeansTowardTheHeavierVoice()
    {
        WriteVoice(_engineVoices, "af_sky", 0f);
        WriteVoice(_engineVoices, "af_nicole", 100f);

        var option = TextToSpeech.VoiceForPersona("sky 75% and nicole 25%", Installed);

        Assert.Equal("af_blend_sky75-nicole25", option!.Name);
        Assert.All(ValuesOf(Blended("af_blend_sky75-nicole25")), v => Assert.Equal(25f, v));
    }

    [Fact]
    public void ThreeEqualPartsAverageAllThreeAtTheSharesTheSlugNames()
    {
        WriteVoice(_engineVoices, "af_sky", 0f);
        WriteVoice(_engineVoices, "af_nicole", 100f);
        WriteVoice(_engineVoices, "af_bella", 200f);

        var option = TextToSpeech.VoiceForPersona("sky and nicole and bella", Installed);

        // 34/33/33 rather than three thirds, and the file holds exactly what
        // the name claims: 0*0.34 + 100*0.33 + 200*0.33 = 99.
        Assert.Equal("af_blend_sky34-nicole33-bella33", option!.Name);
        Assert.All(ValuesOf(Blended("af_blend_sky34-nicole33-bella33")), v => Assert.Equal(99f, v));
    }

    // --- written once ---------------------------------------------------------

    // Twice through the whole entry point, which is what a second click on
    // the speak button does. The file must not be rewritten: an orb speaking
    // for the hundredth time cannot be paying for half a megabyte of
    // arithmetic and a disk write each time.
    [Fact]
    public void TheSecondSpeakReusesTheFileRatherThanRebuildingIt()
    {
        WriteVoice(_engineVoices, "af_sky", 1f);
        WriteVoice(_engineVoices, "af_nicole", 3f);

        var first = TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed);
        var written = Blended("af_blend_sky50-nicole50");

        // A marker in the file rather than a timestamp: a mtime comparison on
        // a fast machine can pass while the file was rewritten inside the
        // clock's resolution, which is the one failure this test exists to
        // catch.
        File.WriteAllBytes(written, File.ReadAllBytes(written).Append((byte)0).ToArray());
        var marked = new FileInfo(written).Length;

        var second = TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed);

        Assert.Equal(first!.Name, second!.Name);
        Assert.Equal(marked, new FileInfo(written).Length);
    }

    // ...and the reuse survives the process losing its in-memory table, which
    // is what a restart is. The file-exists check is the half that has to
    // work on the machine of somebody who built this blend last week.
    [Fact]
    public void AFileBuiltByAnEarlierRunIsReusedWithoutTheConstituentsBeingThere()
    {
        WriteVoice(_engineVoices, "af_sky", 1f);
        WriteVoice(_engineVoices, "af_nicole", 3f);

        Assert.NotNull(TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed));

        // The table cleared, and the ingredients taken away: nothing but the
        // already-written file can answer now.
        VoiceBlends.SetPathsForTests(
            new VoiceBlends.Paths(new[] { _engineVoices, _userVoices }, _userVoices));
        File.Delete(Path.Combine(_engineVoices, "af_sky.npy"));
        File.Delete(Path.Combine(_engineVoices, "af_nicole.npy"));

        var again = TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed);

        Assert.Equal("af_blend_sky50-nicole50", again!.Name);
    }

    // The same mixture written two ways is one file, which is the whole
    // reason the slug is derived rather than taken from the text.
    [Fact]
    public void TwoSpellingsOfOneMixtureShareOneFile()
    {
        WriteVoice(_engineVoices, "af_sky", 2f);
        WriteVoice(_engineVoices, "af_nicole", 4f);

        Assert.NotNull(TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed));
        Assert.NotNull(TextToSpeech.VoiceForPersona("sky 50%, nicole 50%", Installed));

        Assert.Single(Directory.GetFiles(_userVoices, "*.npy"));
    }

    // --- a blend that is a voice ------------------------------------------------

    // One part is that voice, and writing a byte-for-byte copy of a file the
    // engine already has would be a second name for one voice.
    [Fact]
    public void ASinglePartBlendWritesNothingAtAll()
    {
        WriteVoice(_engineVoices, "af_sky", 1f);

        var option = TextToSpeech.VoiceForPersona("50% sky", Installed);

        Assert.Equal("af_sky", option!.Name);
        Assert.Empty(Directory.GetFiles(_userVoices, "*.npy"));
    }

    // --- a blend of a blend ------------------------------------------------------

    // The target directory is on the search path on purpose, so a slug that
    // has already been written is just another voice. Not a use case anybody
    // has asked for — it is the property that says the written file is a real
    // voice rather than something the engine merely tolerates.
    [Fact]
    public void AVoiceTheUserAddedThemselvesCanBePartOfABlend()
    {
        WriteVoice(_engineVoices, "af_sky", 0f);
        WriteVoice(_userVoices, "annabel_mix", 10f);

        var option = TextToSpeech.VoiceForPersona(
            "50% annabel_mix and 50% sky",
            new[] { Neural("af_sky"), Neural("annabel_mix") });

        Assert.Equal("blend_annabel_mix50-af_sky50", option!.Name);
        Assert.All(ValuesOf(Blended("blend_annabel_mix50-af_sky50")), v => Assert.Equal(5f, v));
    }

    // --- everything that goes wrong -----------------------------------------------

    // The rule the whole feature is written around: malformed is the user's
    // own voice, never silence — and nothing is left on disk to be reused as
    // if it had worked.
    [Theory]
    [InlineData("60% sky and 60% nicole")]           // a total nobody meant
    [InlineData("50% sky and 50% nobody")]           // a voice this machine has not got
    [InlineData("50% a matter of taste, plus tone")] // not voices at all
    public void AMalformedBlendWritesNothingAndSpeaksInTheGlobalVoice(string written)
    {
        WriteVoice(_engineVoices, "af_sky", 1f);
        WriteVoice(_engineVoices, "af_nicole", 3f);

        Assert.Null(TextToSpeech.VoiceForPersona(written, Installed));
        Assert.Empty(Directory.GetFiles(_userVoices, "*.npy"));
    }

    // A part the option list offers but the disk does not have: the engine is
    // mid-download, or somebody deleted a voice file by hand. Refused, logged,
    // and — importantly — not remembered as a failure, so the next click
    // builds it once the file is back.
    [Fact]
    public void APartWithNoFileBehindItIsRefusedAndSaysSoInTheLog()
    {
        WriteVoice(_engineVoices, "af_sky", 1f);

        Assert.Null(TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed));
        Assert.Empty(Directory.GetFiles(_userVoices, "*.npy"));

        var line = Assert.Single(LogLines());
        Assert.Contains("af_blend_sky50-nicole50", line);
        Assert.Contains("no voice file for af_nicole", line);

        // Not cached as broken: the ingredient arrives and the next attempt
        // builds it.
        WriteVoice(_engineVoices, "af_nicole", 3f);
        Assert.NotNull(TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed));
    }

    [Fact]
    public void AConstituentThatIsNotAVoiceFileIsRefusedAndSaysWhy()
    {
        WriteVoice(_engineVoices, "af_sky", 1f);
        File.WriteAllText(Path.Combine(_engineVoices, "af_nicole.npy"), "I am not a numpy array");

        Assert.Null(TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed));

        var line = Assert.Single(LogLines());
        Assert.Contains("af_nicole.npy", line);
        Assert.Contains("not a .npy", line);
    }

    [Fact]
    public void TwoConstituentsOfDifferentShapesAreRefusedRatherThanTruncated()
    {
        WriteVoice(_engineVoices, "af_sky", 1f);

        // A perfectly valid voice file of a different shape, which is what a
        // pack for another model would be.
        var dictionary = "{'descr': '<f4', 'fortran_order': False, 'shape': (4,), }";
        var padding = (64 - ((10 + dictionary.Length + 1) % 64)) % 64;
        var header = dictionary + new string(' ', padding) + "\n";
        var bytes = new byte[10 + header.Length + 16];
        bytes[0] = 0x93;
        Encoding.ASCII.GetBytes("NUMPY").CopyTo(bytes, 1);
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)header.Length);
        Encoding.ASCII.GetBytes(header).CopyTo(bytes, 10);
        File.WriteAllBytes(Path.Combine(_engineVoices, "af_nicole.npy"), bytes);

        Assert.Null(TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed));
        Assert.Contains("different shapes", Assert.Single(LogLines()));
    }

    // A directory that cannot be written is the same answer as every other
    // failure here, and it is the arm most likely to be met on somebody
    // else's machine — a synced folder, a permissions change, a full disk.
    [UnixFact]
    public void ATargetDirectoryThisProcessMayNotWriteIsSurvivedInSilence()
    {
        WriteVoice(_engineVoices, "af_sky", 1f);
        WriteVoice(_engineVoices, "af_nicole", 3f);

        File.SetUnixFileMode(_userVoices, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            Assert.Null(TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed));
            Assert.Contains("couldn't write", Assert.Single(LogLines()));
        }
        finally
        {
            File.SetUnixFileMode(_userVoices,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [UnixFact]
    public void AConstituentThisProcessMayNotOpenIsSurvivedInSilence()
    {
        WriteVoice(_engineVoices, "af_sky", 1f);
        WriteVoice(_engineVoices, "af_nicole", 3f);
        File.SetUnixFileMode(Path.Combine(_engineVoices, "af_nicole.npy"), UnixFileMode.None);

        try
        {
            Assert.Null(TextToSpeech.VoiceForPersona("50% sky and 50% nicole", Installed));
            Assert.Contains("couldn't read af_nicole.npy", Assert.Single(LogLines()));
        }
        finally
        {
            File.SetUnixFileMode(Path.Combine(_engineVoices, "af_nicole.npy"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
