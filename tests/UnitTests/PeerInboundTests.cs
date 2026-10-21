using System.Text.Json.Nodes;
using Xunit;

namespace Orbweaver.Tests;

// CB-267: PeerInbound's pure rules. The transcript rows and settings shapes here
// are the ones measured on a real machine (see the plan's "Confirmed" list):
// `permission-mode` rows with `type` first, repeating about every turn.
public class PeerInboundTests
{
    private static string Row(string mode) =>
        $"{{\"type\":\"permission-mode\",\"permissionMode\":\"{mode}\",\"sessionId\":\"s1\"}}";

    // ---- LastPermissionMode ---------------------------------------------------

    [Fact]
    public void TheLastPermissionModeRowWins()
    {
        var lines = new[] { Row("plan"), "{\"type\":\"user\"}", Row("bypassPermissions"), Row("auto") };
        Assert.Equal("auto", PeerInbound.LastPermissionMode(lines));
    }

    [Fact]
    public void NoPermissionModeRowReadsAsNull()
    {
        Assert.Null(PeerInbound.LastPermissionMode(Array.Empty<string>()));
        Assert.Null(PeerInbound.LastPermissionMode(new[] { "{\"type\":\"user\",\"message\":\"hi\"}" }));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("plan")]
    [InlineData("acceptEdits")]
    [InlineData("auto")]
    [InlineData("dontAsk")]
    [InlineData("bypassPermissions")]
    public void EveryModeIsReturnedVerbatim(string mode) =>
        Assert.Equal(mode, PeerInbound.LastPermissionMode(new[] { Row(mode) }));

    [Fact]
    public void ModeNamesAreNotNormalised()
    {
        Assert.Equal("BypassPermissions", PeerInbound.LastPermissionMode(new[] { Row("BypassPermissions") }));
    }

    [Fact]
    public void MalformedAndHalfWrittenLinesAreSkippedNotFatal()
    {
        var lines = new[]
        {
            Row("plan"),
            "{\"type\":\"permission-mode\",\"permissionMode\":\"bypa",  // half-written tail
            "not json but says permission-mode",
        };
        Assert.Equal("plan", PeerInbound.LastPermissionMode(lines));
    }

    [Fact]
    public void ARowThatIsNotAnObjectOrHasAnotherTypeIsIgnored()
    {
        var lines = new[]
        {
            Row("plan"),
            "[\"permission-mode\"]",
            "\"permission-mode\"",
            "{\"type\":7,\"permissionMode\":\"auto\",\"note\":\"permission-mode\"}",
            "{\"type\":\"user\",\"permissionMode\":\"auto\",\"note\":\"permission-mode\"}",
            "{\"permissionMode\":\"auto\",\"note\":\"permission-mode\"}",
        };
        Assert.Equal("plan", PeerInbound.LastPermissionMode(lines));
    }

    [Fact]
    public void APermissionModeRowWithoutAUsableModeKeepsTheEarlierOne()
    {
        var lines = new[]
        {
            Row("plan"),
            "{\"type\":\"permission-mode\"}",
            "{\"type\":\"permission-mode\",\"permissionMode\":5}",
            "{\"type\":\"permission-mode\",\"permissionMode\":\"\"}",
        };
        Assert.Equal("plan", PeerInbound.LastPermissionMode(lines));
    }

    [Fact]
    public void ATranscriptRowQuotingAPermissionModeRowIsNotOne()
    {
        // A user pasting a row into chat: the quoted text is escaped inside a
        // string value, and the row's own top-level type is "user".
        var quoted = JsonNode.Parse("{\"type\":\"user\",\"message\":{\"content\":\"" +
            Row("bypassPermissions").Replace("\"", "\\\"") + "\"}}")!.ToJsonString();
        Assert.Contains("permission-mode", quoted);
        Assert.Equal("plan", PeerInbound.LastPermissionMode(new[] { Row("plan"), quoted }));
    }

    // ---- DefaultModeIn ----------------------------------------------------------

    [Theory]
    [InlineData("{\"permissions\":{\"defaultMode\":\"bypassPermissions\"}}", "bypassPermissions")]
    [InlineData("{\"permissions\":{\"defaultMode\":\"plan\",\"allow\":[]}}", "plan")]
    [InlineData("{}", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("{\"permissions\":[]}", null)]
    [InlineData("{\"permissions\":{}}", null)]
    [InlineData("{\"permissions\":{\"defaultMode\":true}}", null)]
    [InlineData("{\"permissions\":{\"defaultMode\":\"\"}}", null)]
    [InlineData("{\"permissions\":{\"defaultMode\":null}}", null)]
    [InlineData("{\"defaultMode\":\"bypassPermissions\"}", null)]
    [InlineData("{ not json", null)]
    [InlineData("[1,2]", null)]
    public void DefaultModeInReadsOnlyPermissionsDefaultMode(string? text, string? expected) =>
        Assert.Equal(expected, PeerInbound.DefaultModeIn(text));

    // ---- ReadSetting --------------------------------------------------------------

    [Theory]
    [InlineData(null, "NotAccepting")]
    [InlineData("", "NotAccepting")]
    [InlineData("   \n", "NotAccepting")]
    [InlineData("{}", "NotAccepting")]
    [InlineData("{\"crossSessionInbound\":\"accept\"}", "Accepting")]
    [InlineData("\uFEFF{\"crossSessionInbound\":\"accept\"}", "Accepting")]
    [InlineData("{\"crossSessionInbound\":\"Accept\"}", "NotAccepting")]
    [InlineData("{\"crossSessionInbound\":\"ACCEPT\"}", "NotAccepting")]
    [InlineData("{\"crossSessionInbound\":true}", "NotAccepting")]
    [InlineData("{\"crossSessionInbound\":\"deny\"}", "NotAccepting")]
    [InlineData("{\"crossSessionInbound\":null}", "NotAccepting")]
    [InlineData("{\"crossSessionInbound\":[\"accept\"]}", "NotAccepting")]
    [InlineData("[\"accept\"]", "Unreadable")]
    [InlineData("null", "Unreadable")]
    [InlineData("{ nope", "Unreadable")]
    [InlineData("{\"a\":1,}", "Unreadable")]
    [InlineData("{/*c*/\"a\":1}", "Unreadable")]
    [InlineData("{\"crossSessionInbound\":\"accept\",\"crossSessionInbound\":\"no\"}", "Unreadable")]
    public void ReadSettingShapes(string? text, string expected) =>
        Assert.Equal(expected, PeerInbound.ReadSetting(text).ToString());

    // ---- WouldHold truth table ----------------------------------------------------

    private const string Bypass = "{\"permissions\":{\"defaultMode\":\"bypassPermissions\"}}";
    private const string BypassAccept =
        "{\"permissions\":{\"defaultMode\":\"bypassPermissions\"},\"crossSessionInbound\":\"accept\"}";
    private const string PlanDefault = "{\"permissions\":{\"defaultMode\":\"plan\"}}";

    [Theory]
    // session mode known: it decides, whatever the default says
    [InlineData("bypassPermissions", null, true)]
    [InlineData("bypassPermissions", "{}", true)]
    [InlineData("bypassPermissions", PlanDefault, true)]
    [InlineData("bypassPermissions", "{\"crossSessionInbound\":\"accept\"}", false)]
    [InlineData("bypassPermissions", "{\"crossSessionInbound\":\"Accept\"}", true)]
    [InlineData("plan", Bypass, false)]
    [InlineData("auto", Bypass, false)]
    [InlineData("default", Bypass, false)]
    [InlineData("BypassPermissions", Bypass, false)]
    // no session mode: the settings default decides
    [InlineData(null, Bypass, true)]
    [InlineData(null, BypassAccept, false)]
    [InlineData(null, PlanDefault, false)]
    [InlineData(null, "{}", false)]
    [InlineData(null, null, false)]
    [InlineData(null, "{ nope", false)]
    // unreadable settings never count as accepting
    [InlineData("bypassPermissions", "{ nope", true)]
    public void WouldHoldTruthTable(string? sessionMode, string? settings, bool expected) =>
        Assert.Equal(expected, PeerInbound.WouldHold(sessionMode, settings));

    [Fact]
    public void OnlyBypassPermissionsHoldsForNow() =>
        Assert.Equal(new[] { "bypassPermissions" }, PeerInbound.HoldingModes.ToArray());

    // ---- Enable / Disable -----------------------------------------------------------

    // A realistic settings.json: the installer's hooks block, permissions, env,
    // model, statusLine, an emoji, and number spellings a re-serialiser tends to
    // normalise.
    private const string Realistic = """
        {
          "model": "opus",
          "permissions": { "defaultMode": "bypassPermissions", "allow": ["Bash(ls:*)"] },
          "env": { "FOO": "bar", "EMOJI": "🙂 café" },
          "statusLine": { "type": "command", "command": "~/.claude/statusline.sh" },
          "hooks": {
            "PreToolUse": [
              { "matcher": "*", "hooks": [ { "type": "command", "command": "bash \"$HOME/.claude/orbweaver/OrbweaverHook.sh\" working", "async": true } ] }
            ]
          },
          "ratio": 1.0,
          "big": 1e3,
          "tiny": 0.10
        }
        """ + "\n";

    private static void AssertSameExceptKey(string before, string after)
    {
        var a = JsonNode.Parse(before)!.AsObject();
        var b = JsonNode.Parse(after)!.AsObject();
        a.Remove(PeerInbound.SettingKey);
        b.Remove(PeerInbound.SettingKey);
        Assert.True(JsonNode.DeepEquals(a, b), $"changed more than the key:\n{before}\n---\n{after}");
    }

    [Fact]
    public void EnableAddsOnlyTheKeyAndKeepsEverythingElse()
    {
        var edit = PeerInbound.Enable(Realistic);

        Assert.Equal(SettingsEditKind.Changed, edit.Kind);
        Assert.Null(edit.Reason);
        Assert.Equal(InboundSetting.Accepting, PeerInbound.ReadSetting(edit.Text));
        AssertSameExceptKey(Realistic, edit.Text!);
        // Raw number text, non-ASCII text and key order survive.
        Assert.Contains("\"ratio\": 1.0", edit.Text);
        Assert.Contains("\"big\": 1e3", edit.Text);
        Assert.Contains("\"tiny\": 0.10", edit.Text);
        Assert.Contains("🙂 café", edit.Text);
        Assert.True(edit.Text!.IndexOf("\"model\"") < edit.Text.IndexOf("\"permissions\""));
        Assert.True(edit.Text.IndexOf("\"tiny\"") < edit.Text.IndexOf(PeerInbound.SettingKey));
        Assert.EndsWith("\n", edit.Text);
        Assert.Contains("\n  \"model\"", edit.Text); // two-space indent
    }

    [Fact]
    public void AnEscapedSurrogatePairInTheFileIsWrittenAsTheCharacterButAnEscapedBackslashIsNot()
    {
        // "\uD83D\uDE42" in the JSON text is a real escape pair (the emoji); a
        // doubled backslash before it is the text backslash + "uD83D...".
        var text = "{\"pair\":\"\\uD83D\\uDE42\",\"literal\":\"\\\\uD83D\\\\uDE42\",\"odd\":\"\\\\\\uD83D\\uDE42\"}";
        var edit = PeerInbound.Enable(text);

        Assert.Equal(SettingsEditKind.Changed, edit.Kind);
        var before = JsonNode.Parse(text)!.AsObject();
        var after = JsonNode.Parse(edit.Text!)!.AsObject();
        Assert.Equal("\U0001F642", (string?)after["pair"]);
        Assert.Contains("\U0001F642", edit.Text);
        Assert.Equal((string?)before["literal"], (string?)after["literal"]);
        Assert.Equal((string?)before["odd"], (string?)after["odd"]);
        Assert.Contains("\\\\uD83D", edit.Text); // the literal one is still escaped text
    }

    [Fact]
    public void EnableReplacesAnExistingValueInPlace()
    {
        var text = "{\"a\":1,\"crossSessionInbound\":\"Accept\",\"b\":2}";
        var edit = PeerInbound.Enable(text);

        Assert.Equal(SettingsEditKind.Changed, edit.Kind);
        Assert.Equal(InboundSetting.Accepting, PeerInbound.ReadSetting(edit.Text));
        Assert.True(edit.Text!.IndexOf("\"a\"") < edit.Text.IndexOf(PeerInbound.SettingKey));
        Assert.True(edit.Text.IndexOf(PeerInbound.SettingKey) < edit.Text.IndexOf("\"b\""));
        Assert.False(edit.Text.EndsWith('\n')); // the original had none
    }

    [Fact]
    public void EnableWhenAlreadyAcceptingChangesNothing()
    {
        var edit = PeerInbound.Enable("{\"crossSessionInbound\":\"accept\"}");
        Assert.Equal(SettingsEditKind.Unchanged, edit.Kind);
        Assert.Null(edit.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    [InlineData("\uFEFF")]
    public void EnableOnNothingBuildsAnObjectWithOnlyTheKey(string? text)
    {
        var edit = PeerInbound.Enable(text);
        Assert.Equal(SettingsEditKind.Changed, edit.Kind);
        var obj = JsonNode.Parse(edit.Text!)!.AsObject();
        Assert.Single(obj);
        Assert.Equal("accept", (string?)obj[PeerInbound.SettingKey]);
        Assert.EndsWith("\n", edit.Text);
    }

    [Fact]
    public void EnableStripsALeadingBomAndKeepsCrLf()
    {
        var text = "\uFEFF{\r\n  \"a\": 1\r\n}\r\n";
        var edit = PeerInbound.Enable(text);
        Assert.Equal(SettingsEditKind.Changed, edit.Kind);
        Assert.False(edit.Text!.StartsWith('\uFEFF'));
        Assert.DoesNotContain("\n", edit.Text.Replace("\r\n", ""));
        Assert.EndsWith("\r\n", edit.Text);
    }

    [Theory]
    [InlineData("{ nope")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{/* c */\"a\":1}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("[1]")]
    [InlineData("null")]
    [InlineData("\"s\"")]
    [InlineData("{} trailing")]
    public void BothEditsRefuseWhatTheyCannotParse(string text)
    {
        foreach (var edit in new[] { PeerInbound.Enable(text), PeerInbound.Disable(text) })
        {
            Assert.Equal(SettingsEditKind.Refused, edit.Kind);
            Assert.Null(edit.Text);
            Assert.False(string.IsNullOrWhiteSpace(edit.Reason));
        }
    }

    [Fact]
    public void DisableRemovesTheKeyAndKeepsEverythingElse()
    {
        var enabled = PeerInbound.Enable(Realistic).Text!;
        var edit = PeerInbound.Disable(enabled);

        Assert.Equal(SettingsEditKind.Changed, edit.Kind);
        Assert.DoesNotContain(PeerInbound.SettingKey, edit.Text);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Realistic), JsonNode.Parse(edit.Text!)));
        Assert.Contains("\"ratio\": 1.0", edit.Text);
        Assert.EndsWith("\n", edit.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"a\":1}")]
    public void DisableWithoutTheKeyChangesNothing(string? text)
    {
        var edit = PeerInbound.Disable(text);
        Assert.Equal(SettingsEditKind.Unchanged, edit.Kind);
        Assert.Null(edit.Text);
    }

    [Fact]
    public void DisableRemovesTheKeyWhateverItsValue()
    {
        var edit = PeerInbound.Disable("{\"crossSessionInbound\":\"Accept\",\"a\":1}");
        Assert.Equal(SettingsEditKind.Changed, edit.Kind);
        Assert.DoesNotContain(PeerInbound.SettingKey, edit.Text);
    }

    // ---- copy -------------------------------------------------------------------------

    [Fact]
    public void CopyNamesTheBrandAndTheSession()
    {
        Assert.Contains(Brand.DisplayName, PeerInbound.OfferText("job-hunter"));
        Assert.Contains("job-hunter", PeerInbound.OfferText("job-hunter"));
        Assert.Contains("job-hunter", PeerInbound.HeldNote("job-hunter"));
        Assert.False(string.IsNullOrWhiteSpace(PeerInbound.ButtonLabel));
        Assert.False(string.IsNullOrWhiteSpace(PeerInbound.ConfirmLabel));
        Assert.Contains("without your approval", PeerInbound.TradeOff);
        Assert.StartsWith("Done", PeerInbound.DoneText);
        Assert.DoesNotContain(Brand.Legacy.DisplayName, PeerInbound.OfferText("x"));
    }

    // ---- DisplayPath ------------------------------------------------------------------

    [Theory]
    [InlineData("/Users/w/.claude/settings.json", "/Users/w", "~/.claude/settings.json")]
    [InlineData("/Users/w/.claude/settings.json", "/Users/w/", "~/.claude/settings.json")]
    [InlineData("/Users/wx/.claude/settings.json", "/Users/w", "/Users/wx/.claude/settings.json")]
    [InlineData("/Users/w", "/Users/w", "/Users/w")]
    [InlineData("/etc/x.json", "/Users/w", "/etc/x.json")]
    [InlineData("/Users/w/a.json", "", "/Users/w/a.json")]
    [InlineData("C:\\Users\\w\\.claude\\settings.json", "C:\\Users\\w", "~\\.claude\\settings.json")]
    public void DisplayPathShortensOnlyAHomePrefix(string path, string home, string expected)
    {
        // The backslash case is only a prefix on a case-aware match everywhere:
        // the separator check accepts both slashes on every OS.
        Assert.Equal(expected, PeerInbound.DisplayPath(path, home));
    }

    [Fact]
    public void DisplayPathIsCaseInsensitiveOnlyOnWindows()
    {
        var result = PeerInbound.DisplayPath("/users/W/a.json", "/Users/w");
        Assert.Equal(OperatingSystem.IsWindows() ? "~/a.json" : "/users/W/a.json", result);
    }
}
