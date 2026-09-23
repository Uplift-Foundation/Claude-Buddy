using Xunit;

namespace ClaudeBuddy.Tests;

// This collection used to exist for the log *directory*: CLAUDE_BUDDY_LOG_DIR
// is one process-wide environment variable, and several classes here pointed it
// at a scratch directory of their own, so running them in parallel meant each
// writing into the other's half the time. That is no longer why. Those classes
// now take a CrashLog.ScopeForTests instead, which is AsyncLocal and therefore
// has no name outside the flow that opened it — see that method for why an
// environment variable was the wrong width for the job, and
// LogDirIsolationTests for the property that replaced three rounds of listing
// the classes by hand.
//
// What still needs serialising is PersonaLog.Said: a process-wide, never
// expiring dedupe set, and the reason a rejection message is written once per
// process rather than once per call. Every class below clears it in its
// constructor and again on dispose, because otherwise a message an earlier
// class already said would be silently skipped here. Those clears are visible
// to every other class in the process — so a clear landing in the middle of a
// neighbour's run can re-arm a message it has already written and give it a
// second line. That matters because these classes assert on content, mostly as
// Assert.Single(LinesAbout(...)): exactly one line about exactly one picture.
// Serialising them is what keeps "exactly one" true.
//
// Two different pieces of shared state, then, with two different fixes: the
// directory is isolated structurally so that forgetting is harmless, and the
// dedupe set is serialised because content assertions genuinely need one class
// at a time. Same argument, and same shape, as [Collection("Settings")] in
// tests/UiTests.
[CollectionDefinition("LogDir")]
public class LogDirCollection { }
