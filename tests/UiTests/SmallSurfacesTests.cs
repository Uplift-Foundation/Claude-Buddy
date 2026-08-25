using System;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.UiTests;

// A few one-line answers that nothing else happened to ask for. Each is small,
// and each is the sort of thing that goes wrong quietly.
[Collection("Settings")]
public class SmallSurfacesTests
{
    // ---- naming a profile's colour -----------------------------------------

    // The default profile's colour is not one of the named palette entries — it
    // is its own thing — so asking what to call it falls through the table. The
    // fallback has to be a real palette name, because the settings window feeds
    // it straight to a picker: a name the picker does not know would select
    // nothing and the row would look empty.
    [AvaloniaFact]
    public void TheDefaultProfilesColourStillHasAName()
    {
        var name = ClaudeDesktopColors.NameFor("Claude", isDefault: true);

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Contains(name, ClaudeDesktopColors.Names);
    }

    // A non-default profile's colour is derived from its folder name, and every
    // derived colour IS in the table — so this is the arm that does not fall
    // through, and the two together say the fallback is a fallback rather than
    // the usual answer.
    [AvaloniaFact]
    public void ADerivedColourIsNamedFromThePaletteItCameFrom()
    {
        var name = ClaudeDesktopColors.NameFor("Claude-Profile-1", isDefault: false);

        Assert.Contains(name, ClaudeDesktopColors.Names);
    }

    // ---- the two CLI formats ------------------------------------------------

    // Each CLI reads its own settings pair. They are separate on purpose:
    // someone can reasonably want to read a Codex session and never type into it
    // while doing the opposite for Claude Code. A format wired to the other
    // one's settings would be invisible until exactly that combination.
    [AvaloniaFact]
    public void EachFormatReadsItsOwnChatAndReplySettings()
    {
        ClaudeBuddySettings.ReloadForTests();

        ClaudeBuddySettings.ClaudeCodeChatEnabled = true;
        ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;
        ClaudeBuddySettings.CodexChatEnabled = false;
        ClaudeBuddySettings.CodexReplyEnabled = true;

        Assert.True(CliChatFormat.ClaudeCode.ChatEnabled());
        Assert.False(CliChatFormat.ClaudeCode.ReplyEnabled());
        Assert.False(CliChatFormat.Codex.ChatEnabled());
        Assert.True(CliChatFormat.Codex.ReplyEnabled());
    }

    [AvaloniaFact]
    public void ASessionsSourcePicksItsFormat()
    {
        Assert.Same(CliChatFormat.Codex, CliChatFormat.For(SessionSource.Codex));
        Assert.Same(CliChatFormat.ClaudeCode, CliChatFormat.For(SessionSource.ClaudeCode));
    }

    // ---- a picture that will not decode --------------------------------------

    // OpenClawAvatars really does decode what it is handed, and really does fall
    // back to nothing when it cannot. Worth an explicit test because the app's
    // other image path does NOT — Avalonia's Bitmap.DecodeToWidth hands back an
    // ordinary bitmap for rubbish, which is recorded in ChatPanelMarkdownTests.
    // These two sit either side of the same question and answer it differently.
    [AvaloniaFact]
    public void APictureThatWillNotDecodeCostsTheFaceAndNothingElse()
    {
        const string agent = "undecodable";
        OpenClawAvatars.Forget(agent);

        Assert.Null(OpenClawAvatars.For(agent, new byte[] { 1, 2, 3, 4, 5 }));
    }

    // A truncated real PNG takes the same route — the header is right and the
    // rest is missing, which is what a half-finished download looks like.
    [AvaloniaFact]
    public void ATruncatedPictureAlsoCostsOnlyTheFace()
    {
        const string agent = "truncated";
        OpenClawAvatars.Forget(agent);

        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        Assert.Null(OpenClawAvatars.For(agent, header));
    }
}
