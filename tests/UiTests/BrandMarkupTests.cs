using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-250: the three names App.axaml and OrbWindow.axaml now read from code with
// x:Static instead of spelling out. BrandTests pins the constants; what only
// this suite can show is that the markup actually resolved them — the app
// menu's name on macOS, the orb window's title, and the orb menu's quit row —
// rather than compiling to an empty string or a placeholder.
//
// [Collection("Settings")]: constructing an OrbWindow reads a colour setting in
// a field initializer.
[Collection("Settings")]
public class BrandMarkupTests
{
    [AvaloniaFact]
    public void TheApplicationIsNamedFromBrand()
    {
        Assert.Equal(Brand.DisplayName, Application.Current!.Name);
    }

    [AvaloniaFact]
    public void AnOrbWindowIsTitledFromBrand()
    {
        Assert.Equal(Brand.DisplayName, new OrbWindow("brand-markup-title").Title);
    }

    [AvaloniaFact]
    public void TheOrbMenuOffersToExitByName()
    {
        var orb = new OrbWindow("brand-markup-exit");
        var menu = orb.GetLogicalDescendants().OfType<Control>()
            .Select(c => c.ContextMenu).First(m => m is not null)!;

        Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.Header as string == $"Exit {Brand.DisplayName}");
    }
}
