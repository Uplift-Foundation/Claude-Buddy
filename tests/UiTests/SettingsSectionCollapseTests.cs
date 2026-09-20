using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// The collapse half of CB-166: a real gesture on the header, moving the
// chevron and the body's IsVisible, and persisting through
// ClaudeBuddySettings — measured through the actual click handler rather
// than by calling SettingsSection.IsOpen directly, which is exactly the
// seam the production click handler sits behind.
//
// [Collection("Settings")] for the reason SettingsCollection.cs gives, and
// the window is built through its private constructor, shown and never
// closed, the same house rule SettingsSectionTests states: Window.Close()
// on a headless window corrupts a process-wide Avalonia FontManager cache.
[Collection("Settings")]
public class SettingsSectionCollapseTests
{
    private static readonly string[] AllIds =
    {
        "orbs", "orb-click", "auto-organize", "orb-colours", "chat-panel",
        "voice", "claude-code", "codex", "grok", "openclaw", "claude-cloud",
        "peer-link", "claude-desktop"
    };

    private static SettingsWindow NewWindow()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        return (SettingsWindow)ctor.Invoke(null);
    }

    private static void FlushRender()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static ToggleButton HeaderOf(SettingsWindow.SettingsSection section) =>
        section.GetLogicalDescendants().OfType<ToggleButton>()
            .Single(tb => tb.Classes.Contains("settings-disclosure"));

    private static Point CenterOf(Control control, Visual ancestor) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), ancestor)
        ?? throw new InvalidOperationException($"{control} is not inside {ancestor}");

    // Focus plus Space down/up, exercising the same header.Click handler a
    // mouse click reaches — and, unlike a synthesized mouse click, needing
    // no window layout or pointer capture, so most tests below use this. A
    // real mouse click is covered separately, once, in
    // AMouseClickTogglesTheSectionToo — see that test's comment for why
    // it exists rather than being assumed to behave the same way.
    private static void Click(ToggleButton header)
    {
        header.Focus();
        header.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
        FlushRender();
        header.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space });
        FlushRender();
    }

    private static void ResetAllSections()
    {
        foreach (var id in AllIds) ClaudeBuddySettings.SetSettingsSectionCollapsed(id, false);
    }

    [AvaloniaFact]
    public void EverySectionOpensOnAFreshInstall()
    {
        ResetAllSections();
        try
        {
            var window = NewWindow();

            Assert.All(window.Sections.Values, s => Assert.True(s.IsOpen));
        }
        finally
        {
            ResetAllSections();
        }
    }

    // The ticket's own acceptance criterion: click a header, its section
    // folds; click again, it opens back up.
    [AvaloniaFact]
    public void ASectionCollapsesAndExpands()
    {
        ResetAllSections();
        try
        {
            var window = NewWindow();
            window.Show();
            FlushRender();

            var section = window.Sections["voice"];
            var header = HeaderOf(section);

            Assert.True(section.IsOpen);

            Click(header);
            Assert.False(section.IsOpen);
            Assert.True(ClaudeBuddySettings.IsSettingsSectionCollapsed("voice"));

            Click(header);
            Assert.True(section.IsOpen);
            Assert.False(ClaudeBuddySettings.IsSettingsSectionCollapsed("voice"));
        }
        finally
        {
            ResetAllSections();
        }
    }

    // A real mouse click, kept separate from the Space-driven tests above
    // and clicked in the *padding* rather than on the heading text — this is
    // the regression guard for a bug this ticket actually shipped and caught
    // before landing: the header's ControlTemplate is a bare ContentPresenter
    // (App.axaml's "settings-disclosure" style) that did not consume the
    // Background TemplateBinding, so nothing painted a surface across the
    // button's bounds. An unpainted area is not hit-testable in Avalonia,
    // and neither a TextBlock's glyphs nor a Path's stroke are surfaces of
    // their own, so the whole header — not just the gaps around the label —
    // was a dead zone no mouse click could reach at all. Fixed by binding
    // Background on the presenter; this test clicks the padding specifically
    // so a future template edit that drops that binding fails here rather
    // than shipping a header a mouse cannot open.
    [AvaloniaFact]
    public void AMouseClickTogglesTheSectionToo()
    {
        ResetAllSections();
        try
        {
            var window = NewWindow();
            window.Show();
            FlushRender();

            var section = window.Sections["orbs"];
            var header = HeaderOf(section);

            // Far right of the header's own bounds — past the chevron and
            // the heading text, in the padding a real click could easily
            // land in without anyone aiming for a letter.
            var point = header.TranslatePoint(
                new Point(header.Bounds.Width - 2, header.Bounds.Height / 2), window)!.Value;

            Assert.True(section.IsOpen);

            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            FlushRender();
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            FlushRender();

            Assert.False(section.IsOpen);
            Assert.True(ClaudeBuddySettings.IsSettingsSectionCollapsed("orbs"));
        }
        finally
        {
            ResetAllSections();
        }
    }

    // The chevron itself moves — not just IsVisible on the body — since it's
    // the only visual cue once the rows are hidden.
    [AvaloniaFact]
    public void CollapsingRotatesTheChevronAndExpandingRotatesItBack()
    {
        ResetAllSections();
        try
        {
            var window = NewWindow();
            window.Show();
            FlushRender();

            var section = window.Sections["voice"];
            var header = HeaderOf(section);
            var chevron = header.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
            var rotation = Assert.IsType<RotateTransform>(chevron.RenderTransform);

            Assert.Equal(90, rotation.Angle);

            Click(header);
            Assert.Equal(0, rotation.Angle);

            Click(header);
            Assert.Equal(90, rotation.Angle);
        }
        finally
        {
            ResetAllSections();
        }
    }

    // The ticket's other acceptance criterion: a fold survives past the
    // window that made it, which is what a *setting* means as opposed to a
    // one-off view toggle.
    [AvaloniaFact]
    public void TheStateIsRestoredInTheNextWindow()
    {
        ResetAllSections();
        try
        {
            var first = NewWindow();
            first.Show();
            FlushRender();

            Click(HeaderOf(first.Sections["codex"]));
            Assert.False(first.Sections["codex"].IsOpen);

            var second = NewWindow();

            Assert.False(second.Sections["codex"].IsOpen);
            Assert.All(
                AllIds.Where(id => id != "codex"),
                id => Assert.True(second.Sections[id].IsOpen));
        }
        finally
        {
            ResetAllSections();
        }
    }

    // Rebuild() throws away and rebuilds every section from scratch — a
    // gateway TextBox's LostFocus commit does exactly this — so what a
    // section was left as has to come from settings, not from the instance
    // that is about to be discarded.
    [AvaloniaFact]
    public void RebuildKeepsWhatSectionsWereLeftAs()
    {
        ResetAllSections();
        try
        {
            var window = NewWindow();
            window.Show();
            FlushRender();

            Click(HeaderOf(window.Sections["orbs"]));
            Assert.False(window.Sections["orbs"].IsOpen);

            window.Rebuild();

            Assert.False(window.Sections["orbs"].IsOpen);
            Assert.True(window.Sections["voice"].IsOpen);
        }
        finally
        {
            ResetAllSections();
        }
    }

    // A folded section's rows stay in the logical tree — through the real
    // IsOpen setter this time, rather than the architect's own probe of
    // IsVisible directly, which is what the whole-window row tests actually
    // depend on surviving a fold.
    [AvaloniaFact]
    public void AFoldedSectionsRowsStayInTheLogicalTree()
    {
        var window = NewWindow();
        var section = window.Sections["orbs"];

        var rows = section.GetLogicalDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(rows);

        section.IsOpen = false;

        var reachable = window.GetLogicalDescendants().ToHashSet();
        Assert.All(rows, row => Assert.Contains(row, reachable));
    }

    // Space and Enter operate the disclosure header like any other button —
    // they must never also close the window, which is what would happen if
    // the window's own KeyDown handler treated them the way it treats
    // Escape.
    [AvaloniaTheory]
    [InlineData(Key.Space, KeyModifiers.None, false)]
    [InlineData(Key.Enter, KeyModifiers.None, false)]
    [InlineData(Key.Escape, KeyModifiers.None, true)]
    public void SpaceAndEnterAreNotWindowCloseKeysWhileEscapeStillIs(
        Key key, KeyModifiers modifiers, bool shouldClose) =>
        Assert.Equal(shouldClose, SettingsWindow.ShouldCloseOnKeyDown(key, modifiers));

    [AvaloniaFact]
    public void AllThirteenIdsAreDistinct() =>
        Assert.Equal(AllIds.Length, AllIds.Distinct().Count());
}
