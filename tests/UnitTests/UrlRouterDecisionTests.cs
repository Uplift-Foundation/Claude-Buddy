using Xunit;

namespace ClaudeBuddy.UnitTests;

// The four decisions ClaudeDesktopUrlRouter makes before it touches anything.
//
// Everything around them writes the machine's own URL-handler database or opens
// a link for real, and is excluded — so these are what stand between "the rule
// is right" and "nobody has ever checked". Each one takes its input as an
// argument rather than reading it, which is the only reason any of them can be
// asked here at all: the alternative is a test whose answer depends on how many
// Claude Desktop profiles the person running it happens to have.
public class UrlRouterDecisionTests
{
    // ---- whether to claim at all -------------------------------------------

    // With one profile there is nothing to route — a link can only be meant for
    // that profile — so the schemes are left exactly as they were. An install
    // that never creates a second profile never notices this feature exists,
    // and that is deliberate: claiming a system-wide URL scheme is not
    // something to do speculatively.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void WithNothingToRouteBetweenTheSchemesAreLeftAlone(int candidates) =>
        Assert.Equal(ClaudeDesktopUrlRouter.ClaimAction.NotNeeded,
            ClaudeDesktopUrlRouter.WhatToDoAboutClaiming(routeEnabled: true, candidates));

    // Two is where it starts mattering, because two is where a link can be
    // meant for the profile you are not in.
    [Fact]
    public void WithTwoProfilesTheSchemesAreWorthClaiming() =>
        Assert.Equal(ClaudeDesktopUrlRouter.ClaimAction.Claim,
            ClaudeDesktopUrlRouter.WhatToDoAboutClaiming(routeEnabled: true, 2));

    [Fact]
    public void MoreThanTwoIsStillWorthClaiming() =>
        Assert.Equal(ClaudeDesktopUrlRouter.ClaimAction.Claim,
            ClaudeDesktopUrlRouter.WhatToDoAboutClaiming(routeEnabled: true, 7));

    // Switched off wins over everything, including a machine with plenty to
    // route between — and it does not merely decline to claim, it restores.
    // Without that the setting would be a one-way door: turning it off would
    // leave our claim in place and links would keep coming here.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void SwitchedOffAlwaysMeansHandTheSchemesBack(int candidates) =>
        Assert.Equal(ClaudeDesktopUrlRouter.ClaimAction.Restore,
            ClaudeDesktopUrlRouter.WhatToDoAboutClaiming(routeEnabled: false, candidates));

    // ---- what to put back ---------------------------------------------------

    // Claude Desktop when nothing was remembered, because that is who owned the
    // scheme in every case seen — and because turning the setting off has to
    // leave links working rather than owned by nobody.
    //
    // Reached with an empty string whenever routing is switched off without
    // ever having been on, which is an ordinary thing to do while looking at
    // the switch.
    [Fact]
    public void WithNothingRememberedTheSchemesGoBackToClaudeDesktop() =>
        Assert.Equal("com.anthropic.claudefordesktop",
            ClaudeDesktopUrlRouter.HandlerToRestore(""));

    [Fact]
    public void WhateverWasRememberedWins() =>
        Assert.Equal("com.example.browser",
            ClaudeDesktopUrlRouter.HandlerToRestore("com.example.browser"));

    // ---- whether to remember a handler at all -------------------------------

    [Fact]
    public void TheFirstRealHandlerSeenIsRemembered() =>
        Assert.True(ClaudeDesktopUrlRouter.ShouldRememberPreviousHandler(
            "claude", "com.anthropic.claudefordesktop", alreadyRemembered: ""));

    // Once, and never again. Re-claiming must not overwrite the real previous
    // handler — by then the current handler is usually *us*, and recording that
    // would make "restore" hand the schemes back to Claude Buddy for ever.
    [Fact]
    public void AHandlerAlreadyRememberedIsNotOverwritten() =>
        Assert.False(ClaudeDesktopUrlRouter.ShouldRememberPreviousHandler(
            "claude", "org.uplift.ClaudeBuddy", alreadyRemembered: "com.anthropic.claudefordesktop"));

    // Nothing owns the scheme, so there is nothing to put back and recording an
    // empty string would look identical to never having recorded one.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnownedSchemeLeavesNothingToRemember(string? current) =>
        Assert.False(ClaudeDesktopUrlRouter.ShouldRememberPreviousHandler(
            "claude", current, alreadyRemembered: ""));

    // Only "claude" — it is the scheme a person recognises and the one whose old
    // owner is worth restoring. Remembering per scheme would mean a setting that
    // has to restore three different things and can only record one.
    [Theory]
    [InlineData("claude-sign-in")]
    [InlineData("msauth")]
    [InlineData("")]
    public void NoOtherSchemesOwnerIsRemembered(string scheme) =>
        Assert.False(ClaudeDesktopUrlRouter.ShouldRememberPreviousHandler(
            scheme, "com.anthropic.claudefordesktop", alreadyRemembered: ""));

    // ---- what the settings window is told ------------------------------------

    [Fact]
    public void ClaimingEverySchemeIsReportedPlainly() =>
        Assert.Equal("routing links", ClaudeDesktopUrlRouter.ClaimStatusFor(3, 3));

    // Said in full rather than as "failed", because a partial claim is a real
    // state with a real consequence: the schemes that landed route correctly and
    // the ones that did not still go to Default. "Failed" would describe
    // neither, and a silent failure here means links keep going to the wrong
    // profile with nothing on screen to explain why — which is the state this
    // bug was found in.
    [Fact]
    public void APartialClaimSaysHowFarItGot() =>
        Assert.Equal("claimed 2 of 3 schemes", ClaudeDesktopUrlRouter.ClaimStatusFor(2, 3));

    // Nothing claimed is still reported as a count rather than as success, which
    // is the case that matters most — it is what a user sees when the app is
    // running unbundled or macOS refused.
    [Fact]
    public void ClaimingNothingIsNotReportedAsRoutingLinks() =>
        Assert.Equal("claimed 0 of 3 schemes", ClaudeDesktopUrlRouter.ClaimStatusFor(0, 3));

    // ---- the command a link is delivered with --------------------------------

    // No profile to route to at all. Still address a bundle by path rather than
    // calling plain `open <url>`: that would resolve the scheme straight back to
    // us and loop, which is the one failure mode of this feature a user cannot
    // get out of without turning it off.
    [Fact]
    public void WithNoRouteTheLinkStillGoesToABundleByPath()
    {
        var arguments = ClaudeDesktopUrlRouter.ArgumentsFor(null, "claude://x");

        Assert.Equal(new[] { "-a", "/Applications/Claude.app", "claude://x" }, arguments);
        Assert.DoesNotContain("-n", arguments);
    }

    [Fact]
    public void WithARouteTheChosenBundleIsAddressed()
    {
        var route = Route(
            bundle: "/tmp/bundles/Claude-Profile-1/Claude.app",
            userDataDir: "/tmp/profiles/Claude-Profile-1");

        var arguments = ClaudeDesktopUrlRouter.ArgumentsFor(route, "claude://x");

        Assert.Equal("-a", arguments[0]);
        Assert.Equal("/tmp/bundles/Claude-Profile-1/Claude.app", arguments[1]);
        Assert.Equal("claude://x", arguments[^1]);
    }

    // Never -n, on either path. -n is right when *launching* a profile, because
    // the caller has just proved nothing is running on that directory; here the
    // opposite is wanted, and starting a second Chromium on a live userData
    // directory is the leveldb corruption the whole profile feature exists to
    // avoid.
    [Fact]
    public void ALinkIsNeverDeliveredWithANewInstanceFlag()
    {
        var route = Route(bundle: "/tmp/Claude.app", userDataDir: "/tmp/data");

        Assert.DoesNotContain("-n", ClaudeDesktopUrlRouter.ArgumentsFor(route, "claude://x"));
        Assert.DoesNotContain("-n", ClaudeDesktopUrlRouter.ArgumentsFor(null, "claude://x"));
    }

    // The two fields the command is built from, with the rest of the record
    // filled in — nothing here depends on them, and naming them at every call
    // site would hide which two actually matter.
    private static UrlRoute Route(string bundle, string? userDataDir) =>
        new(ProfileDirectory: "/tmp/profiles/Claude-Profile-1",
            BundlePath: bundle,
            UserDataDir: userDataDir,
            AlreadyRunning: true,
            Pid: 4242);

    // Default carries no userData directory at all, exactly as LaunchMac does
    // it: setting the variable suppresses the app's own resolution of its
    // sidecar config directory, so a forwarded link could re-trigger the
    // deployment-mode chooser on an already configured profile.
    [Fact]
    public void ARouteWithNoUserDataDirectoryPassesNoEnvironment()
    {
        var arguments = ClaudeDesktopUrlRouter.ArgumentsFor(
            Route("/tmp/Claude.app", null), "claude://x");

        Assert.Equal(new[] { "-a", "/tmp/Claude.app", "claude://x" }, arguments);
        Assert.DoesNotContain("--env", arguments);
    }

    // And one that has it passes it, unconditionally rather than only when the
    // instance is down: `open` applies --env at launch, so it is meaningless for
    // a running instance and harmless, while a link that has to start the
    // profile starts it on the right userData directory.
    [Fact]
    public void ARouteWithAUserDataDirectoryCarriesItAsAnEnvironmentVariable()
    {
        var arguments = ClaudeDesktopUrlRouter.ArgumentsFor(
            Route("/tmp/Claude.app", "/tmp/data"), "claude://x");

        Assert.Equal(
            new[] { "-a", "/tmp/Claude.app", "--env", "CLAUDE_USER_DATA_DIR=/tmp/data", "claude://x" },
            arguments);
    }
}
