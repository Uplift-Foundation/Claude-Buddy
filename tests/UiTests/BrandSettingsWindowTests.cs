using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Orbweaver.Tests;

// CB-250 QA: the settings window's title is built from Brand.DisplayName in
// its constructor, and nothing else pinned the whole string. Constructed the
// way CloudSettingsRowsTests does it, since the constructor is private and
// SettingsWindow.Open() would show a real window.
//
// [Collection("Settings")]: the constructor reads settings throughout.
[Collection("Settings")]
public class BrandSettingsWindowTests
{
    [AvaloniaFact]
    public void TheSettingsWindowIsTitledWithTheShippedName()
    {
        var ctor = typeof(SettingsWindow).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, types: Type.EmptyTypes)
            ?? throw new MissingMethodException("SettingsWindow", ".ctor()");

        var window = (SettingsWindow)ctor.Invoke(null);

        Assert.Equal($"{Brand.DisplayName} Settings", window.Title);
    }
}
