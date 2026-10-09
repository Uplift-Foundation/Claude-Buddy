using Xunit;
using static Orbweaver.DataDirMigration;

namespace Orbweaver.Tests;

// CB-255 §2: the decisions the data-dir migration makes, with no folder behind
// any of them. The executor that acts on these is driven against temp folders
// in tests/IntegrationTests/DataDirMigrationTests.cs.
public class DataDirMigrationPlanTests
{
    // Every row of the design's table, for the data root. The override and
    // "no legacy folder" rows hold whatever the other inputs say.
    [Theory]
    [InlineData(false, false, false, false, "")]  // nothing to migrate
    [InlineData(false, true, true, false, "")]  // fresh install of the new name
    [InlineData(true, false, false, false, "Move,Snapshot")]  // first launch after upgrade
    [InlineData(true, true, true, false, "KeepNew")]  // new wins
    [InlineData(true, true, false, false, "Merge")]  // a copy that died part-way
    [InlineData(true, false, false, true, "")]  // override set
    [InlineData(true, true, false, true, "")]
    [InlineData(true, true, true, true, "")]
    public void Data_root_plan(bool legacy, bool @new, bool newHasSettings, bool overrideSet, string expected)
    {
        Assert.Equal(expected, string.Join(",", Plan(RootKind.Data, legacy, @new, newHasSettings, overrideSet)));
    }

    // Logs has no commit record: "exists" stands in for "has settings.json",
    // so an existing new Logs folder is always merged into — CrashLog may have
    // created it this very launch — and a move takes no snapshot.
    [Theory]
    [InlineData(false, false, false, false, "")]
    [InlineData(false, true, false, false, "")]
    [InlineData(true, false, false, false, "Move")]
    [InlineData(true, true, false, false, "Merge")]
    [InlineData(true, true, true, false, "Merge")]  // a settings.json in Logs means nothing
    [InlineData(true, false, false, true, "")]
    [InlineData(true, true, false, true, "")]
    public void Logs_root_plan(bool legacy, bool @new, bool newHasSettings, bool overrideSet, string expected)
    {
        Assert.Equal(expected, string.Join(",", Plan(RootKind.Logs, legacy, @new, newHasSettings, overrideSet)));
    }

    [Theory]
    [InlineData("CLAUDE_BUDDY_SETTINGS_DIR")]
    [InlineData("CLAUDE_BUDDY_LOG_DIR")]
    [InlineData("CLAUDE_BUDDY_BUNDLE_ROOT")]
    public void Any_one_of_the_three_overrides_switches_it_off(string name)
    {
        Assert.True(OverrideSet(variable => variable == name ? "/scratch" : null));
    }

    // The negative control: an empty value is "unset", the same as every reader
    // of these variables treats it, and an unrelated variable does nothing.
    [Fact]
    public void No_override_or_an_empty_one_leaves_it_on()
    {
        Assert.False(OverrideSet(_ => null));
        Assert.False(OverrideSet(_ => ""));
        Assert.False(OverrideSet(variable => variable == "CLAUDE_BUDDY_PROFILE_ROOT" ? "/x" : null));
    }

    // On Windows only the Logs subfolder of %LOCALAPPDATA%\ClaudeBuddy moves —
    // the folder itself still holds the hook script running sessions call.
    [Fact]
    public void Windows_roots_are_roaming_data_and_the_Logs_subfolder_of_local()
    {
        var roots = Roots(onWindows: true, @"C:\roam", @"C:\local", @"C:\home");

        Assert.Equal(
            new[]
            {
                new Root(RootKind.Logs, Path.Combine(@"C:\local", "ClaudeBuddy", "Logs"), Path.Combine(@"C:\local", "Orbweaver", "Logs")),
                new Root(RootKind.Data, Path.Combine(@"C:\roam", "ClaudeBuddy"), Path.Combine(@"C:\roam", "Orbweaver")),
            },
            roots);
    }

    [Fact]
    public void Mac_roots_are_application_data_and_Library_Logs()
    {
        var roots = Roots(onWindows: false, "/u/Library/Application Support", "/u/.local/share", "/u");

        Assert.Equal(
            new[]
            {
                new Root(RootKind.Logs, Path.Combine("/u", "Library", "Logs", "ClaudeBuddy"), Path.Combine("/u", "Library", "Logs", "Orbweaver")),
                new Root(RootKind.Data, Path.Combine("/u/Library/Application Support", "ClaudeBuddy"), Path.Combine("/u/Library/Application Support", "Orbweaver")),
            },
            roots);
    }

    // The legacy names are the ones the migration recognises; if they ever
    // drift, it stops finding what it is meant to move.
    [Fact]
    public void Roots_are_keyed_on_the_brand_names()
    {
        var data = Roots(true, "a", "b", "c")[1];

        Assert.Equal(Path.Combine("a", Brand.Legacy.DataDirName), data.Legacy);
        Assert.Equal(Path.Combine("a", Brand.DataDirName), data.New);
    }

    [Theory]
    [InlineData("Data", false, false, true)]
    [InlineData("Data", true, false, false)]
    [InlineData("Data", true, true, false)]  // data: what new has, new keeps
    [InlineData("Logs", false, false, true)]
    [InlineData("Logs", true, false, false)]
    [InlineData("Logs", true, true, true)]  // logs: the newer crash.log wins
    public void Which_files_a_copy_carries(string kind, bool existsInNew, bool legacyIsNewer, bool expected)
    {
        Assert.Equal(expected, ShouldCopy(Enum.Parse<RootKind>(kind), existsInNew, legacyIsNewer));
    }

    [Fact]
    public void What_a_copy_never_carries()
    {
        var sep = Path.DirectorySeparatorChar;

        Assert.True(Skipped(RootKind.Data, MarkerFile));
        Assert.True(Skipped(RootKind.Logs, MarkerFile));
        Assert.True(Skipped(RootKind.Data, "settings.json" + PartialSuffix));
        Assert.True(Skipped(RootKind.Data, "bundles"));
        Assert.True(Skipped(RootKind.Data, $"bundles{sep}Default{sep}Claude.app"));

        // Negative controls: the marker's name only counts at the top, a
        // folder merely starting with "bundles" is ordinary, and Logs has no
        // bundle cache to leave behind.
        Assert.False(Skipped(RootKind.Data, $"voices{sep}{MarkerFile}"));
        Assert.False(Skipped(RootKind.Data, "bundles-old"));
        Assert.False(Skipped(RootKind.Logs, "bundles"));
        Assert.False(Skipped(RootKind.Data, "settings.json"));
    }

    [Fact]
    public void Settings_json_goes_last_and_everything_else_keeps_its_order()
    {
        var sep = Path.DirectorySeparatorChar;

        Assert.Equal(
            new[] { "a", "peer-identity.json", $"voices{sep}settings.json", "z", "settings.json" },
            CopyOrder(new[] { "a", "settings.json", "peer-identity.json", $"voices{sep}settings.json", "z" }));
    }

    [Fact]
    public void The_note_names_where_the_folder_went_and_that_it_is_safe_to_delete()
    {
        var text = MarkerText("/new/home", new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero));

        Assert.Contains("/new/home", text);
        Assert.Contains("2026-10-08 09:30", text);
        Assert.Contains("settings.json", text);
        Assert.Contains("peer-identity.json", text);
        Assert.Contains("safe to delete", text);
    }
}
