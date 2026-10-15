using Xunit;

namespace ClaudeBuddy.Tests;

// CB-266: the stateful half, DesktopBridgeLookup, against fake seams. What it
// has to get right is mostly *when it does not read*: a stat per tick for a
// known record, a re-look at most every few seconds for an unknown one, and a
// re-read exactly when the mtime moves. Counters on the seams prove each.
// Invented ids throughout.
public class DesktopBridgeLookupTests
{
    private const string Host = "local_00000000-aaaa-bbbb-cccc-000000000020";
    private const string Cli = "33333333-4444-5555-6666-777777777777";
    private const string Bridge = "session_01LookupFixtureDDDD";
    private static readonly DateTime T0 = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static string Json(string bridges = "[\"" + Bridge + "\"]", string cli = Cli) =>
        "{\"sessionId\":\"" + Host + "\",\"cliSessionId\":\"" + cli + "\",\"bridgeSessionIds\":" + bridges + "}";

    private static SessionStatus Desktop(string host = Host, string id = Cli) => new()
    {
        Cli = "",
        Entrypoint = SessionStatus.DesktopEntrypoint,
        HostSessionId = host,
        SessionId = id,
    };

    // One fake disk: a single record at a fixed path, which a test can edit,
    // touch or remove, with counters for every seam call.
    private sealed class Disk
    {
        public const string Profile = "profile";
        public static readonly string Records = Path.Combine(Profile, "claude-code-sessions");
        public static readonly string Outer = Path.Combine(Records, "aaa");
        public static readonly string Inner = Path.Combine(Outer, "bbb");
        public static readonly string RecordPath = Path.Combine(Inner, Host + ".json");

        public string? Content;
        public DateTime? Mtime = T0;
        public int Reads, Stats, Lists;

        // Present = the record file exists on the fake disk.
        public bool Present = true;

        public DesktopBridgeLookup Lookup(Func<IEnumerable<string>>? profiles = null) => new(
            profiles ?? (() => new[] { Profile }),
            mtime: path => { Stats++; return path == RecordPath && Present ? Mtime : null; },
            read: path => { Reads++; return path == RecordPath && Present ? Content : null; },
            subdirectories: dir =>
            {
                Lists++;
                return dir == Records ? new[] { Outer } : dir == Outer ? new[] { Inner } : Array.Empty<string>();
            },
            fileExists: path => path == RecordPath && Present,
            files: _ => Present ? new[] { RecordPath } : Array.Empty<string>());
    }

    [Fact]
    public void AKnownRecordIsReadOnceThenOnlyStatted()
    {
        var disk = new Disk { Content = Json() };
        var lookup = disk.Lookup();

        Assert.Equal(Bridge, lookup.BridgeFor(Desktop(), T0));
        Assert.Equal(Bridge, lookup.BridgeFor(Desktop(), T0.AddSeconds(2)));
        Assert.Equal(Bridge, lookup.BridgeFor(Desktop(), T0.AddSeconds(4)));

        Assert.Equal(1, disk.Reads);
        Assert.True(disk.Stats >= 3);
    }

    [Fact]
    public void ABridgeThatAppearsOrVanishesInTheRecordShowsUpWhenItsMtimeMoves()
    {
        var disk = new Disk { Content = Json(bridges: "[]") };
        var lookup = disk.Lookup();

        Assert.Null(lookup.BridgeFor(Desktop(), T0));

        disk.Content = Json();
        disk.Mtime = T0.AddSeconds(30);
        Assert.Equal(Bridge, lookup.BridgeFor(Desktop(), T0.AddSeconds(31)));

        disk.Content = Json(bridges: "[]");
        disk.Mtime = T0.AddSeconds(60);
        Assert.Null(lookup.BridgeFor(Desktop(), T0.AddSeconds(61)));
        Assert.Equal(3, disk.Reads);
    }

    [Fact]
    public void ARecordThatDisappearsIsLookedForAgainRatherThanTrustedForever()
    {
        var disk = new Disk { Content = Json() };
        var lookup = disk.Lookup();
        Assert.Equal(Bridge, lookup.BridgeFor(Desktop(), T0));

        disk.Present = false;
        Assert.Null(lookup.BridgeFor(Desktop(), T0.AddSeconds(2)));

        disk.Present = true;
        Assert.Equal(Bridge, lookup.BridgeFor(Desktop(), T0.AddSeconds(20)));
    }

    [Fact]
    public void AnUnknownRecordIsNotGlobbedAgainUntilTheRelocateIntervalHasPassed()
    {
        var disk = new Disk { Present = false };
        var lookup = disk.Lookup();

        Assert.Null(lookup.BridgeFor(Desktop(), T0));
        var listed = disk.Lists;
        Assert.True(listed > 0);

        Assert.Null(lookup.BridgeFor(Desktop(), T0.AddSeconds(2)));
        Assert.Null(lookup.BridgeFor(Desktop(), T0.AddSeconds(4)));
        Assert.Equal(listed, disk.Lists);

        // The record appears (Desktop wrote it after the hook did).
        disk.Present = true;
        disk.Content = Json();
        Assert.Null(lookup.BridgeFor(Desktop(), T0.AddSeconds(4.9)));
        Assert.Equal(Bridge, lookup.BridgeFor(Desktop(), T0.Add(DesktopBridgeLookup.RelocateAfter).AddSeconds(1)));
    }

    [Fact]
    public void AStatusWithNoHostIdFallsBackToTheRecordNamingItsCliSession()
    {
        var disk = new Disk { Content = Json() };

        Assert.Equal(Bridge, disk.Lookup().BridgeFor(Desktop(host: ""), T0));
        Assert.Equal(Bridge, disk.Lookup().BridgeFor(Desktop(host: "../../etc"), T0));
    }

    [Fact]
    public void ARecordForAnotherCliSessionAnArchivedOneOrNoProfilesGiveNoBridge()
    {
        var other = new Disk { Content = Json(cli: "someone-else") };
        Assert.Null(other.Lookup().BridgeFor(Desktop(), T0));

        var archived = new Disk { Content = Json().Replace("}", ",\"isArchived\":true}") };
        Assert.Null(archived.Lookup().BridgeFor(Desktop(), T0));

        var none = new Disk { Content = Json() };
        Assert.Null(none.Lookup(() => Array.Empty<string>()).BridgeFor(Desktop(), T0));
    }

    [Fact]
    public void OnlyADesktopChatWithASessionIdIsEverLookedUp()
    {
        var disk = new Disk { Content = Json() };
        var lookup = disk.Lookup();

        var terminal = Desktop();
        terminal.Entrypoint = "cli";
        Assert.Null(lookup.BridgeFor(terminal, T0));
        Assert.Null(lookup.BridgeFor(Desktop(id: ""), T0));

        Assert.Equal(0, disk.Reads + disk.Stats + disk.Lists);
    }
}
