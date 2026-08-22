using System.Reflection;

namespace ClaudeBuddy.Tests;

// ChatPanel is a singleton (its own comment explains why: two panels would
// fight over being the key window) and OpenFor/HideFor/etc. are all static,
// with the one live instance held in a private static field. There is no
// public accessor to it — nothing in the app has ever needed one, since
// every other call site is itself static.
//
// Reflection rather than a change to ChatPanel.axaml.cs: this suite is not
// allowed to touch app source, and the field being private is exactly the
// kind of internal detail a test seam would otherwise have to open up for a
// single test project's convenience. Reading a private static field via
// reflection has no runtime effect on the app at all, unlike adding a new
// public member to it would.
internal static class ChatPanelTestAccess
{
    private static readonly FieldInfo InstanceField =
        typeof(ChatPanel).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("ChatPanel", "_instance");

    public static ChatPanel? Instance => (ChatPanel?)InstanceField.GetValue(null);
}
