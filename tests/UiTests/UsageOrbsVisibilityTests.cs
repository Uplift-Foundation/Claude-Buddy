using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-220: the usage (account) orbs have a switch of their own, apart from the
// session orbs, while "Show orbs" — the tray item, the Settings switch and
// Ctrl+Alt+H — still means every orb.
//
// Each test gets its own settings directory, so the inherit-from-ShowOrbs rule
// is tested against a file that really has no showUsageOrbs in it, and nothing
// written here reaches the next test. The account orbs are put on screen with
// AccountOrbs.Apply and a hand-built reading: no usage poll, no CLI.
//
// Two tests point SessionManager.Instance at a scratch manager, because that
// is what the tray item and the hotkey both go through, and restore it in a
// finally. [Collection("Settings")] is what keeps that from being seen by
// another test: every class that reads settings — which is every class that
// builds an orb or a tray — is serialised with this one.
[Collection("Settings")]
public class UsageOrbsVisibilityTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _settingsDir =
        Path.Combine(Path.GetTempPath(), "cb-usage-orbs-settings-" + Guid.NewGuid());

    private readonly string _statusDir =
        Path.Combine(Path.GetTempPath(), "cb-usage-orbs-status-" + Guid.NewGuid());

    private readonly List<SessionManager> _managers = new();

    public UsageOrbsVisibilityTests()
    {
        Directory.CreateDirectory(_settingsDir);
        Directory.CreateDirectory(_statusDir);
        Environment.SetEnvironmentVariable("CLAUDE_BUDDY_SETTINGS_DIR", _settingsDir);
        ClaudeBuddySettings.ReloadForTests();
    }

    public void Dispose()
    {
        foreach (var manager in _managers) manager.AccountOrbsForTests.CloseAll();
        try { Directory.Delete(_statusDir, recursive: true); } catch { }
    }

    private SessionManager NewManager()
    {
        var manager = new SessionManager(_statusDir);
        _managers.Add(manager);
        return manager;
    }

    // One account orb on screen, as a usage poll would have drawn it.
    private static AccountOrbWindow ShowAccountOrb(SessionManager manager)
    {
        manager.AccountOrbsForTests.Apply(new[]
        {
            new AccountUsage(
                ConfigDir: null,
                Label: "account",
                Available: true,
                SubscriptionType: "team",
                Session: new UsageWindow(10, Now.AddHours(3)),
                Weekly: new UsageWindow(40, Now.AddDays(3)),
                Extra: null,
                ReadAt: Now,
                Source: AccountUsageSource.ClaudeCode)
        }, Now);

        return Assert.Single(manager.AccountOrbsForTests.Orbs.Values);
    }

    private static void SetInstance(SessionManager? manager) =>
        typeof(SessionManager).GetProperty(nameof(SessionManager.Instance))!
            .GetSetMethod(nonPublic: true)!.Invoke(null, new object?[] { manager });

    private static NativeMenu MenuOf(TrayController tray) =>
        (NativeMenu)typeof(TrayController)
            .GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tray)!;

    // ---- the setting --------------------------------------------------------

    [AvaloniaFact]
    public void UnsetShowUsageOrbsInheritsShowOrbs()
    {
        // The upgrade case: someone who had every orb hidden must not get the
        // usage orbs back just because they grew a switch of their own.
        Assert.Null(ClaudeBuddySettings.ShowUsageOrbsStored);
        Assert.True(ClaudeBuddySettings.ShowUsageOrbs);

        ClaudeBuddySettings.ShowOrbs = false;
        Assert.False(ClaudeBuddySettings.ShowUsageOrbs);
        Assert.Null(ClaudeBuddySettings.ShowUsageOrbsStored);

        // Once set it is its own answer, whatever ShowOrbs says.
        ClaudeBuddySettings.ShowUsageOrbs = true;
        Assert.True(ClaudeBuddySettings.ShowUsageOrbs);
        Assert.False(ClaudeBuddySettings.ShowOrbs);
    }

    [AvaloniaFact]
    public void AManagerStartedWithOrbsHiddenStartsWithUsageOrbsHiddenToo()
    {
        // AccountOrbs used to start visible with nothing telling it otherwise,
        // so the first poll after a launch with orbs hidden drew a usage orb
        // anyway. The manager now seeds it from the setting.
        ClaudeBuddySettings.ShowOrbs = false;

        var manager = NewManager();

        Assert.False(manager.OrbsVisible);
        Assert.False(manager.UsageOrbsVisible);
        Assert.False(manager.AccountOrbsForTests.Visible);
        Assert.False(ShowAccountOrb(manager).IsVisible);
    }

    [AvaloniaFact]
    public void AnExplicitUsageSettingWinsOverShowOrbsAtStartup()
    {
        ClaudeBuddySettings.ShowOrbs = false;
        ClaudeBuddySettings.ShowUsageOrbs = true;

        var manager = NewManager();

        Assert.False(manager.OrbsVisible);
        Assert.True(manager.UsageOrbsVisible);
        Assert.True(ShowAccountOrb(manager).IsVisible);
    }

    // ---- independence -------------------------------------------------------

    [AvaloniaFact]
    public void HidingUsageOrbsLeavesTheSessionOrbsAlone()
    {
        var manager = NewManager();
        var orb = ShowAccountOrb(manager);
        Assert.True(orb.IsVisible);

        manager.SetUsageOrbsVisible(false);

        Assert.False(orb.IsVisible);
        Assert.False(manager.UsageOrbsVisible);
        Assert.False(ClaudeBuddySettings.ShowUsageOrbs);
        Assert.True(manager.OrbsVisible);
        Assert.True(ClaudeBuddySettings.ShowOrbs);

        // Asked for what it already is: nothing moves and nothing is written.
        manager.SetUsageOrbsVisible(false);
        Assert.False(orb.IsVisible);

        manager.SetUsageOrbsVisible(true);
        Assert.True(orb.IsVisible);
        Assert.True(ClaudeBuddySettings.ShowUsageOrbs);
    }

    [AvaloniaFact]
    public void UsageOrbsCanBeShownWhileTheSessionOrbsAreHidden()
    {
        var manager = NewManager();
        var orb = ShowAccountOrb(manager);

        manager.SetOrbsVisible(false);
        Assert.False(orb.IsVisible);

        manager.SetUsageOrbsVisible(true);

        Assert.True(orb.IsVisible);
        Assert.False(manager.OrbsVisible);
        Assert.False(ClaudeBuddySettings.ShowOrbs);
    }

    [AvaloniaFact]
    public void ShowOrbsStillMeansAllOfThem()
    {
        // Ctrl+Alt+H, the tray's "Show orbs" and the Settings switch all land
        // here: both flags move, and both are written.
        var manager = NewManager();
        var orb = ShowAccountOrb(manager);

        manager.SetOrbsVisible(false);

        Assert.False(manager.OrbsVisible);
        Assert.False(manager.UsageOrbsVisible);
        Assert.False(orb.IsVisible);
        Assert.False(ClaudeBuddySettings.ShowOrbs);
        Assert.Equal(false, ClaudeBuddySettings.ShowUsageOrbsStored);

        manager.SetOrbsVisible(true);

        Assert.True(manager.UsageOrbsVisible);
        Assert.True(orb.IsVisible);
        Assert.Equal(true, ClaudeBuddySettings.ShowUsageOrbsStored);
    }

    [AvaloniaFact]
    public void ShowOrbsBringsTheUsageOrbsBackEvenWhenTheSessionOrbsWereAlreadyShowing()
    {
        // The early return in SetOrbsVisible is for the session orbs. Were the
        // usage half behind it, "Show orbs" on an already-showing session set
        // would leave the usage orbs hidden — the one thing it was asked to do.
        var manager = NewManager();
        var orb = ShowAccountOrb(manager);
        manager.SetUsageOrbsVisible(false);

        manager.SetOrbsVisible(true);

        Assert.True(manager.UsageOrbsVisible);
        Assert.True(orb.IsVisible);
    }

    [AvaloniaFact]
    public void ReapplyingAccountOrbsUsesTheUsageFlagNotTheSessionOne()
    {
        // A settings-window switch for a usage source calls this. It used to
        // pass OrbsVisible, which would put hidden usage orbs back up.
        ClaudeBuddySettings.AccountUsageEnabled = true;
        var manager = NewManager();
        var orb = ShowAccountOrb(manager);
        manager.SetUsageOrbsVisible(false);

        manager.ReapplyAccountOrbs();

        Assert.True(manager.OrbsVisible);
        Assert.False(manager.AccountOrbsForTests.Visible);
        Assert.False(orb.IsVisible);
    }

    // ---- the tray item and the hotkey ---------------------------------------

    [AvaloniaFact]
    public void TheTrayItemAndTheHotkeyToggleOnlyTheUsageOrbs()
    {
        var manager = NewManager();
        var orb = ShowAccountOrb(manager);
        SetInstance(manager);
        try
        {
            // What Ctrl+Alt+U does on a real press, short of the OS.
            HotkeyActions.Dispatch(HotkeyAction.ToggleUsageOrbsVisible);
            Dispatcher.UIThread.RunJobs();

            Assert.False(manager.UsageOrbsVisible);
            Assert.False(orb.IsVisible);
            Assert.True(manager.OrbsVisible);

            // The tray item's handler, which is the same method.
            TrayController.ToggleUsageOrbsVisible();

            Assert.True(manager.UsageOrbsVisible);
            Assert.True(orb.IsVisible);
            Assert.True(manager.OrbsVisible);
        }
        finally
        {
            SetInstance(null);
        }
    }

    [AvaloniaFact]
    public void TheTrayItemSitsBesideShowOrbsAndMirrorsTheFlag()
    {
        var manager = NewManager();
        manager.SetUsageOrbsVisible(false);
        SetInstance(manager);
        try
        {
            TrayController tray;
            try
            {
                tray = new TrayController();
            }
            catch
            {
                // A TrayIcon may not be constructible under the headless
                // platform — the same allowance TrayMenuTests makes.
                return;
            }

            var items = MenuOf(tray).Items.OfType<NativeMenuItem>().ToList();
            var showOrbs = items.FindIndex(i => i.Header == "Show orbs");
            var usage = items.Single(i => i.Header == "Show usage orbs");

            Assert.Equal(showOrbs + 1, items.IndexOf(usage));
            Assert.Equal(MenuItemToggleType.CheckBox, usage.ToggleType);
            Assert.False(usage.IsChecked);
            Assert.True(items[showOrbs].IsChecked);

            // The usage flag is part of the rebuild signature, so flipping it
            // alone rebuilds the menu with the new tick — an unchanged session
            // list would otherwise be declined as nothing new.
            manager.SetUsageOrbsVisible(true);
            tray.Update(Array.Empty<TrayController.SessionEntry>());

            Assert.True(MenuOf(tray).Items.OfType<NativeMenuItem>()
                .Single(i => i.Header == "Show usage orbs").IsChecked);
        }
        finally
        {
            SetInstance(null);
        }
    }

    [AvaloniaFact]
    public void TheTrayItemDefaultsToTickedWithNoSessionManager()
    {
        TrayController tray;
        try
        {
            tray = new TrayController();
        }
        catch
        {
            return;
        }

        var usage = MenuOf(tray).Items.OfType<NativeMenuItem>()
            .Single(i => i.Header == "Show usage orbs");
        Assert.True(usage.IsChecked);

        // And its handler is a no-op there, like every other tray toggle.
        TrayController.ToggleUsageOrbsVisible();
    }
}
