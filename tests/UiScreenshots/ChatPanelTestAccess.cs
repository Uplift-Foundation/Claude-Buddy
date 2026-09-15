namespace ClaudeBuddy.Tests;

// Same seam as tests/UiTests/ChatPanelTestAccess.cs, copied for the same
// reason FakeChatSession is: this project stays isolated from tests/UiTests,
// and a two-line forwarder is cheaper to duplicate than to share.
//
// `Instance` resolves to the transient panel — the one every OpenFor in this
// suite binds, and the one dismiss-on-deactivate still applies to. It was a
// reflected private field until CB-110 turned the singleton into a registry;
// see the UiTests copy for the longer version.
internal static class ChatPanelTestAccess
{
    public static ChatPanel? Instance => ChatPanel.Transient;

    public static ChatPanel? PanelFor(string sessionId) => ChatPanel.PanelFor(sessionId);

    public static IReadOnlyList<ChatPanel> All => ChatPanel.All;
}
