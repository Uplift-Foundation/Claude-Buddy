using Xunit;

namespace ClaudeBuddy.UnitTests;

// Which of macOS's several names for a machine this app uses.
//
// **`Environment.MachineName` was the wrong one, and it took a user reading the
// answer to notice.** It returns `gethostname()`, a *network* name that picks up
// whatever the DHCP search domain supplies — on a real Mac mini here it produced
// `avatar.internal` → "avatar", a word its owner had never chosen and did not
// recognise. macOS's own name for that machine is "Warren's Mac mini".
//
// It matters more than a label: this name is the key a pairing is stored under,
// so a machine named after a DNS artefact is one somebody has to squint at in a
// list of peers before deciding whether to trust it.
//
// The order is asserted as a rule rather than as behaviour, because a test
// machine has exactly one set of names and the interesting cases are the ones it
// does not have.
public class MachineNamePreferenceTests
{
    private static Func<string, string?> Answers(params (string Key, string? Value)[] pairs) =>
        key => pairs.FirstOrDefault(p => p.Key == key).Value;

    [Fact]
    public void LocalHostNameIsPreferred()
    {
        // It is what macOS publishes on the network, already restricted to
        // letters, digits and hyphens, and it follows ComputerName
        // automatically when the user renames the machine.
        if (!OperatingSystem.IsMacOS()) return;

        Assert.Equal(
            "Warrens-Mac-mini",
            MachineNames.Preferred(
                Answers(("LocalHostName", "Warrens-Mac-mini"), ("ComputerName", "Warren’s Mac mini")),
                () => "avatar"));
    }

    [Fact]
    public void ComputerNameIsUsedWhenThereIsNoLocalHostName()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // Free text from Settings, so it needs the sanitising Tag does — which
        // is why the two are separate steps.
        Assert.Equal(
            "Warren’s Mac mini",
            MachineNames.Preferred(
                Answers(("ComputerName", "Warren’s Mac mini")), () => "avatar"));
    }

    [Fact]
    public void GetHostNameIsTheLastResortRatherThanTheFirstChoice()
    {
        // The behaviour this replaced. Still better than nothing, and still
        // reached when scutil answers neither — which is what a failure of the
        // subprocess looks like from here.
        if (!OperatingSystem.IsMacOS()) return;

        Assert.Equal("avatar", MachineNames.Preferred(Answers(), () => "avatar"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyAnswerIsNotAnAnswer(string? blank)
    {
        // scutil prints an empty line for a key that is not set, and exits 0
        // doing it — so "answered" and "answered usefully" are different
        // questions.
        if (!OperatingSystem.IsMacOS()) return;

        Assert.Equal(
            "Warren’s Mac mini",
            MachineNames.Preferred(
                Answers(("LocalHostName", blank), ("ComputerName", "Warren’s Mac mini")),
                () => "avatar"));
    }

    [Fact]
    public void OffMacOsNothingIsAsked()
    {
        // scutil is Apple's. Everywhere else the platform's own machine name is
        // the only name there is, and asking would mean spawning a process that
        // does not exist.
        var asked = 0;

        var name = MachineNames.Preferred(
            _ => { asked++; return "should-not-be-used"; }, () => "windows-box");

        if (OperatingSystem.IsMacOS()) return;

        Assert.Equal(0, asked);
        Assert.Equal("windows-box", name);
    }

    // --- and what the wire ends up carrying ------------------------------------

    [Fact]
    public void AFriendlyNameIsMadeSafeForAWire()
    {
        // ComputerName is free text and routinely has an apostrophe and spaces
        // in it. What goes on the wire has to survive being a filename, a tmux
        // target and a dictionary key.
        var tag = MachineNames.Tag("Warren’s Mac mini");

        Assert.Equal("warrensmacmini", tag);
        Assert.DoesNotContain(' ', tag);
        Assert.DoesNotContain('’', tag);
    }

    [Fact]
    public void ALocalHostNameSurvivesIntactApartFromCase()
    {
        // The reason to prefer it: it needs almost nothing doing to it, so the
        // name a user sees in Sharing settings is the name they see in Buddy.
        Assert.Equal("warrens-mac-mini", MachineNames.Tag("Warrens-Mac-mini"));
    }

    // --- the one name, kept readable -------------------------------------------

    [Fact]
    public void TheNameOnTheWireKeepsItsCaseAndHyphens()
    {
        // Unlike Tag, which was a tmux target and had to be short and
        // lower-case. This one is read by a person and matched against what
        // macOS shows them in Sharing settings.
        Assert.Equal("Warrens-Mac-mini", MachineNames.Clean("Warrens-Mac-mini"));
    }

    [Fact]
    public void FreeTextIsStillMadeSafe()
    {
        // ComputerName routinely has spaces and a curly apostrophe.
        Assert.Equal("WarrensMacmini", MachineNames.Clean("Warren’s Mac mini"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("---")]
    [InlineData("’’’")]
    public void ANameThatCleansToNothingGetsAPlaceholder(string? nothing)
    {
        // An empty name would make two machines that both failed to report one
        // collide with each other, which is worse than either being anonymous.
        Assert.Equal("machine", MachineNames.Clean(nothing));
    }

    [Fact]
    public void ABonjourSuffixIsTrimmedHereToo()
    {
        Assert.Equal("avatar", MachineNames.Clean("avatar.local"));
    }

    [Fact]
    public void AVeryLongNameIsBounded()
    {
        Assert.Equal(32, MachineNames.Clean(new string('a', 90)).Length);
    }

    [Fact]
    public void TheDnsArtefactStillGetsItsSuffixTrimmed()
    {
        // The old answer, kept working: ".local" is Bonjour's and carries no
        // information, and this is what a machine falls back to when scutil
        // says nothing.
        Assert.Equal("avatar", MachineNames.Tag("avatar.local"));
    }
}
