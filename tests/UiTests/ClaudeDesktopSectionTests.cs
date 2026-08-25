using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiTests;

// The Claude Desktop submenu: one row per profile, what its label says, and what
// its theme picker offers.
//
// Built here and asserted on its shape. Nothing is clicked — every Click handler
// in this file reaches ClaudeDesktopManager to launch, focus, quit or rewrite the
// theme of a real application, and those are excluded for exactly that reason.
// What is testable is everything up to the click: which items exist, which are
// enabled, which is ticked.
//
// Worth testing rather than counting, because the label is the only place the app
// can say a thing it otherwise cannot: two processes sharing one profile
// directory corrupts leveldb and SQLite, it can happen without this app's
// involvement, and it used to be invisible because instances were counted with
// TryAdd so a duplicate collapsed into one "running" row.
[Collection("Settings")]
public class ClaudeDesktopSectionTests
{
    private static ProfileView Profile(
        string name = "Claude-Profile-1",
        bool running = false,
        ProfileActivity activity = ProfileActivity.None,
        string? message = null,
        string themeMode = "system",
        int instances = 1) =>
        new(DisplayName: name,
            Directory: "/tmp/" + name,
            IsDefault: false,
            IsRunning: running,
            Pid: running ? 4242 : 0,
            Activity: activity,
            Message: message,
            ThemeMode: themeMode,
            InstanceCount: instances);

    // ---- the label -------------------------------------------------------

    [AvaloniaFact]
    public void AnIdleProfileIsJustItsName()
    {
        Assert.Equal("Claude-Profile-1", ClaudeDesktopSection.ProfileLabel(Profile()));
    }

    [AvaloniaFact]
    public void ATransientActivityIsSpelledOutAfterTheName()
    {
        Assert.Contains("Launching…",
            ClaudeDesktopSection.ProfileLabel(Profile(activity: ProfileActivity.Launching)));
        Assert.Contains("Quitting…",
            ClaudeDesktopSection.ProfileLabel(Profile(activity: ProfileActivity.Quitting)));
        Assert.Contains("won't quit",
            ClaudeDesktopSection.ProfileLabel(Profile(activity: ProfileActivity.ForceQuitOffered)));
    }

    // An error carries its own message, because "error" on its own tells the user
    // nothing they can act on.
    [AvaloniaFact]
    public void AnErrorCarriesItsMessage()
    {
        var label = ClaudeDesktopSection.ProfileLabel(
            Profile(activity: ProfileActivity.Error, message: "codesign refused it"));

        Assert.Contains("codesign refused it", label);
    }

    [AvaloniaFact]
    public void AnErrorWithNoMessageStillSaysError()
    {
        var label = ClaudeDesktopSection.ProfileLabel(
            Profile(activity: ProfileActivity.Error, message: null));

        Assert.Contains("error", label);
    }

    // The duplicate-instance warning, which is the whole reason InstanceCount is
    // carried through at all.
    [AvaloniaFact]
    public void TwoInstancesOnOneProfileAreCalledOut()
    {
        var label = ClaudeDesktopSection.ProfileLabel(Profile(running: true, instances: 2));

        Assert.Contains("2 instances", label);
        Assert.Contains("quit one", label);
    }

    // ...but an activity wins over it. Something actively happening is more
    // urgent than a count, and the two suffixes would not fit together.
    [AvaloniaFact]
    public void AnActivityTakesPrecedenceOverTheInstanceCount()
    {
        var label = ClaudeDesktopSection.ProfileLabel(
            Profile(running: true, activity: ProfileActivity.Quitting, instances: 2));

        Assert.Contains("Quitting…", label);
        Assert.DoesNotContain("instances", label);
    }

    [AvaloniaFact]
    public void OneInstanceIsNotWarnedAbout()
    {
        Assert.DoesNotContain("instances",
            ClaudeDesktopSection.ProfileLabel(Profile(running: true, instances: 1)));
    }

    // ---- truncation ------------------------------------------------------

    // Profile names are folder names, so they can be arbitrarily long.
    [AvaloniaFact]
    public void AShortNameIsLeftAlone()
    {
        Assert.Equal("short", ClaudeDesktopSection.Truncate("short"));
    }

    [AvaloniaFact]
    public void AVeryLongNameIsShortenedWithAnEllipsis()
    {
        var truncated = ClaudeDesktopSection.Truncate(new string('x', 200));

        Assert.EndsWith("…", truncated);
        Assert.True(truncated.Length < 200);
    }

    // Trailing space is trimmed before the ellipsis, so the result never reads
    // as "name …".
    [AvaloniaFact]
    public void TrailingSpaceIsTrimmedBeforeTheEllipsis()
    {
        var name = new string('x', 40) + new string(' ', 100) + "tail";

        var truncated = ClaudeDesktopSection.Truncate(name);

        Assert.DoesNotContain(" …", truncated);
    }

    // ---- the theme picker ------------------------------------------------

    // A running instance cannot be re-themed — the theme is read at launch — so
    // the row says why instead of offering choices that would not take effect.
    [AvaloniaFact]
    public void ARunningProfileIsOfferedNoThemeChoices()
    {
        var item = ClaudeDesktopSection.BuildThemeItem(Profile(running: true));

        Assert.False(item.IsEnabled);
        Assert.Null(item.Menu);
        Assert.Contains("quit to change", item.Header);
    }

    [AvaloniaFact]
    public void AStoppedProfileIsOfferedThreeThemes()
    {
        var item = ClaudeDesktopSection.BuildThemeItem(Profile(running: false));

        Assert.NotNull(item.Menu);
        var labels = item.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();

        Assert.Equal(3, labels.Count);
        Assert.Contains("Match system", labels);
        Assert.Contains("Light", labels);
        Assert.Contains("Dark", labels);
    }

    // Exactly one is ticked, and it is the one the profile is actually set to.
    [AvaloniaFact]
    public void TheProfilesOwnThemeIsTheTickedOne()
    {
        var item = ClaudeDesktopSection.BuildThemeItem(Profile(themeMode: "dark"));

        var checked_ = item.Menu!.Items.OfType<NativeMenuItem>()
            .Where(i => i.IsChecked)
            .Select(i => i.Header)
            .ToList();

        Assert.Equal(new[] { "Dark" }, checked_);
    }

    // Case-insensitive, because the value is written by whatever last set it and
    // has been seen both ways.
    [AvaloniaFact]
    public void TheThemeMatchIsCaseInsensitive()
    {
        var item = ClaudeDesktopSection.BuildThemeItem(Profile(themeMode: "DARK"));

        Assert.Contains(item.Menu!.Items.OfType<NativeMenuItem>(),
            i => i.IsChecked && i.Header == "Dark");
    }

    // A theme value this version does not know leaves nothing ticked rather than
    // guessing — better than silently claiming the profile is on "system".
    [AvaloniaFact]
    public void AnUnknownThemeLeavesNothingTicked()
    {
        var item = ClaudeDesktopSection.BuildThemeItem(Profile(themeMode: "solarized"));

        Assert.DoesNotContain(item.Menu!.Items.OfType<NativeMenuItem>(), i => i.IsChecked);
    }

    // ---- the profile row -------------------------------------------------

    // "Launch" or "Bring to front" depending on whether it is up — the same row
    // doing both, since which one you want is never ambiguous.
    [AvaloniaFact]
    public void AStoppedProfileOffersLaunchAndARunningOneOffersBringToFront()
    {
        var stopped = ClaudeDesktopSection.BuildProfileItem(Profile(running: false));
        var running = ClaudeDesktopSection.BuildProfileItem(Profile(running: true));

        Assert.Contains(Children(stopped), i => i.Header == "Launch");
        Assert.Contains(Children(running), i => i.Header == "Bring to front");
    }

    // Quit is offered only for something that is running, and becomes "Force
    // quit" once the ordinary one has been ignored.
    [AvaloniaFact]
    public void QuitIsOnlyEnabledForARunningProfile()
    {
        var stopped = Children(ClaudeDesktopSection.BuildProfileItem(Profile(running: false)));
        var running = Children(ClaudeDesktopSection.BuildProfileItem(Profile(running: true)));

        Assert.False(stopped.Single(i => i.Header == "Quit").IsEnabled);
        Assert.True(running.Single(i => i.Header == "Quit").IsEnabled);
    }

    [AvaloniaFact]
    public void AProfileThatWillNotQuitIsOfferedForceQuit()
    {
        var items = Children(ClaudeDesktopSection.BuildProfileItem(
            Profile(running: true, activity: ProfileActivity.ForceQuitOffered)));

        Assert.Contains(items, i => i.Header == "Force quit");
        Assert.DoesNotContain(items, i => i.Header == "Quit");
    }

    // Mid-quit, neither is offered again: a second quit while one is in flight is
    // how you end up force-quitting something that was about to close cleanly.
    [AvaloniaFact]
    public void AProfileAlreadyQuittingIsNotOfferedQuitAgain()
    {
        var items = Children(ClaudeDesktopSection.BuildProfileItem(
            Profile(running: true, activity: ProfileActivity.Quitting)));

        Assert.False(items.Single(i => i.Header == "Quit").IsEnabled);
    }

    private static System.Collections.Generic.List<NativeMenuItem> Children(NativeMenuItem item)
    {
        Assert.NotNull(item.Menu);
        return item.Menu!.Items.OfType<NativeMenuItem>().ToList();
    }
}
