using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Six decisions that were only reachable from the machine the tests happened to
// be running on, or from a platform CI only half covers, until each was given
// an argument instead of a question.
//
// They have nothing in common except that: a rule that reads its own
// environment cannot be asserted, only observed. Grouped here rather than
// scattered because the pattern is the point — see each case's own note for
// what was being read and what is now passed in.
public class LastReachableArmsTests
{
    // ---- Codex's prompts directory -----------------------------------------

    // ForCodex used to read ~/.codex/prompts off the real home directory, so
    // whether this arm ran at all depended on whether the person running the
    // tests keeps prompts there. That is not a test of this code.
    [Fact]
    public void ACodexPromptBecomesAPrefixedCommand()
    {
        var home = TempHome();
        var prompts = Directory.CreateDirectory(Path.Combine(home, ".codex", "prompts"));
        File.WriteAllText(Path.Combine(prompts.FullName, "standup.md"), "Write the standup note.\n");

        var commands = SlashCommandCatalog.ForCodex(home);

        var standup = Assert.Single(commands, c => c.Name == "/prompts:standup");
        Assert.Equal("Write the standup note.", standup.Description);
    }

    // "/prompts:<name>", never a bare "/<name>" — Codex's own docs are explicit
    // about that, and a catalogue that offered the bare form would be offering
    // something the CLI does not accept.
    [Fact]
    public void APromptIsNeverOfferedUnderItsBareName()
    {
        var home = TempHome();
        Directory.CreateDirectory(Path.Combine(home, ".codex", "prompts"));
        File.WriteAllText(Path.Combine(home, ".codex", "prompts", "standup.md"), "x");

        var names = SlashCommandCatalog.ForCodex(home).Select(c => c.Name).ToList();

        Assert.Contains("/prompts:standup", names);
        Assert.DoesNotContain("/standup", names);
    }

    // Top-level files only. Codex's docs say it "scans only the top-level
    // Markdown files", so a prompt filed in a subdirectory is not a command,
    // and offering it would be offering something that does not exist.
    [Fact]
    public void APromptInASubdirectoryIsNotACommand()
    {
        var home = TempHome();
        var nested = Directory.CreateDirectory(
            Path.Combine(home, ".codex", "prompts", "archive"));
        File.WriteAllText(Path.Combine(nested.FullName, "old.md"), "x");

        Assert.DoesNotContain(SlashCommandCatalog.ForCodex(home),
            c => c.Name.Contains("old", StringComparison.Ordinal));
    }

    // A home with no prompts directory at all still produces the built-ins
    // rather than nothing — the ordinary case for anyone who has never written
    // one.
    [Fact]
    public void AHomeWithNoPromptsStillOffersTheBuiltIns()
    {
        Assert.NotEmpty(SlashCommandCatalog.ForCodex(TempHome()));
    }

    // A prompt named after a built-in replaces it, because it is what actually
    // runs. The merge order is what decides this, and it is the same rule the
    // Claude Code side follows for a custom command shadowing a built-in.
    [Fact]
    public void APromptCannotBeShadowedByABuiltInBecauseItIsMergedLast()
    {
        var home = TempHome();
        Directory.CreateDirectory(Path.Combine(home, ".codex", "prompts"));

        var builtin = SlashCommandCatalog.ForCodex(home).First().Name;
        File.WriteAllText(
            Path.Combine(home, ".codex", "prompts",
                builtin.TrimStart('/').Replace(":", "-") + ".md"),
            "mine\n");

        // Named "/prompts:<file>", so it cannot collide with a built-in at all
        // — which is the answer, and worth asserting rather than assuming: the
        // prefix is what makes the two namespaces separate.
        Assert.Contains(SlashCommandCatalog.ForCodex(home), c => c.Name == builtin);
    }

    private static string TempHome()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "cb-codex-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- where a profile's logs are ----------------------------------------

    // Two genuinely different rules rather than two spellings of one path, and
    // until the platform became an argument only one CI leg ever executed
    // either. A rule only one runner reaches is a rule nobody reads until it is
    // wrong.
    [Fact]
    public void OnWindowsThereIsOneLogDirectory()
    {
        var candidates = ClaudeDesktopManager
            .LogCandidates(@"C:\Users\x\AppData\Roaming\Claude-Work", windows: true, Never)
            .ToList();

        Assert.Single(candidates);
        Assert.EndsWith("logs", candidates[0]);
    }

    // Electron's userData resolves the same way on Windows whether or not
    // --user-data-dir was passed, so there was never a Default/created split
    // there — and that arm is the one that turned out to be right about macOS
    // too. It is asserted here as the invariant it always was: the answer
    // depends on the profile directory and on nothing else.
    [Fact]
    public void OnWindowsTheAnswerDependsOnlyOnTheProfileDirectory()
    {
        var once = ClaudeDesktopManager
            .LogCandidates(@"C:\Users\x\AppData\Roaming\Claude", windows: true, Never);
        var again = ClaudeDesktopManager
            .LogCandidates(@"C:\Users\x\AppData\Roaming\Claude", windows: true, Recent);

        Assert.Equal(once, again);
    }

    // macOS no longer has a Default/created split either, and this is the test
    // that used to assert the bug.
    //
    // It read: a created profile's logs are at <profile>/Logs, full stop. That
    // was only ever true because CLAUDE_USER_DATA_DIR made it true — Claude
    // Desktop's own startup called app.setPath("logs", …) inside the variable's
    // branch. --user-data-dir sets Chromium's userData and nothing else, so on
    // a current build the logs stay at ~/Library/Logs/Claude and the
    // <profile>/Logs left over from when the variable worked is stale. The old
    // assertion passed the whole time and pinned Reveal logs to the stale
    // directory, which is worse than having had no test at all.
    [Fact]
    public void OnMacOsTheLiveLogDirectoryComesFirstWhereverItIs()
    {
        var electron = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "Claude");
        var inside = Path.Combine("/tmp/Claude-Work", "Logs");

        // A current build: the switch moved the data, Electron kept the logs.
        var current = ClaudeDesktopManager.LogCandidates(
            "/tmp/Claude-Work", windows: false,
            path => path == electron ? Recent(path) : Stale(path));

        Assert.Equal(new[] { electron, inside, "/tmp/Claude-Work" }, current);

        // An older build that still honours the variable, which is why this app
        // still sends it. Same list, ordered by the same evidence, no opinion
        // about which build is installed.
        var older = ClaudeDesktopManager.LogCandidates(
            "/tmp/Claude-Work", windows: false,
            path => path == inside ? Recent(path) : Stale(path));

        Assert.Equal(new[] { inside, electron, "/tmp/Claude-Work" }, older);
    }

    // The profile directory is last however the clock falls, and deliberately
    // outside the comparison: Chromium writes Cookies and Local Storage into it
    // continuously, so ranking it by mtime would make it win every time and
    // Reveal logs would stop revealing logs.
    [Fact]
    public void TheProfileDirectoryIsAlwaysTheLastResort()
    {
        var candidates = ClaudeDesktopManager.LogCandidates(
            "/tmp/Claude-Work", windows: false, _ => DateTime.UtcNow);

        Assert.Equal("/tmp/Claude-Work", candidates[^1]);
        Assert.Equal(3, candidates.Count);
    }

    // Neither log directory has ever been written to — a profile that has not
    // run yet. Nothing to order on, so the static preference stands, and
    // Electron's path leads because that is where a current build will write.
    [Fact]
    public void WithNothingWrittenTheStaticPreferenceStands()
    {
        var candidates = ClaudeDesktopManager.LogCandidates(
            "/tmp/Claude-Work", windows: false, Never);

        Assert.Contains("Library", candidates[0]);
        Assert.Contains("Logs", candidates[0]);
        Assert.Equal(Path.Combine("/tmp/Claude-Work", "Logs"), candidates[1]);
    }

    // ---- ByRecency ---------------------------------------------------------

    [Fact]
    public void ByRecencyPutsTheMostRecentlyWrittenFirst()
    {
        var when = new Dictionary<string, DateTime>
        {
            ["a"] = new(2026, 8, 1),
            ["b"] = new(2026, 8, 25),
            ["c"] = new(2026, 8, 14)
        };

        Assert.Equal(
            new[] { "b", "c", "a" },
            ClaudeDesktopManager.ByRecency(new[] { "a", "b", "c" }, path => when[path]));
    }

    // Unwritten candidates sort behind written ones and keep the order they
    // came in, which is the tie-break the list itself still encodes.
    [Fact]
    public void ByRecencyKeepsTheIncomingOrderForCandidatesWithNoWrites()
    {
        Assert.Equal(
            new[] { "a", "b", "c" },
            ClaudeDesktopManager.ByRecency(new[] { "a", "b", "c" }, Never));

        Assert.Equal(
            new[] { "c", "a", "b" },
            ClaudeDesktopManager.ByRecency(
                new[] { "a", "b", "c" },
                path => path == "c" ? new DateTime(2026, 8, 25) : (DateTime?)null));
    }

    [Fact]
    public void ByRecencyHandlesAnEmptyList()
    {
        Assert.Empty(ClaudeDesktopManager.ByRecency(Array.Empty<string>(), Never));
    }

    // ---- NewestWrite -------------------------------------------------------

    [Fact]
    public void NewestWriteIsNullForADirectoryThatIsNotThere()
    {
        Assert.Null(ClaudeDesktopManager.NewestWrite(
            Path.Combine(Path.GetTempPath(), "cb-absent-" + Guid.NewGuid().ToString("N"))));
    }

    // The file inside, not the directory around it. Appending to main.log does
    // not touch its parent, so a live log directory can carry a much older
    // mtime than its contents — measured on the machine this was found on, and
    // reading the directory's own stamp would have preferred the stale
    // candidate, which is the entire bug.
    [Fact]
    public void NewestWriteReadsTheFilesRatherThanTheDirectory()
    {
        var dir = TempDirectory();
        try
        {
            var log = Path.Combine(dir, "main.log");
            File.WriteAllText(log, "x");

            var future = DateTime.UtcNow.AddHours(1);
            File.SetLastWriteTimeUtc(log, future);
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-30));

            var newest = ClaudeDesktopManager.NewestWrite(dir);

            Assert.NotNull(newest);
            Assert.True(newest > DateTime.UtcNow.AddMinutes(30),
                "the file's stamp should win, not the directory's");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NewestWriteTakesTheNewestOfSeveralFiles()
    {
        var dir = TempDirectory();
        try
        {
            var old = Path.Combine(dir, "old.log");
            var recent = Path.Combine(dir, "recent.log");
            File.WriteAllText(old, "x");
            File.WriteAllText(recent, "y");

            var newest = DateTime.UtcNow.AddHours(1);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(recent, newest);

            Assert.Equal(newest, ClaudeDesktopManager.NewestWrite(dir)!.Value,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Two files stamped identically, which is a log rotation writing both
    // halves inside the same second and is the only way to reach the "not
    // newer" arm on purpose. NewestWriteTakesTheNewestOfSeveralFiles reaches it
    // too, but only when the filesystem happens to enumerate the newest file
    // first — a coin toss, and a coin toss is not coverage.
    [Fact]
    public void FilesWrittenAtTheSameInstantSettleOnThatInstant()
    {
        var dir = TempDirectory();
        try
        {
            var when = DateTime.UtcNow.AddHours(1);
            foreach (var name in new[] { "a.log", "b.log" })
            {
                var file = Path.Combine(dir, name);
                File.WriteAllText(file, "x");
                File.SetLastWriteTimeUtc(file, when);
            }

            Assert.Equal(when, ClaudeDesktopManager.NewestWrite(dir)!.Value,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // An empty directory still beats one that is not there: it is evidence the
    // app created it, which a missing path is not.
    [Fact]
    public void AnEmptyDirectoryFallsBackToItsOwnStamp()
    {
        var dir = TempDirectory();
        try
        {
            Assert.NotNull(ClaudeDesktopManager.NewestWrite(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The catch. A directory that exists and cannot be enumerated would
    // otherwise throw out of a menu click, on a background thread, with nothing
    // to catch it.
    //
    // Unix only for staging reasons, not because the rule is: mode bits are the
    // one portable-enough way to make a real directory unreadable, and Windows
    // needs an ACL edit that a test has no business making. The same
    // split-by-platform pattern BundleCacheLayoutTests uses for its unreadable
    // marker.
    [Fact]
    public void ADirectoryThatCannotBeReadIsTreatedAsUnwritten()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.log"), "x");
            File.SetUnixFileMode(dir, UnixFileMode.None);

            Assert.Null(ClaudeDesktopManager.NewestWrite(dir));
        }
        finally
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DateTime? Never(string path) => null;

    private static DateTime? Recent(string path) => new(2026, 8, 25);

    private static DateTime? Stale(string path) => new(2026, 7, 1);

    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- every colour has a name -------------------------------------------

    // NameFor ends in a fallback that cannot run, and this is why: every colour
    // For() can hand it is in Named. Asserted rather than assumed, because the
    // fallback is excluded from coverage on the strength of exactly this — and
    // an invariant nothing checks is a comment, not a guarantee.
    //
    // The palette is deliberately a copy of OrbWindow.AgentColors' values
    // rather than a reference to them, so the two can drift; this is what
    // catches a drift that leaves a profile with a colour the settings window
    // cannot name.
    [Fact]
    public void EveryColourAProfileCanGetHasAName()
    {
        var named = ClaudeDesktopColors.NamedColours;

        Assert.All(ClaudeDesktopColors.EveryColourAProfileCanGet,
            colour => Assert.Contains(colour, named));
    }

    // ---- a title with no agent in the key ----------------------------------

    // Everything the gateway currently reports has an "agent:…" key, so the two
    // fallbacks below are for a session shape this app does not produce and
    // cannot stop the gateway producing. They are the difference between an orb
    // labelled with something and an orb labelled with a key.
    [Fact]
    public void ASessionWithNoAgentInItsKeyFallsBackToItsOwnLabel()
    {
        Assert.Equal("Standup notes",
            OpenClawSessions.TitleFor(Json("""{"label":"Standup notes"}"""),
                                      Json("{}"), "room:general"));
    }

    // Then origin's label, which is where a conversation that came from
    // somewhere else carries its name.
    [Fact]
    public void WithNoLabelOfItsOwnTheOriginsLabelIsUsed()
    {
        Assert.Equal("#general",
            OpenClawSessions.TitleFor(Json("{}"),
                                      Json("""{"label":"#general"}"""), "room:general"));
    }

    // A blank label in origin is not a name. Without this the orb would be
    // titled with a space.
    [Fact]
    public void ABlankOriginLabelIsNotAName()
    {
        Assert.Equal("room:general",
            OpenClawSessions.TitleFor(Json("{}"),
                                      Json("""{"label":"   "}"""), "room:general"));
    }

    // And with nothing anywhere, the key — which at least identifies the
    // session uniquely, where an empty title identifies nothing.
    [Fact]
    public void WithNothingToGoOnTheKeyIsTheTitle()
    {
        Assert.Equal("room:general",
            OpenClawSessions.TitleFor(Json("{}"), Json("{}"), "room:general"));
    }

    // origin is absent on 12 of the 70 sessions this was measured against, and
    // arrives as an undefined element rather than an object when it is. Reading
    // a property off that is what the ValueKind check in front of it prevents.
    [Fact]
    public void AMissingOriginIsNotReadAsAnObject()
    {
        Assert.Equal("room:general",
            OpenClawSessions.TitleFor(Json("{}"), default, "room:general"));
    }

    private static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // ---- what the composer says --------------------------------------------

    // Whether there is a pane to type into needs a real tmux and a real
    // session, so it is asked at the property and decided here — which is what
    // makes "no pane" something a test can state rather than something the
    // machine happens to be.
    [Fact]
    public void WithNoPaneTheComposerSaysSoRatherThanOfferingToSend()
    {
        Assert.Equal("No pane to type into",
            LocalCliChatSession.ComposerHintFor(canSendQuietly: false, replyEnabled: true));
    }

    // No pane beats replying-off, because it is the more specific answer: the
    // setting is not what is stopping this one.
    [Fact]
    public void NoPaneBeatsReplyingOff()
    {
        Assert.Equal("No pane to type into",
            LocalCliChatSession.ComposerHintFor(canSendQuietly: false, replyEnabled: false));
    }

    [Fact]
    public void WithAPaneTheHintFollowsTheReplySetting()
    {
        Assert.Equal("Message…",
            LocalCliChatSession.ComposerHintFor(canSendQuietly: true, replyEnabled: true));
        Assert.Equal("Replying is off",
            LocalCliChatSession.ComposerHintFor(canSendQuietly: true, replyEnabled: false));
    }

    // ---- the notes a refused send leaves -----------------------------------

    // Two different problems that both end in "nothing was typed", and the note
    // has to say which: a session outside tmux can still be replied to in its
    // own terminal, where a missing tmux binary cannot be worked around at all.
    // Telling someone to go to a terminal that isn't there is the failure this
    // distinction exists to avoid.
    [Fact]
    public void ASessionOutsideTmuxIsToldWhereItCanReply()
    {
        Assert.Contains("Reply in the terminal instead", LocalCliChatSession.NoPaneNote(null));
        Assert.Contains("Reply in the terminal instead", LocalCliChatSession.NoPaneNote(""));
    }

    [Fact]
    public void ASessionInAPaneWithNoTmuxIsToldThatInstead()
    {
        Assert.Equal("Couldn't find tmux to type with.", LocalCliChatSession.NoPaneNote("%12"));
    }

    // Every refusal in this app names the setting that would lift it. A note
    // that says only "no" is a dead end for whoever reads it.
    [Fact]
    public void EveryRefusalNamesTheSettingThatWouldLiftIt()
    {
        Assert.Contains("Settings", LocalCliChatSession.ReplyingOffNote);
        Assert.Contains("Allow replying to sessions", LocalCliChatSession.ReplyingOffNote);

        Assert.Contains("Settings", RemoteControlChatSession.RemoteControlOffNote);
        Assert.Contains("Show sessions from other machines",
            RemoteControlChatSession.RemoteControlOffNote);
    }
}
