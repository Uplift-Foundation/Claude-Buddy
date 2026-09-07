using System.Reflection;

namespace ClaudeBuddy.Tests;

// Reaching the panel the static entry points are talking to.
//
// This used to reflect over ChatPanel's private static `_instance`, because
// there was one panel and no accessor to it — nothing in the app had ever
// needed one, every other call site being static itself. CB-110 replaced that
// field with a registry of live panels, and gave the app itself reasons to
// ask which one is which: OpenFor has to know whether a session is already
// pinned somewhere, Reposition has to know which rectangles to avoid. So the
// accessors exist now as ordinary internals, and this file is a thin naming
// layer over them rather than a reflection seam.
//
// `Instance` keeps its name deliberately, so the several hundred call sites
// across this suite that mean "the panel the last OpenFor bound" go on saying
// it. What that resolves to is the *transient* panel — the one dismiss-on-
// deactivate still applies to — which is what every one of those tests has
// always been about.
internal static class ChatPanelTestAccess
{
    public static ChatPanel? Instance => ChatPanel.Transient;

    // The panel showing a given conversation, wherever it is. Distinct from
    // Instance once a panel can be pinned: a pinned panel is not the transient
    // and is the only place its session is on screen.
    public static ChatPanel? PanelFor(string sessionId) => ChatPanel.PanelFor(sessionId);

    public static IReadOnlyList<ChatPanel> All => ChatPanel.All;

    // Arranges the one state reading the registry cannot reach: a static hook
    // firing when no panel has ever been built. That branch is real — the
    // settings window can be open before any orb has been clicked — but by the
    // time any test in this assembly runs, some earlier one has usually built
    // a panel, so the no-panel case can only be arranged rather than waited
    // for.
    //
    // Empties the registry and puts it back. The one thing here still done by
    // reflection, and deliberately: emptying the registry is a thing only a
    // test ever wants, so it stays out of ChatPanel rather than becoming an
    // internal method the app could call by mistake. Reading and clearing a
    // private static list has no effect on the app at all, unlike a
    // ClearRegistry() sitting next to OpenFor would.
    //
    // Every caller restores what it took in a finally: leaving live panels
    // detached from the registry would not fail here, it would fail in
    // whichever class ran next, which is the worst shape a test-only seam can
    // have.
    public static IDisposable WithNoPanel()
    {
        var live = (IList<ChatPanel>)PanelsField.GetValue(null)!;
        var held = live.ToList();

        live.Clear();

        return new Restore(() =>
        {
            live.Clear();
            foreach (var panel in held) live.Add(panel);
        });
    }

    private static readonly FieldInfo PanelsField =
        typeof(ChatPanel).GetField("Panels", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("ChatPanel", "Panels");

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }
}
