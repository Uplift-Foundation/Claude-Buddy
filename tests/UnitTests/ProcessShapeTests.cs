using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ClaudeBuddy.Tests;

// What this app reads off a live process: whether a session belongs to an agent
// team (AgentTeam), and the shape both platforms' Claude Desktop scans agree on
// (ClaudeInstance).
//
// AgentTeam's answer comes from the kernel — KERN_PROCARGS2 on macOS, a WMI
// Win32_Process query on Windows — and the pid it is asked about here is this
// test process's own, which is the one pid on the machine that is certainly
// alive and certainly ours to inspect. Neither read spawns anything: the macOS
// path is a sysctl, and the Windows path is a query against a WMI provider that
// is already running.
//
// The cache is the part worth testing properly. It exists because the scan asks
// this once per session every two seconds, and it has a deliberate safety valve
// — a minute — because a pid can be recycled, and a cache with no expiry would
// let a recycled one pin a wrong answer for the life of the app. That is exactly
// the failure that draws an arrow from one team's member to an unrelated
// session's orb, and it cannot be observed by reading a return value once.
public class ProcessShapeTests
{
    private const long CacheMs = 60_000;

    private static Dictionary<int, (AgentTeam.Membership Value, long Stamp)> Cache() =>
        (Dictionary<int, (AgentTeam.Membership, long)>)typeof(AgentTeam)
            .GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    // The cache is process-wide static state, so anything left in it is state
    // the next test in this assembly inherits. Emptied around every case rather
    // than after, so a case also cannot be affected by one that ran before it.
    private static void WithEmptyCache(Action body)
    {
        var cache = Cache();
        lock (cache) cache.Clear();
        try
        {
            body();
        }
        finally
        {
            lock (cache) cache.Clear();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void APidThatIsNotAPidIsAnsweredWithoutAskingTheKernel(int pid)
    {
        WithEmptyCache(() =>
        {
            // AgentTeam.None rather than default(Membership): empty strings,
            // not nulls, because this value is assigned straight onto
            // SessionStatus.Lead.
            Assert.Equal(AgentTeam.None, AgentTeam.Of(pid));
            Assert.Equal("", AgentTeam.LeadOf(pid));
            Assert.NotEqual(default, AgentTeam.Of(pid));

            // Not cached either — there is nothing to remember, and caching it
            // would spend an entry on every hook older than the session_pid
            // field.
            Assert.Empty(Cache());
        });
    }

    [Fact]
    public void AnOrdinaryProcessIsNotInATeamAndIsRememberedAsFirmlyAsOneThatIs()
    {
        // "An empty Lead means 'not a team member', which is the answer for
        // almost every session and is cached just as firmly as a real one — the
        // point is to ask the kernel once per session, not once per scan." This
        // process carries no --parent-session-id, so it is that answer.
        WithEmptyCache(() =>
        {
            var membership = AgentTeam.Of(Environment.ProcessId);

            Assert.Equal("", membership.Lead);
            Assert.Equal("", membership.Color);
            Assert.Equal("", AgentTeam.LeadOf(Environment.ProcessId));

            Assert.True(Cache().ContainsKey(Environment.ProcessId));
        });
    }

    [Fact]
    public void AFreshCacheEntryIsTrustedOverAskingTheProcessAgain()
    {
        // Proved by seeding an answer this process could not possibly give: if
        // Of returns it, the kernel was not consulted.
        WithEmptyCache(() =>
        {
            var invented = new AgentTeam.Membership("lead-session-id", "blue", "MenuUX");
            var cache = Cache();
            lock (cache) cache[Environment.ProcessId] = (invented, Environment.TickCount64);

            Assert.Equal(invented, AgentTeam.Of(Environment.ProcessId));
            Assert.Equal("lead-session-id", AgentTeam.LeadOf(Environment.ProcessId));
        });
    }

    [Fact]
    public void AnEntryOlderThanAMinuteIsReReadSoARecycledPidCannotPinAWrongAnswer()
    {
        // The safety valve. Same seeded answer as above, but stamped over a
        // minute ago — this time the real process wins, and the real process is
        // in no team.
        WithEmptyCache(() =>
        {
            var invented = new AgentTeam.Membership("lead-session-id", "blue", "MenuUX");
            var cache = Cache();
            lock (cache)
            {
                cache[Environment.ProcessId] = (invented, Environment.TickCount64 - CacheMs - 1);
            }

            Assert.Equal("", AgentTeam.Of(Environment.ProcessId).Lead);
        });
    }

    [Fact]
    public void TheCacheIsPrunedOfStaleEntriesOnceItGrowsPastItsLimit()
    {
        // "Sessions come and go all day; without this the map grows for as long
        // as the app runs." Pruning keeps the fresh entries — it is a cleanup,
        // not a flush, and dropping a live session's answer would put the
        // kernel back on the two-second path for every orb on screen.
        WithEmptyCache(() =>
        {
            var cache = Cache();
            var now = Environment.TickCount64;

            lock (cache)
            {
                // Pids well clear of anything real, and clear of this process's
                // own, so the lookup below is a genuine miss.
                for (var i = 0; i < 200; i++)
                {
                    cache[1_000_000 + i] = (default, now - CacheMs - 1);   // stale
                }

                for (var i = 0; i < 100; i++)
                {
                    cache[2_000_000 + i] = (default, now);                 // fresh
                }
            }

            Assert.True(cache.Count > 256);

            AgentTeam.Of(Environment.ProcessId);

            Assert.DoesNotContain(1_000_000, cache.Keys);
            Assert.Equal(100, cache.Keys.Count(pid => pid >= 2_000_000 && pid < 2_000_100));
            Assert.True(cache.ContainsKey(Environment.ProcessId));
        });
    }

    // --- OfAll: every pid in one read (CB-212) ---------------------------------
    //
    // On Windows one read is a WMI query costing about 200 ms whether it names
    // one pid or ten, so the scan asks about every pid at once. The read is
    // handed over here: these pids are invented, and the point is to count
    // what is asked.

    private static Dictionary<string, string> Args(params (string Flag, string Value)[] flags) =>
        flags.ToDictionary(f => f.Flag, f => f.Value, StringComparer.Ordinal);

    [Fact]
    public void EveryMissIsReadInOneCallAndOnlyTheMissesAreRead()
    {
        WithEmptyCache(() =>
        {
            var cached = new AgentTeam.Membership("cached-lead", "red", "Cached");
            var cache = Cache();
            lock (cache) cache[1_000_001] = (cached, Environment.TickCount64);

            var reads = new List<IReadOnlyList<int>>();
            var answers = AgentTeam.OfAll(
                // A duplicate, two non-pids and the cached one, around two misses.
                new[] { 1_000_002, 0, 1_000_001, -4, 1_000_003, 1_000_002 },
                pids =>
                {
                    reads.Add(pids);
                    return new Dictionary<int, Dictionary<string, string>>
                    {
                        [1_000_002] = Args(("--parent-session-id", "lead-1"), ("--agent-color", "blue"),
                            ("--agent-name", "MenuUX")),
                    };
                });

            Assert.Equal(new[] { 1_000_002, 1_000_003 }, Assert.Single(reads));
            Assert.Equal(cached, answers[1_000_001]);
            Assert.Equal(new AgentTeam.Membership("lead-1", "blue", "MenuUX"), answers[1_000_002]);

            // A pid the read had nothing for — gone, or not ours to query — is
            // "no team known", and remembered like any other answer.
            Assert.Equal(AgentTeam.None, answers[1_000_003]);
            Assert.Equal(AgentTeam.None, Cache()[1_000_003].Value);

            // Non-pids get no answer at all, and no cache entry.
            Assert.Equal(3, answers.Count);
            Assert.False(Cache().ContainsKey(0));
        });
    }

    [Fact]
    public void AllHitsMakeNoReadAtAll()
    {
        WithEmptyCache(() =>
        {
            var cache = Cache();
            lock (cache) cache[1_000_001] = (AgentTeam.None, Environment.TickCount64);

            var answers = AgentTeam.OfAll(new[] { 1_000_001, 0 },
                _ => throw new InvalidOperationException("nothing needed reading"));

            Assert.Equal(AgentTeam.None, Assert.Single(answers).Value);
        });
    }

    [Fact]
    public void ABatchedReadIsCachedSoTheNextPassAsksNothing()
    {
        WithEmptyCache(() =>
        {
            var reads = 0;
            IReadOnlyDictionary<int, Dictionary<string, string>> Read(IReadOnlyList<int> pids)
            {
                reads++;
                return pids.ToDictionary(p => p, _ => Args(("--parent-session-id", "lead-1")));
            }

            AgentTeam.OfAll(new[] { 1_000_001, 1_000_002 }, Read);
            var second = AgentTeam.OfAll(new[] { 1_000_002, 1_000_001 }, Read);

            Assert.Equal(1, reads);
            Assert.All(second.Values, m => Assert.Equal("lead-1", m.Lead));
        });
    }

    [Fact]
    public void WhatABatchedReadReturnsIsSanitizedLikeASingleOne()
    {
        WithEmptyCache(() =>
        {
            var answers = AgentTeam.OfAll(new[] { 1_000_001 }, _ =>
                new Dictionary<int, Dictionary<string, string>>
                {
                    [1_000_001] = Args(("--parent-session-id", "not;a;session"), ("--agent-name", "  Menu<UX>  ")),
                });

            Assert.Equal(new AgentTeam.Membership("", "", "MenuUX"), answers[1_000_001]);
        });
    }

    [Fact]
    public void TheEntryPointWithNoReaderReadsThisProcessForReal()
    {
        // The overload the scan uses, against the one pid certainly alive and
        // certainly ours: a real WMI query on the Windows leg, a sysctl on the
        // Mac one. This process is in no team.
        WithEmptyCache(() =>
        {
            var answers = AgentTeam.OfAll(new[] { Environment.ProcessId });

            Assert.Equal(AgentTeam.None, answers[Environment.ProcessId]);
            Assert.True(Cache().ContainsKey(Environment.ProcessId));
        });
    }

    // --- reading a Windows command line ----------------------------------------

    [Fact]
    public void AMemberCommandLineYieldsAllThreeFlags()
    {
        var args = AgentTeam.ArgumentsIn(
            "\"C:\\Program Files\\claude\\claude.exe\" --agent-id CatAudio@session-6a6fcb43 " +
            "--agent-name CatAudio --team-name session-6a6fcb43 --agent-color=blue " +
            "--parent-session-id \"6a6fcb43-fa28-4894-9940-c1c6c9970e54\"");

        Assert.Equal("6a6fcb43-fa28-4894-9940-c1c6c9970e54", args["--parent-session-id"]);
        Assert.Equal("blue", args["--agent-color"]);
        Assert.Equal("CatAudio", args["--agent-name"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("claude.exe --resume")]
    public void ACommandLineWithoutTheFlagsYieldsNothing(string? command) =>
        Assert.Empty(AgentTeam.ArgumentsIn(command));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(AgentTeam.PidsPerQuery, 1)]
    [InlineData(AgentTeam.PidsPerQuery + 1, 2)]
    public void PidsAreAskedAboutABoundedNumberAtATime(int pids, int queries)
    {
        var all = Enumerable.Range(100, pids).ToList();
        var texts = AgentTeam.WindowsQueries(all).ToList();

        Assert.Equal(queries, texts.Count);
        Assert.All(texts, t => Assert.StartsWith(
            "SELECT ProcessId, CommandLine FROM Win32_Process WHERE ProcessId = ", t));

        // Every pid asked about exactly once across the queries.
        var named = texts.SelectMany(t => t.Split(" OR ")).Select(c => int.Parse(c[(c.LastIndexOf(' ') + 1)..]));
        Assert.Equal(all, named);
    }

    // --- ClaudeInstance ------------------------------------------------------

    [Fact]
    public void AClaudeInstanceWithNoUserDataDirMeansNoOverrideWasGiven()
    {
        // The one thing this record says, and the reason it is shared between
        // MacOSProcessScan and WindowsProcessScan rather than each having its
        // own: null is not "unknown", it is "launched without the override" —
        // a Dock or shell launch, or this app's own launch of the Default
        // profile — which is what ClaudeDesktopManager.MapInstances branches on
        // to decide which profile a running window belongs to. A record struct,
        // so two scans of the same process compare equal.
        var launchedFromDock = new ClaudeInstance(4321, null);
        var launchedWithProfile = new ClaudeInstance(4321, "/Users/user/Claude-Work");

        Assert.Null(launchedFromDock.UserDataDir);
        Assert.Equal(4321, launchedFromDock.Pid);
        Assert.Equal(launchedFromDock, new ClaudeInstance(4321, null));
        Assert.NotEqual(launchedFromDock, launchedWithProfile);
    }
}
