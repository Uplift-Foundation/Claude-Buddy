using Xunit;

namespace ClaudeBuddy.Tests;

// The seam between a persona and the filesystem, against a real tree.
//
// LocalPersonaTests in the unit suite already asks which file wins; this asks
// the questions that only a real filesystem can answer, and they are the
// security half of the feature: a picture path is a string a user wrote in a
// markdown file, and the app reads bytes off the disk because of it. Every
// refusal below is a way that string could have pointed somewhere it has no
// business pointing, and each one is asserted against a real file that really
// exists at the far end — a refusal proved against a path that was never there
// proves nothing.
//
// Covered here rather than in the unit suite for the reason CLAUDE.md gives:
// anything touching a format or a facility someone else defines gets a test at
// the seam as well as a test of the decision, because the two fail differently.
[Collection("Settings")]
public class LocalPersonaFilesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-persona-files-" + Guid.NewGuid());

    public LocalPersonaFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // A 000-mode file from the permissions test would otherwise take the
        // whole directory with it.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static byte[] Png() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string directory, string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(directory, name), lines);

    // --- the tree, end to end ---------------------------------------------

    [Fact]
    public void AProjectInheritsWhatItDoesNotSayFromAboveIt()
    {
        var top = Dir("tree");
        var middle = Dir("tree", "team");
        var project = Dir("tree", "team", "project");
        var configDir = Dir("config");

        Write(configDir, "CLAUDE.md", "Her name is UserLevel", "Her voice is Samantha");
        Write(top, "CLAUDE.md", "Her name is Monorepo");
        Write(middle, "AGENTS.md", "Her picture is team.png");
        File.WriteAllBytes(Path.Combine(middle, "team.png"), Png());
        Write(project, "CLAUDE.md", "Her name is Leota");

        var persona = LocalPersona.Resolve(project, SessionSource.ClaudeCode, new[] { configDir });

        Assert.Equal("Leota", persona.Name);
        Assert.Equal(Png(), persona.Avatar);
        Assert.Equal(Path.Combine(middle, "AGENTS.md"), persona.AvatarSource);
        // Nothing nearer said anything about a voice, so the user-level file —
        // the last file asked — is what supplies it.
        Assert.Equal("Samantha", persona.Voice);
    }

    [Fact]
    public void APictureBesideTheMarkdownIsRead()
    {
        var project = Dir("project");
        var pictures = Dir("project", "pictures");
        File.WriteAllBytes(Path.Combine(pictures, "leota.png"), Png());

        Assert.Equal(Png(), PersonaFiles.AvatarAt(project, "pictures/leota.png"));
    }

    // --- the refusals -------------------------------------------------------

    // The file exists, and is refused for where it is rather than for being
    // absent: the relative path climbs out of the directory that named it.
    [Fact]
    public void APictureAboveTheMarkdownsDirectoryIsRefused()
    {
        var project = Dir("tree", "project");
        var above = Dir("tree");
        File.WriteAllBytes(Path.Combine(above, "outside.png"), Png());

        Assert.Equal(Png(), File.ReadAllBytes(Path.Combine(above, "outside.png")));
        Assert.Null(PersonaFiles.AvatarAt(project, Path.Combine("..", "outside.png")));
    }

    [Fact]
    public void ARootedPicturePathIsRefused()
    {
        var project = Dir("project");
        var elsewhere = Dir("elsewhere");
        var absolute = Path.Combine(elsewhere, "leota.png");
        File.WriteAllBytes(absolute, Png());

        Assert.True(Path.IsPathRooted(absolute));
        Assert.Null(PersonaFiles.AvatarAt(project, absolute));
    }

    // The path stays inside the directory as a string; the directory it walks
    // through does not stay inside as a *place*. This is the case
    // EscapesThroughLink exists for, and the one a check on the final file
    // alone would let through.
    [SymlinkFact]
    public void APictureReachedThroughALinkOutOfTheDirectoryIsRefused()
    {
        var project = Dir("project");
        var elsewhere = Dir("elsewhere");
        File.WriteAllBytes(Path.Combine(elsewhere, "leota.png"), Png());
        Directory.CreateSymbolicLink(Path.Combine(project, "pictures"), elsewhere);

        Assert.True(File.Exists(Path.Combine(project, "pictures", "leota.png")));
        Assert.Null(PersonaFiles.AvatarAt(project, "pictures/leota.png"));
    }

    [SymlinkFact]
    public void APictureThatIsItselfALinkOutOfTheDirectoryIsRefused()
    {
        var project = Dir("project");
        var elsewhere = Dir("elsewhere");
        var real = Path.Combine(elsewhere, "leota.png");
        File.WriteAllBytes(real, Png());
        File.CreateSymbolicLink(Path.Combine(project, "leota.png"), real);

        Assert.Null(PersonaFiles.AvatarAt(project, "leota.png"));
    }

    // Two megabytes is the cap, and the file at the far end is a real one this
    // process can read — the refusal is about its size and nothing else.
    [Fact]
    public void APictureLargerThanTheCapIsRefused()
    {
        var project = Dir("project");
        var big = Path.Combine(project, "big.png");
        File.WriteAllBytes(big, new byte[3 * 1024 * 1024]);

        Assert.Equal(3 * 1024 * 1024, new FileInfo(big).Length);
        Assert.Null(PersonaFiles.AvatarAt(project, "big.png"));
    }

    [Fact]
    public void AnEmptyPictureFileIsRefused()
    {
        var project = Dir("project");
        File.WriteAllBytes(Path.Combine(project, "empty.png"), Array.Empty<byte>());

        Assert.Null(PersonaFiles.AvatarAt(project, "empty.png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoPictureNamedIsNoPictureRead(string? avatar)
    {
        Assert.Null(PersonaFiles.AvatarAt(Dir("project"), avatar));
    }

    // --- the markdown cap and the permissions -------------------------------

    // A CLAUDE.md is prose someone wrote. Past a quarter of a megabyte it is
    // generated, concatenated, or not what we think it is — and a persona is
    // read on a two-second timer, so this is a cost bound as much as a sanity
    // one. Asserted with a file that would otherwise parse: the refusal has to
    // be about the size.
    [Fact]
    public void AMarkdownFilePastTheCapIsNotRead()
    {
        var project = Dir("project");
        var path = Path.Combine(project, "CLAUDE.md");
        var padding = new string('x', 300 * 1024);
        File.WriteAllLines(path, new[] { "Her name is Leota", padding });

        Assert.True(new FileInfo(path).Length > PersonaFiles.MaxMarkdownBytes);
        Assert.Null(PersonaFiles.ReadMarkdown(path));
        Assert.True(LocalPersona.Resolve(project, SessionSource.ClaudeCode, Array.Empty<string>()).IsEmpty);
    }

    [Fact]
    public void AMarkdownFileJustUnderTheCapIsRead()
    {
        var project = Dir("project");
        var path = Path.Combine(project, "CLAUDE.md");
        File.WriteAllLines(path, new[] { "Her name is Leota", new string('x', 200 * 1024) });

        Assert.True(new FileInfo(path).Length <= PersonaFiles.MaxMarkdownBytes);
        Assert.Equal("Leota", LocalPersona.Resolve(
            project, SessionSource.ClaudeCode, Array.Empty<string>()).Name);
    }

    [Fact]
    public void AMarkdownFileThatIsNotThereIsNotAnError()
    {
        Assert.Null(PersonaFiles.ReadMarkdown(Path.Combine(Dir("project"), "CLAUDE.md")));
    }

    // Unix only because the mode is: on Windows the equivalent is an ACL, which
    // a test cannot set without deciding whose account it is running as.
    [UnixFact]
    public void AMarkdownFileThisProcessCannotOpenIsNotRead()
    {
        var project = Dir("project");
        var path = Path.Combine(project, "CLAUDE.md");
        File.WriteAllLines(path, new[] { "Her name is Leota" });
        File.SetUnixFileMode(path, UnixFileMode.None);

        // Running as root would read it anyway, and the assertion below would
        // then be about nothing at all — a mode of 000 does not stop uid 0. So
        // the mode is checked to have actually taken effect before it is used
        // as the premise, rather than assumed from the chmod succeeding.
        if (CanStillRead(path)) return;

        Assert.Null(PersonaFiles.ReadMarkdown(path));
    }

    // --- paths and files the platform itself refuses ----------------------

    // A NUL in a path is the one thing every .NET path API refuses outright,
    // and it is the cheapest way to prove each of these functions answers
    // "there is nothing here" rather than throwing into a two-second scan
    // loop. Not a hypothetical input: a persona path is a token off a line of
    // a file, and a file can contain anything.
    [Fact]
    public void APathThePlatformRefusesIsNotAnExceptionOutOfAnyOfThem()
    {
        var project = Dir("project");

        Assert.Null(PersonaFiles.ReadMarkdown("a\0b"));
        Assert.Null(PersonaFiles.CanonicalDirectory("a\0b"));
        Assert.Null(PersonaFiles.CanonicalFile("a\0b"));
        Assert.Null(PersonaFiles.AvatarAt(project, "a\0b.png"));
    }

    [Fact]
    public void ADirectoryThatIsNotThereIsNotADirectory()
    {
        Assert.Null(PersonaFiles.CanonicalDirectory(Path.Combine(_root, "never-made")));
        Assert.Null(PersonaFiles.CanonicalFile(Path.Combine(_root, "never-made")));
    }

    // The file is there, is the right size, and is inside the directory — and
    // the read still fails, because the filesystem says no. That is a fall back
    // to the orb's letters, not a crash on the scan thread.
    [UnixFact]
    public void APictureThisProcessCannotOpenIsRefused()
    {
        var project = Dir("project");
        var picture = Path.Combine(project, "leota.png");
        File.WriteAllBytes(picture, Png());
        File.SetUnixFileMode(picture, UnixFileMode.None);

        // uid 0 reads it regardless, which would make the assertion below a
        // statement about nothing.
        if (CanStillRead(picture)) return;

        Assert.Null(PersonaFiles.AvatarAt(project, "leota.png"));
    }

    [Fact]
    public void APictureHeldOpenExclusivelyIsRefusedRatherThanThrown()
    {
        var project = Dir("project");
        var picture = Path.Combine(project, "leota.png");
        File.WriteAllBytes(picture, Png());

        using var exclusive = new FileStream(picture, FileMode.Open, FileAccess.Read, FileShare.None);

        // Where the platform enforces the share mode this is an IOException on
        // the read; where it does not, the bytes come back and there is nothing
        // to assert about. Both are correct behaviour for this function, and
        // neither is a reason to fail on one runner.
        var read = PersonaFiles.AvatarAt(project, "leota.png");
        Assert.True(read is null || read.SequenceEqual(Png()));
    }

    private static bool CanStillRead(string path)
    {
        try
        {
            File.ReadAllBytes(path);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }
}
