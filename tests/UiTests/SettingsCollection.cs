using Xunit;

namespace ClaudeBuddy.UiTests;

// Every test class that reads or writes ClaudeBuddySettings runs in this
// collection, which means one at a time.
//
// The settings model is a process-wide static, and almost everything visual in
// this app reads it while being constructed — an OrbWindow picks up a colour in
// a field initializer, SettingsWindow builds its entire row list out of it. So
// two classes running in parallel, one of them flipping a setting, do not race
// to a *failure* so much as to a different set of executed lines: the losing
// class still asserts what it asserted, having taken the other branch to get
// there.
//
// That was found by measuring rather than by a red test. Three consecutive runs
// of this suite over an identical binary reported 1914, 2024 and 1914 covered
// lines in SettingsWindow.cs — a 110-line swing with nothing to explain it but
// scheduling, which is also large enough to swamp a real change and read as one.
// A flaky number is worse than a low one, because it invites arguing with the
// measurement.
//
// The definition has to be here rather than borrowed from tests/UnitTests, which
// has one under the same name: xUnit resolves a collection definition per
// assembly, so [Collection("Settings")] in this assembly was grouping the
// classes correctly and finding no definition at all. That works — the name is
// enough to serialise — but it leaves nowhere to write this down, and nothing to
// stop the two assemblies' idea of "Settings" drifting apart.
[CollectionDefinition("Settings")]
public sealed class SettingsCollection
{
}
