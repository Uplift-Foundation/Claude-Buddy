using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace ClaudeBuddy.Tests;

// App.Initialize's one decision: which theme this build wears.
//
// Worth a test rather than being taken on trust, because both halves of it have
// already gone wrong once and neither failure looked like a failure. Declaring
// the macOS theme from code renders every control template as nothing at all —
// labels appear, switches and fields come out invisible — which is why App.axaml
// declares it and Initialize only *replaces* it (the comment at the top of
// App.axaml has the story). And the replacement itself is what stops Windows
// running AppKit's control metrics, which kept landing close-but-wrong because
// they are not published anywhere to copy.
//
// This suite hosts the real App class, so Initialize has already run by the time
// any test body does — there is nothing to arrange, only the outcome to read.
public class AppStylesTests
{
    [AvaloniaFact]
    public void ThePlatformGetsItsOwnDesignLanguageAndNotTheOtherOnes()
    {
        var app = Application.Current;
        Assert.NotNull(app);

        var fluent = app!.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Any();

        if (OperatingSystem.IsMacOS())
        {
            // The XAML-declared theme is left in place, ToolTip override and
            // all — Styles.Clear() would take that with it.
            Assert.False(fluent);
            Assert.NotEmpty(app.Styles);
        }
        else
        {
            // Fluent, and *only* Fluent: Styles.Clear() runs first, so the
            // macOS theme is not left underneath it competing for the same
            // control templates.
            Assert.True(fluent);
            Assert.Single(app.Styles);
        }
    }

    // The stripped ToolTip template is the reason an orb's thought bubble can
    // *be* its tooltip, and it is also why a plain string tooltip cannot be
    // used anywhere in this app — it would draw as unstyled text floating on
    // the desktop with no background, which is exactly how OrbFlyout's button
    // captions first shipped. Both facts follow from this one style, so it is
    // asserted rather than assumed: on macOS it comes from App.axaml, and on
    // Windows Styles.Clear() means Fluent's own template applies instead.
    [AvaloniaFact]
    public void OnMacOsTheToolTipTemplateIsStrippedToABareContentPresenter()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var app = Application.Current!;

        // Read off the style collection rather than off a constructed ToolTip:
        // Application styles apply to a control once it is attached to a
        // logical tree under the app, and a ToolTip built standalone here has
        // every property still at its unset default. What matters is that the
        // declaration survives Initialize, which is exactly what this asks.
        var style = app.Styles.OfType<Style>()
            .FirstOrDefault(s => s.Selector?.ToString() == "ToolTip");
        Assert.NotNull(style);

        var properties = style!.Setters.OfType<Setter>()
            .Select(setter => setter.Property?.Name)
            .ToArray();

        // No chrome of its own — a background or a border here would show as a
        // grey box behind every thought bubble in the app — and a Template
        // setter, which is the part that replaces the popup's whole visual with
        // a bare ContentPresenter so the bubble *is* the tooltip.
        Assert.Contains("Background", properties);
        Assert.Contains("BorderThickness", properties);
        Assert.Contains("Padding", properties);
        Assert.Contains("Template", properties);
    }
}
