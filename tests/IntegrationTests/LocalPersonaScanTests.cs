using Xunit;

namespace ClaudeBuddy.Tests;

// The scan's half of the persona feature, driven against a real filesystem.
//
// LocalPersonaFilesTests covers what the reader refuses; the unit suites cover
// the grammar and the fold. What none of them can answer is the question this
// one is about: does the thing that runs twice a second actually put a persona
// in the registry, does it stop reading the disk once it has, and does it
// notice when somebody edits the file — including a file that is only reached
// through an `@` import, which is not a candidate and so is invisible to the
// candidate list alone.
//
// SessionManager.ApplyPersona rather than ScanAndUpdate, deliberately.
// ScanAndUpdate builds OrbWindows and needs an Avalonia application, which is
// why SessionScanTests lives in tests/UiTests — and there is an end-to-end
// scan-to-registry case there for exactly that reason. Everything ApplyPersona
// does is filesystem, so it belongs here, where the filesystem is real and no
// window has to exist for it to run.
//
// Not [Collection("Settings")]: nothing here reads a setting. LocalPersonas is
// process-wide, so every case uses its own session id and empties the registry
// afterwards.
public class LocalPersonaScanTests : IDisposable
{
    private readonly string _project =
        Path.Combine(Path.GetTempPath(), "cb-persona-scan-" + Guid.NewGuid());

    private readonly string _statusDir =
        Path.Combine(Path.GetTempPath(), "cb-persona-status-" + Guid.NewGuid());

    private readonly string _sessionId = "persona-scan-" + Guid.NewGuid();

    public LocalPersonaScanTests()
    {
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(_statusDir);
    }

    public void Dispose()
    {
        LocalPersonas.Forget(_sessionId);
        try { Directory.Delete(_project, recursive: true); } catch { }
        try { Directory.Delete(_statusDir, recursive: true); } catch { }
    }

    private void WriteMarkdown(string name, string body) =>
        File.WriteAllText(Path.Combine(_project, name), body);

    private SessionStatus Status(SessionSource source = SessionSource.ClaudeCode) => new()
    {
        Source = source,
        State = "idle",
        Cwd = _project,
        Title = "cb-persona-scan",
    };

    // One dictionary per pass, the same way ScanAndUpdate makes one per tick.
    private Dictionary<(string Cwd, SessionSource Source), IReadOnlyList<string>> Pass() => new();

    private SessionManager Manager() => new(_statusDir);

    // The whole point, end to end through the method the timer calls: a
    // sentence in a file on disk becomes a name in the registry the orb reads.
    [Fact]
    public void AProjectsClaudeMdPutsItsPersonaInTheRegistry()
    {
        WriteMarkdown("CLAUDE.md", "# Notes\n\nHer name is Leota.\n");

        Manager().ApplyPersona(_sessionId, Status(), Pass());

        var persona = LocalPersonas.For(_sessionId);
        Assert.NotNull(persona);
        Assert.Equal("Leota", persona!.Name);
        Assert.Contains(Path.Combine(_project, "CLAUDE.md"), persona.Files.Select(Path.GetFullPath));
    }

    // The cache, stated as the thing it actually promises. Not "it is fast" —
    // that is unassertable — but "the second pass did not resolve again", which
    // is visible as the very same Persona object still being in the registry.
    // A rebuild would put an equal-but-different one there, and would also make
    // LocalPersonas.Set evict a decoded portrait on every tick forever.
    [Fact]
    public void ASecondPassOverUnchangedFilesDoesNotResolveAgain()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var manager = Manager();
        var pass = Pass();

        manager.ApplyPersona(_sessionId, Status(), pass);
        var first = LocalPersonas.For(_sessionId);

        manager.ApplyPersona(_sessionId, Status(), pass);
        var second = LocalPersonas.For(_sessionId);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    // An edit is picked up on the next pass. The replacement name is a
    // different length as well as a different word, so this does not depend on
    // the filesystem's timestamp resolution being finer than the gap between
    // two statements — a same-length rewrite inside one mtime tick is a real
    // hole, and one this test would silently stop covering if it relied on the
    // timestamp alone.
    [Fact]
    public void EditingTheMarkdownIsPickedUpOnTheNextPass()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var manager = Manager();
        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Equal("Leota", LocalPersonas.For(_sessionId)!.Name);

        WriteMarkdown("CLAUDE.md", "Her name is Constance.\n");
        manager.ApplyPersona(_sessionId, Status(), Pass());

        Assert.Equal("Constance", LocalPersonas.For(_sessionId)!.Name);
    }

    // The case the candidate list cannot see on its own. `persona.md` is not a
    // candidate — no directory walk would ever name it — and it is only read
    // because the CLAUDE.md imports it. Watching the candidates alone would
    // leave an edit here invisible until something else in the tree moved,
    // which is why the signature is taken over the files the last read actually
    // followed as well.
    [Fact]
    public void EditingAnImportedFileIsPickedUpToo()
    {
        WriteMarkdown("CLAUDE.md", "# Notes\n\n@persona.md\n");
        WriteMarkdown("persona.md", "Her name is Leota.\n");

        var manager = Manager();
        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Equal("Leota", LocalPersonas.For(_sessionId)!.Name);

        WriteMarkdown("persona.md", "Her name is Constance.\n");
        manager.ApplyPersona(_sessionId, Status(), Pass());

        Assert.Equal("Constance", LocalPersonas.For(_sessionId)!.Name);
    }

    // ...and having noticed the import once, it settles: an unchanged pass
    // after an imported file has been read must not resolve again either. This
    // is the arm that catches the off-by-one in *what* is being compared — the
    // first pass has no previous read to take imports from, so its signature is
    // over the candidates alone, and storing that would make every second pass
    // differ from every first one, forever.
    [Fact]
    public void AnImportedFileSettlesRatherThanResolvingEveryPass()
    {
        WriteMarkdown("CLAUDE.md", "@persona.md\n");
        WriteMarkdown("persona.md", "Her name is Leota.\n");

        var manager = Manager();

        manager.ApplyPersona(_sessionId, Status(), Pass());
        var first = LocalPersonas.For(_sessionId);

        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(first, LocalPersonas.For(_sessionId));

        manager.ApplyPersona(_sessionId, Status(), Pass());
        Assert.Same(first, LocalPersonas.For(_sessionId));
    }

    // A picture beside the markdown reaches the registry as bytes. The reader's
    // refusals are LocalPersonaFilesTests' subject; what this adds is that the
    // scan asks for one at all.
    [Fact]
    public void APictureBesideTheMarkdownReachesTheRegistry()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\nHer profile picture is leota.png.\n");
        File.WriteAllBytes(Path.Combine(_project, "leota.png"), new byte[] { 1, 2, 3, 4 });

        Manager().ApplyPersona(_sessionId, Status(), Pass());

        var persona = LocalPersonas.For(_sessionId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, persona!.Avatar);
    }

    // A gateway session is never asked. Its identity is the gateway's, and its
    // Cwd — when it has one at all — is wherever this app was started from, so
    // reading a persona out of it would put the developer's own repository name
    // on somebody else's orb.
    [Fact]
    public void AGatewaySessionIsNotGivenAPersona()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        Manager().ApplyPersona(_sessionId, Status(SessionSource.OpenClaw), Pass());

        Assert.Null(LocalPersonas.For(_sessionId));
    }

    // Neither is a session with nowhere to look.
    [Fact]
    public void ASessionWithNoWorkingDirectoryIsNotGivenAPersona()
    {
        var status = Status();
        status.Cwd = "";

        Manager().ApplyPersona(_sessionId, status, Pass());

        Assert.Null(LocalPersonas.For(_sessionId));
    }

    // A project that says nothing about a persona still gets an entry, and the
    // entry is empty rather than absent. That is what makes the second pass
    // cheap for the overwhelming majority of sessions, which have no persona at
    // all: without a stored signature there is nothing to compare against and
    // every tick would walk the tree again.
    [Fact]
    public void AProjectWithNoPersonaIsRecordedAsHavingNone()
    {
        WriteMarkdown("CLAUDE.md", "# Notes\n\nThe name is derived from the folder.\n");

        var manager = Manager();
        var pass = Pass();

        manager.ApplyPersona(_sessionId, Status(), pass);
        var first = LocalPersonas.For(_sessionId);

        Assert.NotNull(first);
        Assert.True(first!.IsEmpty);

        manager.ApplyPersona(_sessionId, Status(), pass);
        Assert.Same(first, LocalPersonas.For(_sessionId));
    }

    // Two sessions in one repository resolve the same files and each gets its
    // own registry entry — the candidate list is shared for the pass, the
    // registry entry is not. A cache keyed by directory rather than by session
    // would leave the second orb nameless.
    [Fact]
    public void TwoSessionsInOneRepositoryEachGetTheirOwnEntry()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var other = _sessionId + "-second";
        try
        {
            var manager = Manager();
            var pass = Pass();

            manager.ApplyPersona(_sessionId, Status(), pass);
            manager.ApplyPersona(other, Status(), pass);

            Assert.Equal("Leota", LocalPersonas.For(_sessionId)!.Name);
            Assert.Equal("Leota", LocalPersonas.For(other)!.Name);

            // One candidate list built, not two: the second session's cwd and
            // source are the first's.
            Assert.Single(pass);
        }
        finally
        {
            LocalPersonas.Forget(other);
        }
    }

    // Codex reads the directory walk and not the user-level config directories,
    // so its candidate list is a different one and must not be served out of
    // the entry Claude Code's pass made. Asserted on the pass dictionary rather
    // than on the outcome, because in a tree with no user-level file the two
    // lists would happen to produce the same persona and the bug would not
    // show.
    [Fact]
    public void ClaudeCodeAndCodexInOneRepositoryDoNotShareACandidateList()
    {
        WriteMarkdown("CLAUDE.md", "Her name is Leota.\n");

        var codex = _sessionId + "-codex";
        try
        {
            var manager = Manager();
            var pass = Pass();

            manager.ApplyPersona(_sessionId, Status(), pass);
            manager.ApplyPersona(codex, Status(SessionSource.Codex), pass);

            Assert.Equal(2, pass.Count);
            Assert.Equal("Leota", LocalPersonas.For(codex)!.Name);
        }
        finally
        {
            LocalPersonas.Forget(codex);
        }
    }
}
