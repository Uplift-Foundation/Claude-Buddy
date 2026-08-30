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

    // Puts the field back to what it was, for the one thing reading it cannot
    // reach: a static hook's behaviour when no panel has ever been built.
    // That branch is real — the settings window can be open before any orb has
    // been clicked — but by the time any test in this assembly runs, the
    // singleton has usually been constructed by an earlier one, so the
    // no-panel case can only be arranged rather than waited for.
    //
    // Every caller restores what it took in a finally. Leaving a live panel
    // detached from the field would not fail here, it would fail in whichever
    // class ran next, which is the worst shape a test-only seam can have.
    public static IDisposable WithNoPanel()
    {
        var held = Instance;
        InstanceField.SetValue(null, null);
        return new Restore(() => InstanceField.SetValue(null, held));
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }
}
