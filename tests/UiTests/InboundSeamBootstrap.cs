using System.Runtime.CompilerServices;

namespace Orbweaver.Tests;

// CB-267: SettingsWindow's inbound rows read real config roots by default, and
// every test in this assembly that builds a SettingsWindow would otherwise read
// the developer's ~/.claude/settings.json (or, before PeerInboundSettingsFile
// existed, hit its stub). Roots default to none here, so no existing test sees
// a row or a first-run card, and the tests that want them install a fake world
// of their own. The production delegates are kept so one test can still call
// each of them against a scratch directory.
internal static class InboundSeamBootstrap
{
    // Captured inside Initialize(), not in field initializers. A class with no
    // static constructor is beforefieldinit, so its field initializers may run at
    // the first *field* access rather than when Initialize() is called -- which
    // can be after a test class has installed its fakes, and then "production"
    // is a fake. Assigning here pins the capture to module load, before anything
    // in this assembly can have replaced a seam.
    internal static Func<IReadOnlyList<string>> ProductionRoots { get; private set; } = null!;
    internal static Func<string, InboundSetting> ProductionState { get; private set; } = null!;
    internal static Func<string, string> ProductionDisplayPath { get; private set; } = null!;

    [ModuleInitializer]
    internal static void Initialize()
    {
        ProductionRoots = SettingsWindow.InboundRoots;
        ProductionState = SettingsWindow.InboundState;
        ProductionDisplayPath = SettingsWindow.InboundDisplayPath;

        SettingsWindow.InboundRoots = () => Array.Empty<string>();
    }
}
