using Xunit;
using static Orbweaver.KeepAliveRepair;

namespace Orbweaver.Tests;

// CB-256 §2: the rules that decide whether a keep-alive LaunchAgent is ours
// and dead, and where the bundled script that repairs it lives — with no
// filesystem behind them. The executor against real folders is
// KeepAliveRepairTests in IntegrationTests.
public class KeepAliveRepairRuleTests
{
    // The three paths of the design's table: a phase-2 plist names B, this
    // build runs C.
    private const string Interim = "/Applications/Orbweaver.app/Contents/MacOS/ClaudeBuddy";
    private const string Current = "/Applications/Orbweaver.app/Contents/MacOS/Orbweaver";

    // ---- Decide -------------------------------------------------------------

    // The case the class exists for: a drag-install replaced the phase-2
    // bundle, so the plist names an executable that is gone, and this process
    // is the new one, running from a bundle with a script to repair it with.
    [Fact]
    public void Our_plist_naming_a_missing_program_from_a_bundle_is_repaired() =>
        Assert.Equal(Decision.Repair, Decide(Interim, Current, programExists: false));

    // "Dead", not "different": a plist pointing at a program that is there
    // is left alone, even one that is not this process — a copy in
    // ~/Applications, say, which somebody set up on purpose.
    [Fact]
    public void A_program_that_exists_is_left_alone()
    {
        Assert.Equal(Decision.LeaveAlone, Decide(Interim, Current, programExists: true));
        Assert.Equal(Decision.LeaveAlone, Decide(Current, Current, programExists: true));
    }

    // No plist that is provably ours (ReadProgram said null) — not opted in,
    // or somebody else's job — is never repaired into existence, whatever the
    // existence check says.
    [Fact]
    public void No_program_is_left_alone()
    {
        Assert.Equal(Decision.LeaveAlone, Decide(null, Current, programExists: false));
        Assert.Equal(Decision.LeaveAlone, Decide("", Current, programExists: false));
    }

    // Not running from a bundle — `dotnet run`, a loose publish — means there
    // is no bundled script and nothing launchd should be pointed at.
    [Fact]
    public void A_process_outside_a_bundle_is_left_alone()
    {
        Assert.Equal(Decision.LeaveAlone, Decide(Interim, "/Users/dev/src/Orbweaver/bin/Release/Orbweaver", programExists: false));
        Assert.Equal(Decision.LeaveAlone, Decide(Interim, null, programExists: false));
    }

    // ---- ReadProgram --------------------------------------------------------

    // The shape install-hooks.sh's keepalive_plist_content writes, verbatim
    // apart from the paths. A nested dict (KeepAlive) carries a decoy Label,
    // so a reader that looked past the top level would see two.
    private static string Plist(string label, string programArguments) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            {label}
            <key>ProgramArguments</key>
            {programArguments}
            <key>KeepAlive</key>
            <dict>
                <key>SuccessfulExit</key>
                <false/>
                <key>Label</key>
                <string>decoy</string>
            </dict>
            <key>ThrottleInterval</key>
            <integer>60</integer>
        </dict>
        </plist>
        """;

    private static string Ours(string programArguments) =>
        Plist("<string>io.github.wtvamp.claudebuddy</string>", programArguments);

    private static readonly string Arguments = $"<array><string>{Interim}</string></array>";

    [Fact]
    public void Our_plist_yields_its_program() =>
        Assert.Equal(Interim, ReadProgram(Ours(Arguments)));

    // The label is the bundle id, which is never renamed — so the rule reads
    // the real constant, not a copy of it.
    [Fact]
    public void Ours_means_labelled_with_the_brand_bundle_id() =>
        Assert.Equal(Interim, ReadProgram(Plist($"<string>{Brand.MacBundleId}</string>", Arguments)));

    // Only the first argument is the program; anything after it is an
    // argument to it.
    [Fact]
    public void The_program_is_the_first_argument() =>
        Assert.Equal(Interim, ReadProgram(Ours($"<array><string>{Interim}</string><string>--flag</string></array>")));

    [Fact]
    public void Somebody_elses_job_yields_nothing()
    {
        Assert.Null(ReadProgram(Plist("<string>com.example.agent</string>", Arguments)));
        // Plist strings are case-sensitive, and so is the rule.
        Assert.Null(ReadProgram(Plist("<string>IO.GITHUB.WTVAMP.CLAUDEBUDDY</string>", Arguments)));
    }

    [Fact]
    public void A_label_that_is_missing_or_not_a_string_yields_nothing()
    {
        // No Label key anywhere (the decoy's is renamed too).
        Assert.Null(ReadProgram(Ours(Arguments).Replace("<key>Label</key>", "<key>Name</key>")));
        Assert.Null(ReadProgram(Plist("<integer>1</integer>", Arguments)));
    }

    [Fact]
    public void Program_arguments_that_are_missing_or_malformed_yield_nothing()
    {
        // Not an array.
        Assert.Null(ReadProgram(Ours($"<string>{Interim}</string>")));
        // An empty array.
        Assert.Null(ReadProgram(Ours("<array></array>")));
        // A first element that is not a string.
        Assert.Null(ReadProgram(Ours("<array><integer>1</integer></array>")));
        // An empty program.
        Assert.Null(ReadProgram(Ours("<array><string></string></array>")));
        // No ProgramArguments key at all: the key is renamed, and its array
        // is then only the value of a key nobody asks for.
        Assert.Null(ReadProgram(Ours(Arguments).Replace("<key>ProgramArguments</key>", "<key>Program</key>")));
    }

    // Anything that is not the XML form our script writes reads as "not
    // provably ours", which is the direction the repair must fail in.
    [Fact]
    public void Anything_but_an_xml_plist_with_a_dict_yields_nothing()
    {
        Assert.Null(ReadProgram("bplist00\u0001\u0002"));
        Assert.Null(ReadProgram("<plist><dict><key>Label</key>"));
        Assert.Null(ReadProgram("<notaplist><dict/></notaplist>"));
        Assert.Null(ReadProgram("<plist version=\"1.0\"><array/></plist>"));
        Assert.Null(ReadProgram("<stale/>"));
    }

    // ---- BundledScript ------------------------------------------------------

    private static string? Script(string? processPath) => BundledScript(processPath)?.Replace('\\', '/');

    [Fact]
    public void A_bundle_executable_has_its_script_in_resources() =>
        Assert.Equal("/Applications/Orbweaver.app/Contents/Resources/install-hooks.sh", Script(Current));

    // Any bundle, under any name, with any executable: the script ships in the
    // bundle the process runs from, wherever that is.
    [Fact]
    public void The_bundle_can_be_anywhere_and_called_anything() =>
        Assert.Equal("/Users/dev/Applications/Old Copy.app/Contents/Resources/install-hooks.sh",
            Script("/Users/dev/Applications/Old Copy.app/Contents/MacOS/ClaudeBuddy"));

    [Fact]
    public void A_path_not_shaped_like_a_bundle_has_no_script()
    {
        // Not .app.
        Assert.Null(Script("/Users/dev/Orbweaver/Contents/MacOS/Orbweaver"));
        // Contents is not Contents.
        Assert.Null(Script("/Applications/Orbweaver.app/Stuff/MacOS/Orbweaver"));
        // MacOS is not MacOS.
        Assert.Null(Script("/Applications/Orbweaver.app/Contents/Resources/Orbweaver"));
        // A loose binary.
        Assert.Null(Script("/Users/dev/src/bin/Release/Orbweaver"));
    }

    // Too short to have a bundle above it at all, down to nothing.
    [Fact]
    public void A_path_too_short_for_a_bundle_has_no_script()
    {
        Assert.Null(Script("/MacOS/Orbweaver"));
        Assert.Null(Script("Orbweaver"));
        Assert.Null(Script("/"));
        Assert.Null(Script(""));
        Assert.Null(Script(null));
    }
}
