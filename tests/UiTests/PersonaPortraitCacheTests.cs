using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// The decoded-picture cache, driven from the scan rather than from a persona
// handed over by hand.
//
// LocalPersonaScanTests already proves the scan notices a portrait replaced in
// place and puts the new bytes in the registry. That is only half of what a
// user sees. OpenClawAvatars keeps one decoded Avatar per key, and an orb draws
// whatever is in that cache — so new bytes in the registry and a stale entry in
// the cache still puts the old face on the screen, and the two failures are
// indistinguishable from anywhere except here.
//
// It lives in tests/UiTests because that is the only suite where the decode
// actually happens: OpenClawAvatars.For builds an Avalonia WriteableBitmap, and
// in a process with no Avalonia behind it every decode returns null and every
// assertion below would pass against a cache that was never dropped. The same
// reason ChatPanelAvatarTests and OrbAvatarTests are here.
//
// [AvaloniaFact] rather than [Fact], and that is the whole reason this file is
// not in tests/IntegrationTests: the decode ends in a WriteableBitmap, which
// needs the platform up. Without it every decode returns null, both assertions
// below hold trivially, and the suite would report a cache it never touched.
// No window is constructed all the same.
//
// [Collection("Settings")] because ApplyPersona asks LocalPersona.UserConfigDirs
// for the machine's Claude Code accounts, which reads ClaudeCodeProfileDirs —
// one process-wide static a dozen classes here touch. The cache and the
// registry are process-wide too, so each case uses its own session id and
// cleans up after itself.
[Collection("Settings")]
public class PersonaPortraitCacheTests : IDisposable
{
    private readonly string _project =
        Path.Combine(Path.GetTempPath(), "cb-portrait-cache-" + Guid.NewGuid());

    private readonly string _statusDir =
        Path.Combine(Path.GetTempPath(), "cb-portrait-status-" + Guid.NewGuid());

    private readonly string _sessionId = "portrait-cache-" + Guid.NewGuid();

    public PersonaPortraitCacheTests()
    {
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_statusDir);
    }

    public void Dispose()
    {
        OpenClawAvatars.Forget(LocalPersonas.AvatarKey(_sessionId));
        LocalPersonas.Forget(_sessionId);
        try { Directory.Delete(_project, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_statusDir, recursive: true); } catch (IOException) { }
    }

    // A real PNG, for the reason LocalPersonaUiTests' own helper gives: these
    // bytes go through the actual decoder, and only a file it can decode proves
    // anything about the cache in front of it.
    private static byte[] Portrait(byte r, byte g, byte b)
    {
        var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(r, g, b, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private SessionStatus Status() => new()
    {
        Source = SessionSource.ClaudeCode,
        State = "idle",
        Cwd = _project,
        Title = "cb-portrait-cache",
    };

    private Dictionary<(string Cwd, SessionSource Source), IReadOnlyList<string>> Pass() => new();

    // The decoded frames an orb would draw for this session, asked the way the
    // orb asks for them.
    private OpenClawAvatars.Avatar? Decoded() =>
        OpenClawAvatars.For(LocalPersonas.AvatarKey(_sessionId), LocalPersonas.For(_sessionId)?.Avatar);

    [AvaloniaFact]
    public void APortraitReplacedInPlaceIsDecodedAgainRatherThanServedFromTheCache()
    {
        var picture = Path.Combine(_project, "leota.png");
        File.WriteAllText(
            Path.Combine(_project, "CLAUDE.md"),
            "Her name is Leota.\nHer profile picture is leota.png.\n");
        File.WriteAllBytes(picture, Portrait(0x8A, 0x6F, 0xD4));

        var manager = new SessionManager(_statusDir);
        manager.ApplyPersona(_sessionId, Status(), Pass());

        var before = Decoded();
        Assert.NotNull(before);

        // Two solid 16x16 PNGs can encode to the same number of bytes, so the
        // timestamp is what has to move here — set outright rather than slept
        // for, since the claim is about the signature reading it and not about
        // this filesystem's clock resolution.
        File.WriteAllBytes(picture, Portrait(0x2E, 0xA0, 0x43));
        File.SetLastWriteTimeUtc(picture, File.GetLastWriteTimeUtc(picture).AddSeconds(5));

        manager.ApplyPersona(_sessionId, Status(), Pass());

        var after = Decoded();
        Assert.NotNull(after);

        // Not the same object. OpenClawAvatars answers from its cache on a
        // hit and never looks at the bytes it was handed, so an entry that
        // survived the change would come back identical however new the bytes
        // in the registry are — which is exactly what the orb was drawing.
        Assert.NotSame(before, after);
        Assert.Equal(Portrait(0x2E, 0xA0, 0x43), LocalPersonas.For(_sessionId)!.Avatar);
    }

    // The other side of it: nothing moved, so nothing is thrown away. A cache
    // dropped on every tick would re-decode a portrait twice a second for the
    // life of the session, which is the cost this whole signature mechanism
    // exists to avoid.
    [AvaloniaFact]
    public void AnUntouchedPortraitKeepsItsDecodedFrames()
    {
        File.WriteAllText(
            Path.Combine(_project, "CLAUDE.md"),
            "Her name is Leota.\nHer profile picture is leota.png.\n");
        File.WriteAllBytes(Path.Combine(_project, "leota.png"), Portrait(0x8A, 0x6F, 0xD4));

        var manager = new SessionManager(_statusDir);
        manager.ApplyPersona(_sessionId, Status(), Pass());

        var before = Decoded();
        Assert.NotNull(before);

        manager.ApplyPersona(_sessionId, Status(), Pass());
        manager.ApplyPersona(_sessionId, Status(), Pass());

        Assert.Same(before, Decoded());
    }
}
