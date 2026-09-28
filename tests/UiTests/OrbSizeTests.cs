using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-198's orb half: drawing an orb at a size, the right-click Size submenu, the
// size following an orb across the title migration, and the anchors outside
// Root — the mic flyout and the chat panel — that used to assume (28,28).
//
// Driven the way OrbSoundSubmenuTests drives the Sound submenu: RebuildSizeSubmenu
// and ApplyOrbSize directly rather than through a real right-click, which needs
// a shown window and a working popup nothing else here depends on.
//
// [Collection("Settings")] because nearly every case reads or writes orbSize or
// orbSizes, which are process-wide; each restores what it changed.
[Collection("Settings")]
public class OrbSizeTests : IDisposable
{
    private readonly List<string> _keys = new();
    private readonly List<string> _chats = new();
    private readonly double _globalBefore = ClaudeBuddySettings.OrbSize;

    public void Dispose()
    {
        foreach (var key in _keys) ClaudeBuddySettings.SetOrbSize(key, null);
        foreach (var id in _chats) ChatPanel.CloseFor(id);
        ClaudeBuddySettings.OrbSize = _globalBefore;
        Flush();
    }

    private static void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private OrbWindow NewOrb()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString())
        {
            SoundKey = "orb-size-test-" + Guid.NewGuid()
        };
        _keys.Add(orb.SoundKey);
        return orb;
    }

    // SettingsWindow's constructor is private; SettingsWindowCoverageTests
    // reaches it the same way.
    private static SettingsWindow NewSettings() =>
        (SettingsWindow)typeof(SettingsWindow).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            types: Type.EmptyTypes)!.Invoke(null);

    private static MenuItem SizeMenu(OrbWindow orb) => orb.FindControl<MenuItem>("SizeMenuItem")!;

    private static List<MenuItem> SizeItems(OrbWindow orb) =>
        SizeMenu(orb).Items.OfType<MenuItem>().ToList();

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    // --- drawing at a size ---------------------------------------------------

    [AvaloniaFact]
    public void AnOrbStartsAtTheShippedSize()
    {
        var orb = NewOrb();

        Assert.Equal(1.0, orb.OrbSize);
        Assert.Equal(56, orb.Width);
        Assert.Equal(28, orb.CentreDip);
        Assert.Equal(18, orb.OrbRadius);
    }

    [AvaloniaFact]
    public void DoublingAnOrbDoublesItsWindowRadiusAndCentreAndScalesRootNotItsInsides()
    {
        var orb = NewOrb();

        Assert.True(orb.ApplyOrbSize(2.0));

        Assert.Equal(112, orb.Width);
        Assert.Equal(112, orb.Height);
        Assert.Equal(36, orb.OrbRadius, 6);
        Assert.Equal(56, orb.CentreDip);

        // The transform carries the size; Root itself keeps its 56-DIP geometry,
        // which is the whole point of doing it this way rather than threading a
        // fourth factor through SetTeamRole.
        var transform = Assert.IsType<ScaleTransform>(orb.FindControl<LayoutTransformControl>("SizeTransform")!.LayoutTransform);
        Assert.Equal(2.0, transform.ScaleX);
        Assert.Equal(2.0, transform.ScaleY);
        Assert.Equal(56, orb.Root.Width);
    }

    [AvaloniaFact]
    public void ResizingKeepsTheVisibleCentreWhereItWas()
    {
        var orb = NewOrb();
        orb.Position = new PixelPoint(300, 200);
        var scaling = orb.DesktopScaling;
        var centreBefore = (X: 300 + 28 * scaling, Y: 200 + 28 * scaling);

        orb.ApplyOrbSize(2.0);

        Assert.Equal(centreBefore.X, orb.Position.X + 56 * scaling, 0);
        Assert.Equal(centreBefore.Y, orb.Position.Y + 56 * scaling, 0);

        // And back down again, landing where it started.
        orb.ApplyOrbSize(1.0);
        Assert.Equal(new PixelPoint(300, 200), orb.Position);
    }

    [AvaloniaFact]
    public void ATeamMemberAtDoubleSizeKeepsItsSmallerRadiusWhicheverIsAppliedFirst()
    {
        // 18 * 0.72 * 2. Both orders, because each setter has its own early
        // return and neither may skip the other's contribution.
        var memberFirst = NewOrb();
        memberFirst.SetTeamRole(true);
        memberFirst.ApplyOrbSize(2.0);

        var sizeFirst = NewOrb();
        sizeFirst.ApplyOrbSize(2.0);
        sizeFirst.SetTeamRole(true);

        Assert.Equal(25.92, memberFirst.OrbRadius, 6);
        Assert.Equal(25.92, sizeFirst.OrbRadius, 6);

        sizeFirst.SetTeamRole(false);
        Assert.Equal(36, sizeFirst.OrbRadius, 6);
    }

    [AvaloniaFact]
    public void ApplyingTheSameSizeAgainChangesNothingAndASizeIsClamped()
    {
        var orb = NewOrb();
        orb.Position = new PixelPoint(10, 10);

        Assert.False(orb.ApplyOrbSize(1.0));
        Assert.Equal(new PixelPoint(10, 10), orb.Position);

        Assert.True(orb.ApplyOrbSize(50));
        Assert.Equal(OrbSizing.Max, orb.OrbSize);
    }

    [AvaloniaFact]
    public void TheEffectiveSizeIsTheOrbsOverrideOrElseTheSlider()
    {
        var orb = NewOrb();

        ClaudeBuddySettings.OrbSize = 1.5;
        orb.ApplyEffectiveOrbSize();
        Assert.Equal(1.5, orb.OrbSize);

        ClaudeBuddySettings.SetOrbSize(orb.SoundKey, 0.75);
        orb.ApplyEffectiveOrbSize();
        Assert.Equal(0.75, orb.OrbSize);
    }

    // --- the Size submenu ----------------------------------------------------

    [AvaloniaFact]
    public void TheMenuOffersDefaultNamingTheSliderThenEveryPresetWithDefaultChecked()
    {
        var orb = NewOrb();
        ClaudeBuddySettings.OrbSize = 1.25;

        orb.RebuildSizeSubmenu();

        var items = SizeItems(orb);
        Assert.Equal("Default (125%)", items[0].Header);
        Assert.True(items[0].IsChecked);
        Assert.Equal(OrbSizing.Presets.Select(OrbWindow.SizeLabel), items.Skip(1).Select(i => (string)i.Header!));
        Assert.All(items.Skip(1), i => Assert.False(i.IsChecked));
        Assert.All(items, i => Assert.Equal(MenuItemToggleType.CheckBox, i.ToggleType));

        // Rebuilt, not appended to, on every open.
        orb.RebuildSizeSubmenu();
        Assert.Equal(items.Count, SizeItems(orb).Count);
    }

    [AvaloniaFact]
    public void APresetWritesTheOverrideResizesTheOrbAndIsTheCheckedRowNextTime()
    {
        var orb = NewOrb();
        orb.RebuildSizeSubmenu();

        Click(SizeItems(orb).Single(i => Equals(i.Header, "150%")));

        Assert.Equal(1.5, ClaudeBuddySettings.OrbSizeFor(orb.SoundKey));
        Assert.Equal(1.5, orb.OrbSize);

        orb.RebuildSizeSubmenu();
        var checkedItem = Assert.Single(SizeItems(orb), i => i.IsChecked);
        Assert.Equal("150%", checkedItem.Header);
    }

    [AvaloniaFact]
    public void DefaultClearsTheOverrideAndTheOrbFollowsTheSliderAgain()
    {
        var orb = NewOrb();
        ClaudeBuddySettings.OrbSize = 0.75;
        ClaudeBuddySettings.SetOrbSize(orb.SoundKey, 2.0);
        orb.ApplyEffectiveOrbSize();

        orb.RebuildSizeSubmenu();
        Click(SizeItems(orb)[0]);

        Assert.Null(ClaudeBuddySettings.OrbSizeFor(orb.SoundKey));
        Assert.Equal(0.75, orb.OrbSize);
    }

    [AvaloniaFact]
    public void AnOverrideEqualToTheSliderIsStillShownAsAnOverride()
    {
        // It will stop following the slider the moment the slider moves, so the
        // menu has to say which of the two it is.
        var orb = NewOrb();
        ClaudeBuddySettings.OrbSize = 1.0;
        ClaudeBuddySettings.SetOrbSize(orb.SoundKey, 1.0);

        orb.RebuildSizeSubmenu();

        var items = SizeItems(orb);
        Assert.False(items[0].IsChecked);
        Assert.True(items.Single(i => Equals(i.Header, "100%")).IsChecked);
    }

    [AvaloniaFact]
    public void OpeningTheContextMenuBuildsTheSizeSubmenu()
    {
        var orb = NewOrb();

        orb.SessionMenu_Opening(null, new System.ComponentModel.CancelEventArgs());

        Assert.NotEmpty(SizeItems(orb));
    }

    // AvaloniaFact although nothing here needs a window: this class's Dispose
    // flushes the dispatcher, and a plain [Fact] runs that on a pool thread —
    // which is CB-183's hazard exactly (a stray thread reaching
    // Dispatcher.UIThread). The first version was a [Fact], threw from
    // Dispose in Release, and took four OrbFlyoutTests clicks down after it.
    [AvaloniaFact]
    public void SizeLabelsAreWholePercentagesInAnyCulture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("75%", OrbWindow.SizeLabel(0.75));
            Assert.Equal("125%", OrbWindow.SizeLabel(1.25));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    // --- the title migration -------------------------------------------------

    [AvaloniaFact]
    public void ASizeChosenBeforeTheTitleArrivesFollowsTheOrbOntoItsNewKey()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var cwd = "/Users/user/project-" + Guid.NewGuid();

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "" });
        var oldKey = orb.SoundKey;
        _keys.Add(oldKey);

        orb.RebuildSizeSubmenu();
        Click(SizeItems(orb).Single(i => Equals(i.Header, "200%")));
        Assert.Equal(2.0, orb.OrbSize);

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "claude-buddy" });
        var newKey = orb.SoundKey;
        _keys.Add(newKey);

        Assert.NotEqual(oldKey, newKey);
        // Copied, not moved — CB-167's QA round 3 rule for a shared key.
        Assert.Equal(2.0, ClaudeBuddySettings.OrbSizeFor(oldKey));
        Assert.Equal(2.0, ClaudeBuddySettings.OrbSizeFor(newKey));
        Assert.Equal(2.0, orb.OrbSize);
    }

    [AvaloniaFact]
    public void TheMigrationNeverOverwritesASizeTheNewKeyAlreadyHas()
    {
        var orb = new OrbWindow(Guid.NewGuid().ToString());
        var cwd = "/Users/user/project-" + Guid.NewGuid();
        var titled = new SessionStatus { State = "idle", Cwd = cwd, Title = "named" };

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = cwd, Title = "" });
        var oldKey = orb.SoundKey;
        _keys.Add(oldKey);
        var newKey = SessionManager.SoundKeyFor(titled, orb.SessionId);
        _keys.Add(newKey);

        ClaudeBuddySettings.SetOrbSize(oldKey, 2.0);
        ClaudeBuddySettings.SetOrbSize(newKey, 0.75);

        orb.UpdateFrom(titled);

        Assert.Equal(0.75, ClaudeBuddySettings.OrbSizeFor(newKey));
        Assert.Equal(0.75, orb.OrbSize);
    }

    [AvaloniaFact]
    public void AnOrbWithNoOverrideFollowsTheSliderFromItsFirstUpdate()
    {
        ClaudeBuddySettings.OrbSize = 1.5;
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        orb.UpdateFrom(new SessionStatus { State = "idle", Cwd = "/Users/user/p-" + Guid.NewGuid(), Title = "" });
        _keys.Add(orb.SoundKey);

        Assert.Equal(1.5, orb.OrbSize);
        Assert.Null(ClaudeBuddySettings.OrbSizeFor(orb.SoundKey));
    }

    // --- anchors outside Root ------------------------------------------------

    [AvaloniaFact]
    public void TheMicFlyoutsArcIsConcentricWithTheScaledOrb()
    {
        var orb = NewOrb();
        orb.ApplyOrbSize(2.0);

        // The second call finds the flyout visible and puts it straight at its
        // resting place instead of starting the glide again — ShowNear's own
        // IsVisible branch — so the assertion is about where it settles, not a
        // frame of the animation.
        orb.EnsureFlyoutShown();
        Flush();
        orb.EnsureFlyoutShown();
        var flyout = orb.Flyout!;

        // The arc's virtual centre sits on the orb's centre, which at 2x is
        // (56,56) and not the (28,28) it used to hard-code.
        var arc = orb.PointToScreen(new Point(56 - flyout.ArcOriginX, 56 - flyout.ArcOriginY));
        Assert.Equal(arc, flyout.Position);
        Assert.NotEqual(orb.PointToScreen(new Point(28 - flyout.ArcOriginX, 28 - flyout.ArcOriginY)), flyout.Position);

        flyout.Close();
    }

    [AvaloniaFact]
    public void AChatPanelBesideADoubleSizeOrbClearsItsLargerHalf()
    {
        var orb = NewOrb();
        orb.ApplyOrbSize(2.0);
        var fake = new FakeChatSession(null) { SessionId = "orb-size-chat-" + Guid.NewGuid() };
        _chats.Add(fake.SessionId);

        ChatPanel.OpenFor(orb, fake);
        Flush();
        var panel = ChatPanel.PanelFor(fake.SessionId)!;

        var centre = orb.PointToScreen(new Point(orb.CentreDip, orb.CentreDip));
        var scale = (panel.Screens.ScreenFromPoint(centre) ?? panel.Screens.Primary)!.Scaling;
        var gap = OrbSizing.ChatPanelGap(2.0) * scale;
        var top = panel.Position.Y;
        var bottom = panel.Position.Y + panel.Height * scale;

        // Below the orb or flipped above it, but never nearer its centre than
        // the scaled gap — 62 DIP at 2x, where the old flat 34 would have put
        // the panel's edge inside a 56-DIP half.
        Assert.True(top - centre.Y >= gap - 1 || centre.Y - bottom >= gap - 1,
            $"panel {top}..{bottom} is within {gap} of the orb's centre {centre.Y}");
    }

    [AvaloniaFact]
    public void FollowOrbResizeMovesTheOpenPanelAndIsSafeWithNone()
    {
        using (ChatPanelTestAccess.WithNoPanel())
        {
            ChatPanel.FollowOrbResize();
        }

        var orb = NewOrb();
        var fake = new FakeChatSession(null) { SessionId = "orb-size-follow-" + Guid.NewGuid() };
        _chats.Add(fake.SessionId);
        ChatPanel.OpenFor(orb, fake);
        Flush();
        var panel = ChatPanel.PanelFor(fake.SessionId)!;
        var before = panel.Position;

        orb.ApplyOrbSize(2.0);
        ChatPanel.FollowOrbResize();

        Assert.NotEqual(before, panel.Position);
    }

    // --- the settings slider -------------------------------------------------

    [AvaloniaFact]
    public void TheSliderSpansExactlyOrbSizingsRangeAndOpensOnTheSavedValue()
    {
        ClaudeBuddySettings.OrbSize = 1.25;

        var slider = NewSettings().OrbSizeSlider();

        Assert.Equal(OrbSizing.Min, slider.Minimum);
        Assert.Equal(OrbSizing.Max, slider.Maximum);
        Assert.Equal(1.25, slider.Value);
        Assert.True(slider.IsSnapToTickEnabled);
        Assert.Equal(OrbSizing.Step, slider.TickFrequency);
        Assert.Equal(OrbSizing.Step, slider.SmallChange);
    }

    [AvaloniaFact]
    public void MovingTheSliderWritesTheGlobalSize()
    {
        ClaudeBuddySettings.OrbSize = 1.0;
        var slider = NewSettings().OrbSizeSlider();

        slider.Value = 1.5;

        Assert.Equal(1.5, ClaudeBuddySettings.OrbSize);

        // Stored snapped to the step, whatever the thumb landed on.
        slider.Value = 1.23;
        Assert.Equal(1.25, ClaudeBuddySettings.OrbSize);

        // An unrelated property changing is not a size change.
        slider.MinWidth = 200;
        Assert.Equal(1.25, ClaudeBuddySettings.OrbSize);
    }
}
