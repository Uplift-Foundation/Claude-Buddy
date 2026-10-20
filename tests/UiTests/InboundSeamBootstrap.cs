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
    internal static readonly Func<IReadOnlyList<string>> ProductionRoots = SettingsWindow.InboundRoots;
    internal static readonly Func<string, InboundSetting> ProductionState = SettingsWindow.InboundState;
    internal static readonly Func<string, string> ProductionDisplayPath = SettingsWindow.InboundDisplayPath;

    [ModuleInitializer]
    internal static void Initialize()
    {
        SettingsWindow.InboundRoots = () => Array.Empty<string>();
    }
}
