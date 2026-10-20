using System.Runtime.CompilerServices;

namespace Orbweaver.Tests;

// CB-267: the same guard tests/UiTests has. SettingsWindow's inbound rows read
// real Claude Code config roots by default, which no screenshot of any other
// window has a reason to do — and which would put the developer's own
// ~/.claude state into a capture that is attached to a PR.
internal static class InboundSeamBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        SettingsWindow.InboundRoots = () => Array.Empty<string>();
    }
}
