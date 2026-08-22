using System.Reflection;

namespace ClaudeBuddy.Tests;

// Same reflection seam as tests/UiTests/ChatPanelTestAccess.cs, copied for
// the same reason FakeChatSession is: this project stays isolated from
// tests/UiTests, and reflection over a private static field needs no
// InternalsVisibleTo grant to duplicate cheaply.
internal static class ChatPanelTestAccess
{
    private static readonly FieldInfo InstanceField =
        typeof(ChatPanel).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("ChatPanel", "_instance");

    public static ChatPanel? Instance => (ChatPanel?)InstanceField.GetValue(null);
}
