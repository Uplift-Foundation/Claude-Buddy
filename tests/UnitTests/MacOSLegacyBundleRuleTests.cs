using Xunit;
using static ClaudeBuddy.MacOSLegacyBundle;

namespace ClaudeBuddy.Tests;

// CB-255 §5: the rules that decide whether a "Claude Buddy.app" beside
// Orbweaver.app is ours to move to the Trash, with no filesystem behind them.
// The executor against real folders is MacOSLegacyBundleTests in
// IntegrationTests.
public class MacOSLegacyBundleRuleTests
{
    // ---- Decide -------------------------------------------------------------

    [Fact]
    public void Our_bundle_id_and_our_executable_is_trashed() =>
        Assert.Equal(Decision.Trash, Decide("io.github.wtvamp.claudebuddy", "ClaudeBuddy"));

    // The rule reads the real constant, not a copy of it: a drift in either is
    // a bundle this stops recognising.
    [Fact]
    public void The_rule_is_keyed_on_the_brand_bundle_id() =>
        Assert.Equal(Decision.Trash, Decide(Brand.MacBundleId, LegacyExecutable));

    // The case this whole rule exists for: somebody else's app that happens to
    // be called Claude Buddy.app, even one with an executable of the same name.
    [Fact]
    public void A_same_named_app_that_is_not_ours_is_left_alone() =>
        Assert.Equal(Decision.LeaveAlone, Decide("com.example.claudebuddy", "ClaudeBuddy"));

    // Our id with another executable is not proof either — a bundle has to look
    // like ours in both places.
    [Fact]
    public void Our_id_with_another_executable_is_left_alone() =>
        Assert.Equal(Decision.LeaveAlone, Decide("io.github.wtvamp.claudebuddy", "Orbweaver"));

    [Fact]
    public void A_plist_that_says_nothing_is_left_alone()
    {
        Assert.Equal(Decision.LeaveAlone, Decide(null, null));
        Assert.Equal(Decision.LeaveAlone, Decide("io.github.wtvamp.claudebuddy", null));
        Assert.Equal(Decision.LeaveAlone, Decide(null, "ClaudeBuddy"));
    }

    // Plist strings are case-sensitive, and so is the rule.
    [Fact]
    public void The_match_is_ordinal() =>
        Assert.Equal(Decision.LeaveAlone, Decide("IO.GITHUB.WTVAMP.CLAUDEBUDDY", "claudebuddy"));

    // ---- ReadPlist ----------------------------------------------------------

    // The shape build-macos-app.sh writes, down to the nested CFBundleURLTypes
    // dict — which here carries a decoy CFBundleExecutable, so a reader that
    // looked past the top-level dict would answer "Decoy".
    private const string BuildScriptPlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleName</key>              <string>Claude Buddy</string>
            <key>CFBundleURLTypes</key>
            <array>
                <dict>
                    <key>CFBundleExecutable</key>
                    <string>Decoy</string>
                </dict>
            </array>
            <key>CFBundleIdentifier</key>        <string>io.github.wtvamp.claudebuddy</string>
            <!-- a comment between a key and the next one -->
            <key>CFBundleExecutable</key>        <string>ClaudeBuddy</string>
            <key>LSUIElement</key>               <true/>
        </dict>
        </plist>
        """;

    [Fact]
    public void Reads_both_values_from_the_plist_the_build_script_writes() =>
        Assert.Equal(("io.github.wtvamp.claudebuddy", "ClaudeBuddy"), ReadPlist(BuildScriptPlist));

    // No whitespace between elements: a streaming reader that advances twice
    // per value skips every other key on exactly this input.
    [Fact]
    public void Reads_a_plist_with_no_whitespace_between_elements() =>
        Assert.Equal(("io.github.wtvamp.claudebuddy", "ClaudeBuddy"), ReadPlist(
            "<plist><dict><key>CFBundleIdentifier</key><string>io.github.wtvamp.claudebuddy</string>" +
            "<key>CFBundleExecutable</key><string>ClaudeBuddy</string></dict></plist>"));

    // A binary plist is not the form our build writes; it reads as nothing,
    // which Decide leaves alone.
    [Fact]
    public void A_binary_plist_reads_as_nothing() =>
        Assert.Equal((null, null), ReadPlist("bplist00Ñ\u0001\u0002_\u0010\u001cCFBundleIdentifier"));

    [Fact]
    public void A_document_that_is_not_a_plist_reads_as_nothing() =>
        Assert.Equal((null, null), ReadPlist(
            "<html><dict><key>CFBundleIdentifier</key><string>io.github.wtvamp.claudebuddy</string></dict></html>"));

    [Fact]
    public void A_plist_with_no_dict_reads_as_nothing() =>
        Assert.Equal((null, null), ReadPlist("<plist version=\"1.0\"><array/></plist>"));

    [Fact]
    public void A_missing_key_reads_as_null_for_that_key_only() =>
        Assert.Equal(("io.github.wtvamp.claudebuddy", null), ReadPlist(
            "<plist><dict><key>CFBundleIdentifier</key><string>io.github.wtvamp.claudebuddy</string></dict></plist>"));

    // A key with no value after it, and a key whose value is not a string.
    [Fact]
    public void A_key_without_a_string_value_reads_as_null()
    {
        Assert.Equal((null, null), ReadPlist(
            "<plist><dict><key>CFBundleExecutable</key><true/><key>CFBundleIdentifier</key></dict></plist>"));
    }

    // ---- TrashDestination ---------------------------------------------------

    private static readonly DateTimeOffset At = new(2026, 10, 8, 14, 3, 9, TimeSpan.Zero);

    [Fact]
    public void A_free_name_in_the_trash_is_used_as_is() =>
        Assert.Equal(Path.Combine("trash", "Claude Buddy.app"), TrashDestination("trash", At, _ => false));

    // An old bundle trashed last week is never overwritten.
    [Fact]
    public void A_taken_name_gets_a_timestamp()
    {
        var taken = new HashSet<string> { Path.Combine("trash", "Claude Buddy.app") };

        Assert.Equal(Path.Combine("trash", "Claude Buddy 20261008-140309.app"),
            TrashDestination("trash", At, taken.Contains));
    }

    // Two trashed in the same second — /Applications and ~/Applications, say,
    // on top of one already there.
    [Fact]
    public void A_taken_timestamp_gets_a_counter()
    {
        var taken = new HashSet<string>
        {
            Path.Combine("trash", "Claude Buddy.app"),
            Path.Combine("trash", "Claude Buddy 20261008-140309.app"),
            Path.Combine("trash", "Claude Buddy 20261008-140309 2.app"),
        };

        Assert.Equal(Path.Combine("trash", "Claude Buddy 20261008-140309 3.app"),
            TrashDestination("trash", At, taken.Contains));
    }

    // The words the user is shown, pinned: it names the old app, the new one
    // and where to go.
    [Fact]
    public void The_notice_says_what_happened_and_what_to_do() =>
        Assert.Equal(
            "Claude Buddy.app was moved to the Trash. If it was in your Login Items, " +
            "add Orbweaver instead (System Settings > General > Login Items).",
            NoticeText);
}
