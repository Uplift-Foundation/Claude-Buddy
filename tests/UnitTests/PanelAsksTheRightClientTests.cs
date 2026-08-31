using Xunit;

namespace ClaudeBuddy.UnitTests;

// Every question a panel asks about a remote session has to reach the client
// that actually holds the answer.
//
// **This is the bug class that cost the most and showed the least.** There is
// one accessor that decides which client a panel talks to — MirrorClientFor —
// and CB-69 taught it to prefer the direct link. What CB-69 did not do was go
// looking for the callers that went *around* it and read the relay table
// directly. That was the same thing while a relay was the only client there
// was, and stopped being the same thing the moment the link arrived.
//
// The failure has no error in it. StateFor is never asked, so it answers
// Unknown, and the panel says "checking whether a live view is available…" and
// means it — forever, with the roster arriving every ten seconds the whole
// time. It was found by a person clicking an orb.
public class PanelAsksTheRightClientTests
{
    private static RemoteMirrorClient Client(string account) =>
        new(account, new RemoteMirrorClient.Seams((_, _) => Task.FromResult(true)));

    [Fact]
    public void EveryPanelQuestionGoesThroughTheOneAccessor()
    {
        // Asserted as a fact about the source rather than about behaviour,
        // because behaviour cannot see the difference: a caller that reads the
        // relay table directly returns a perfectly well-formed "I don't know".
        //
        // The relay table is a test seam now and nothing in the app writes it,
        // so a direct read outside MirrorClientFor and the seams themselves is
        // a caller that will silently never see the link.
        var source = File.ReadAllLines(SourceOf("RemoteControlSessions.cs"));

        var offenders = source
            .Select((line, i) => (Line: line, Number: i + 1))
            // Only reads that pull a *client* out of the table. That is the bug
            // class: a caller that fetches a client without going through
            // MirrorClientFor cannot see the link. Reading anything else out of
            // the table is a different question with different answers.
            .Where(l => l.Line.Contains("Relays.TryGetValue") && l.Line.Contains("Client"))
            .Where(l => !Within(source, l.Number, "MirrorClientFor")
                        && !Within(source, l.Number, "ForTests"))
            .Select(l => $"{l.Number}: {l.Line.Trim()}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "these read the relay table directly instead of asking MirrorClientFor, "
            + "so they cannot see the peer link:\n  " + string.Join("\n  ", offenders));
    }

    // --- and the behaviour the bug produced ------------------------------------

    [Fact]
    public void AKnownSessionIsAvailableRatherThanUnknown()
    {
        // The shape of what the panel saw: a client that knows the session, and
        // a caller that never asked it.
        var client = Client("acct");

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unknown,
            client.StateFor("never-heard-of-it").Availability);
    }

    [Fact]
    public void NoClientAtAllIsUnknownRatherThanUnavailable()
    {
        // The distinction the panel depends on: Unknown keeps it checking,
        // Unavailable settles it as "no live view". A missing client must not
        // be read as a definite no — that is a different sentence to the user
        // and only one of them is true.
        RemoteControlSessions.ResetForTests();

        var state = RemoteControlSessions.MirrorStateFor("acct", "job-hunter");

        Assert.Equal(RemoteMirrorClient.MirrorAvailability.Unknown, state.Availability);
        Assert.Null(state.Entry);
    }

    private static bool Within(string[] source, int lineNumber, string marker)
    {
        // Walk back to the enclosing member's signature and look for the marker
        // in it. Crude, and enough: the members here are short and named.
        for (var i = lineNumber - 1; i >= 0 && i > lineNumber - 25; i--)
        {
            if (source[i].Contains(marker)) return true;
            if (source[i].TrimStart().StartsWith("internal static")
                || source[i].TrimStart().StartsWith("public static"))
                return source[i].Contains(marker);
        }

        return false;
    }

    private static string SourceOf(string file)
    {
        // Up from the test binary to the repository root. The suites already
        // reach the app by ProjectReference, so the layout is fixed.
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate)) return candidate;

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new FileNotFoundException($"could not find {file} above {AppContext.BaseDirectory}");
    }
}
