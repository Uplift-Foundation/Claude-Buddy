using Avalonia.Headless.XUnit;
using SkiaSharp;
using Xunit;

namespace ClaudeBuddy.Tests;

// OpenClawAvatars.ForFile — the path-taking entry point CB-135 added so a local
// persona need not keep its picture's bytes.
//
// The distinction it exists for is not visible from the outside of a decode:
// the gateway's avatars arrive as base64 over the wire and have nowhere else to
// live, while a persona's picture is a file already on this disk that every
// session in the repository could read for itself. Holding those bytes on the
// persona record meant one copy per session of one file, on a machine that
// routinely runs twenty or thirty agents — so what is asserted here is that the
// file is read at the decode, once, and that a cache hit reads nothing at all.
//
// In tests/UiTests for the reason PersonaPortraitCacheTests gives: the decode
// ends in an Avalonia WriteableBitmap, and in a process with no Avalonia behind
// it every one of these would return null and every assertion would hold
// against a decoder that never ran.
public class PersonaAvatarFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cb-avatar-file-" + Guid.NewGuid());

    private readonly List<string> _keys = new();

    public PersonaAvatarFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var key in _keys) OpenClawAvatars.Forget(key);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Key()
    {
        var key = "local:file-" + Guid.NewGuid();
        _keys.Add(key);
        return key;
    }

    private static byte[] Png(byte r = 0x8A, byte g = 0x6F, byte b = 0xD4)
    {
        var info = new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(new SKColor(r, g, b, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private string WritePng(string name = "portrait.png")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Png());
        return path;
    }

    [AvaloniaFact]
    public void APictureOnDiskIsDecodedIntoFrames()
    {
        var avatar = OpenClawAvatars.ForFile(Key(), WritePng());

        Assert.NotNull(avatar);
        Assert.Single(avatar!.Frames);
        Assert.False(avatar.IsAnimated);
    }

    // CB-140: LocalPersona.Resolve now hands back an absolute AvatarPath when
    // a profile's `image:` field is itself absolute — an ordinary shape for a
    // generator whose persona file is `@`-imported from an arbitrary
    // directory. This decoder was never given a relative-vs-absolute
    // distinction to make in the first place (WritePng above has always
    // handed it an absolute path), so the assertion worth making is the seam,
    // not the decode: a real persona resolved end to end, whose path happens
    // to be absolute, draws exactly like any other.
    [AvaloniaFact]
    public void AnAbsoluteAvatarPathFromAResolvedPersonaDecodesTheSameWay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cb-persona-avatar-abs-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var picture = Path.Combine(directory, "avatar.png");
            File.WriteAllBytes(picture, Png());
            File.WriteAllText(
                Path.Combine(directory, "CLAUDE.md"),
                "---\nname: \"Skyler\"\nimage: \"" + picture + "\"\n---\n");

            var persona = LocalPersona.Resolve(directory, SessionSource.ClaudeCode, Array.Empty<string>());
            Assert.Equal(picture, persona.AvatarPath);

            var avatar = OpenClawAvatars.ForFile(Key(), persona.AvatarPath!);

            Assert.NotNull(avatar);
            Assert.Single(avatar!.Frames);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    // CB-147: a picture named relative to the workspace root rather than to
    // the directory of the markdown that named it — reached the way the app
    // actually reaches it, through the same `@`-import shape this
    // repository's own CLAUDE.md uses, so the import hop is exercised rather
    // than a hand-picked root. No new visible surface comes with this
    // ticket, so there is no new tests/UiScreenshots capture to add — the
    // decode path below is the same one AnAbsoluteAvatarPathFromAResolvedPersonaDecodesTheSameWay
    // already draws through, and this is the same seam with a different root
    // winning the resolve.
    [AvaloniaFact]
    public void AWorkspaceRelativeAvatarPathFromAResolvedPersonaDecodesTheSameWay()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "cb-persona-avatar-ws-" + Guid.NewGuid());
        var personaDir = Path.Combine(workspace, ".claude", "persona");
        Directory.CreateDirectory(personaDir);
        try
        {
            var picture = Path.Combine(personaDir, "margo.gif");
            File.WriteAllBytes(picture, Png());
            File.WriteAllText(Path.Combine(workspace, "CLAUDE.md"), "@.claude/persona/persona.md\n");
            File.WriteAllText(
                Path.Combine(personaDir, "persona.md"),
                "- Name: Margo\n- Profile picture: .claude/persona/margo.gif\n");

            var persona = LocalPersona.Resolve(workspace, SessionSource.ClaudeCode, Array.Empty<string>());
            Assert.Equal(picture, persona.AvatarPath);

            var avatar = OpenClawAvatars.ForFile(Key(), persona.AvatarPath!);

            Assert.NotNull(avatar);
            Assert.Single(avatar!.Frames);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    // The cache is the whole point of the entry point taking a key. An orb asks
    // for its picture on every poll tick; a read per tick is what the decoded
    // cache has always existed to avoid, and moving the bytes out of the
    // persona record must not have quietly turned it into one.
    [AvaloniaFact]
    public void ASecondAskReadsNothingAndAnswersFromTheCache()
    {
        var key = Key();
        var path = WritePng();

        var first = OpenClawAvatars.ForFile(key, path);
        Assert.NotNull(first);

        // Taking the file away is what makes this a measurement rather than a
        // hope: a second read would fail, so an identical answer can only have
        // come from the cache.
        File.Delete(path);

        Assert.Same(first, OpenClawAvatars.ForFile(key, path));
    }

    [AvaloniaFact]
    public void APictureThatIsNotThereIsNoPicture()
    {
        Assert.Null(OpenClawAvatars.ForFile(Key(), Path.Combine(_dir, "absent.png")));
    }

    // 9 MiB, not 17: this was the size that tested the cap before CB-146 raised
    // it to 16 MiB. Left at 9 it would still return null, but for the wrong
    // reason — the zero bytes below are not a real PNG regardless of size, so
    // an unmoved literal here would keep passing while silently testing decode
    // failure instead of the cap. Sized to sit over the *current* cap so this
    // stays a test of `AFileTooBigForTheCapIsNoPicture` and not of something
    // else with the same name.
    [AvaloniaFact]
    public void AFileTooBigForTheCapIsNoPicture()
    {
        var path = Path.Combine(_dir, "huge.png");
        File.WriteAllBytes(path, new byte[(int)PersonaFiles.MaxAvatarBytes + 1]);

        Assert.Null(OpenClawAvatars.ForFile(Key(), path));
    }

    // The boundary CB-146 exists for, driven through the real UI-visible path
    // rather than through PersonaFiles directly: a picture between the old
    // 8 MiB cap and the new 16 MiB one now reaches the surface an orb actually
    // draws, where before this ticket it would have decoded to nothing and
    // fallen back to the emoji. 8,391,801 is not a round number — it is the
    // real animated persona's exact size that forced this change (see
    // PersonaFiles.MaxAvatarBytes), used here rather than an arbitrary
    // in-between value so this test is pinned to the actual case, not merely
    // a plausible one.
    [AvaloniaFact]
    public void APictureBetweenTheOldAndNewCapsIsDecodedIntoFrames()
    {
        var path = Path.Combine(_dir, "between-caps.png");
        File.WriteAllBytes(path, Png());
        using (var stream = new FileStream(path, FileMode.Append))
        {
            // Padded past 8,388,608 (the old cap) up to the real file's exact
            // size, 8,391,801 — a real PNG header followed by padding decodes
            // exactly like the unpadded one, since SkiaSharp reads the format
            // from the header and stops there.
            stream.Write(new byte[8_391_801 - new FileInfo(path).Length]);
        }

        var avatar = OpenClawAvatars.ForFile(Key(), path);

        Assert.NotNull(avatar);
        Assert.Single(avatar!.Frames);
    }

    // Bytes that are not a picture decode to nothing, the same as they do
    // through the byte-taking entry point — falling back to the letters is the
    // contract of this class and a file is not a reason to change it.
    [AvaloniaFact]
    public void AFileThatIsNotAPictureIsNoPicture()
    {
        var path = Path.Combine(_dir, "notes.png");
        File.WriteAllText(path, "I am not a picture");

        Assert.Null(OpenClawAvatars.ForFile(Key(), path));
    }

    // Forgetting a key is what LocalPersonas.Set does when a persona changes,
    // and it has to send the next ask back to the file rather than to the
    // cache — otherwise a portrait replaced in place stays on the orb, which is
    // the defect PersonaPortraitCacheTests was written for.
    [AvaloniaFact]
    public void ForgettingAKeySendsTheNextAskBackToTheFile()
    {
        var key = Key();
        var path = WritePng();

        var first = OpenClawAvatars.ForFile(key, path);
        OpenClawAvatars.Forget(key);

        var second = OpenClawAvatars.ForFile(key, path);

        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }
}
