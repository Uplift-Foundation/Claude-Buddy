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

    // Takes focus away from a panel, which is the gesture the whole of pinning
    // is defined against: an unpinned panel hides when it loses focus and a
    // pinned one does not, and neither half can be tested without a way to
    // make it happen.
    //
    // Reflection, and the second thing here that has to be. Nothing a test can
    // legitimately do deactivates a headless window — confirmed by trying:
    // showing a second Window and calling Activate() on it leaves the panel's
    // IsActive true, because Avalonia's headless platform has no window
    // manager to move focus between windows and never raises the callback.
    // What it does have is the callback itself: HeadlessWindowImpl exposes the
    // same `Deactivated` action every real backend invokes, and firing that is
    // the platform doing to the window exactly what a click on another app
    // would do — IsActive goes false and ChatPanel's own handler runs with all
    // four of its carve-outs live. Calling ChatPanel's handler directly
    // instead would skip the IsActive re-check that handler opens with, which
    // is one of the things worth covering.
    public static void Deactivate(ChatPanel panel)
    {
        var impl = ImplProperty.GetValue(panel)
            ?? throw new InvalidOperationException("panel has no PlatformImpl — is it shown?");

        var deactivated = impl.GetType().GetProperty(
            "Deactivated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        var action = deactivated?.GetValue(impl) as Action
            ?? throw new InvalidOperationException(
                "no Deactivated callback on " + impl.GetType().FullName);

        action();
    }

    private static readonly PropertyInfo ImplProperty =
        typeof(Avalonia.Controls.TopLevel).GetProperty(
            "PlatformImpl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMemberException("TopLevel", "PlatformImpl");

    private static readonly FieldInfo PanelsField =
        typeof(ChatPanel).GetField("Panels", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("ChatPanel", "Panels");

    // What the panel thinks this machine is called.
    //
    // The third header line names the machine always, and colours it when the
    // session is on a *different* one — so the two cases a reviewer cares
    // about are "same name" and "different name", and on a real machine only
    // one of them can be arranged: MachineNames.Mine() answers whatever this
    // Mac or this runner is called, and a test that asserted a literal would
    // be asserting the hostname of whoever ran it.
    //
    // Reflection, and the third thing here that has to be, for the reason the
    // two above give: a settable machine name is a thing only a test ever
    // wants, and an internal setter beside ApplyMeta would be a seam the app
    // could reach by mistake. ChatPanel caches the answer in a private static
    // precisely because it cannot change while the process lives.
    //
    // Restores what it took, so the next class to run sees the real machine
    // again. Every caller of this is in [Collection("Settings")] — the field
    // is process-wide, and two classes racing on it would race to a different
    // set of executed lines rather than to a failure.
    public static IDisposable WithMachineName(string? name)
    {
        var held = MachineField.GetValue(null);
        MachineField.SetValue(null, name);

        return new Restore(() => MachineField.SetValue(null, held));
    }

    private static readonly FieldInfo MachineField =
        typeof(ChatPanel).GetField("_thisMachine", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("ChatPanel", "_thisMachine");

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }
}
