using Xunit;

namespace ClaudeBuddy.Tests;

// PersonaFiles.CandidateRoots — CB-147's D8, in isolation and with no
// filesystem at all: which roots a picture is tried against, in which order,
// and the dedupe that keeps the overwhelmingly common case (a workspace root
// that is itself the directory of the markdown file) down to one root rather
// than two. The guard chain that actually walks these roots is covered
// against a real tree in PersonaWorkspaceRootFileTests and
// LocalPersonaFilesTests; this is only the decision of *which* roots to hand
// it, which is pure and needs no disk to prove.
public class PersonaAvatarRootsTests
{
    [Fact]
    public void TwoDistinctDirectoriesGiveBothRootsFileDirectoryFirst()
    {
        var roots = PersonaFiles.CandidateRoots("/ws/.claude/persona", "/ws");

        Assert.Equal(new[] { "/ws/.claude/persona", "/ws" }, roots);
    }

    // The overwhelmingly common shape: a CLAUDE.md sitting directly in the
    // session's cwd, so the file's own directory and the workspace root are
    // the same string. One root, not two — trying the same guard chain twice
    // would cost nothing correctness-wise but would risk the two-root log
    // wording firing for a resolve that only ever had one place to look.
    [Fact]
    public void TheSameDirectoryTwiceDedupesToOneRoot()
    {
        var roots = PersonaFiles.CandidateRoots("/ws/project", "/ws/project");

        Assert.Equal(new[] { "/ws/project" }, roots);
    }

    // A trailing separator is not a different directory — Trim is what
    // IsWithin itself already normalises through, and CandidateRoots has to
    // agree with it or the dedupe could miss the common case on a value that
    // merely differs by a trailing slash.
    [Fact]
    public void ATrailingSeparatorDoesNotDefeatTheDedupe()
    {
        var withSeparator = "/ws/project" + Path.DirectorySeparatorChar;

        var roots = PersonaFiles.CandidateRoots("/ws/project", withSeparator);

        Assert.Equal(new[] { "/ws/project" }, roots);
    }

    // A null workspace root — no cwd, or one that did not canonicalise — is
    // exactly one candidate root, which is D8's required degenerate case:
    // byte-identical behaviour to before this ticket.
    [Fact]
    public void ANullWorkspaceRootIsJustTheFileDirectory()
    {
        var roots = PersonaFiles.CandidateRoots("/ws/.claude/persona", null);

        Assert.Equal(new[] { "/ws/.claude/persona" }, roots);
    }

    // Case sensitivity is deliberately not folded in here — unlike
    // LocalPersona.CandidateFiles' OrdinalIgnoreCase for Windows paths, a
    // root is already whatever PersonaFiles.CanonicalDirectory or
    // Path.GetDirectoryName produced, and those preserve the filesystem's own
    // casing rather than a user's typed one, so an ordinal compare is the
    // right one here — consistent with IsWithin and Trim, which this method's
    // own comment says it has to agree with.
    [Fact]
    public void CaseIsNotFoldedTheWayLocalPersonaCandidateFilesFoldsIt()
    {
        var roots = PersonaFiles.CandidateRoots("/ws/project", "/WS/PROJECT");

        Assert.Equal(new[] { "/ws/project", "/WS/PROJECT" }, roots);
    }

    // PersonaFiles.Rank — D5's ranking, asserted as the ordering contract it
    // actually is rather than as three hardcoded integers: AvatarAt only ever
    // asks "did this rejection get strictly further than the best one so
    // far", so what matters is the relative order, not the literal numbers,
    // and a test pinned to 0/1/2/-1 would turn a harmless renumbering into a
    // false failure. Unreadable means nothing was there at all; EscapesRoot
    // means something real was found outside the root; TooLarge means the
    // file was found and measured — each one a stronger claim about the
    // world than the last, hence the order.
    //
    // NotAPicturePath is included even though it can never actually reach
    // Rank in production (it is decided in RejectUnusableValue before any
    // root is tried) — its arm exists only to keep the switch exhaustive,
    // and the contract that matters for it is purely negative: it must never
    // be able to win AvatarAt's "got strictly further" comparison against a
    // real outcome, which is what ranking it below all three asserts.
    [Fact]
    public void EveryRejectionRanksBelowTheOneThatProvesMoreAboutTheWorld()
    {
        var unreadable = PersonaFiles.Rank(PersonaFiles.AvatarRejection.Unreadable);
        var escapesRoot = PersonaFiles.Rank(PersonaFiles.AvatarRejection.EscapesRoot);
        var tooLarge = PersonaFiles.Rank(PersonaFiles.AvatarRejection.TooLarge);
        var notAPicturePath = PersonaFiles.Rank(PersonaFiles.AvatarRejection.NotAPicturePath);

        Assert.True(unreadable < escapesRoot);
        Assert.True(escapesRoot < tooLarge);

        // Below every real outcome, not merely below one of them — otherwise
        // a future reordering of the other three could leave it able to
        // outrank the weakest of them.
        Assert.True(notAPicturePath < unreadable);
        Assert.True(notAPicturePath < escapesRoot);
        Assert.True(notAPicturePath < tooLarge);
    }
}
