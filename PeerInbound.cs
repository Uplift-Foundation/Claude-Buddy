namespace Orbweaver
{
    internal enum InboundSetting { Accepting, NotAccepting, Unreadable }

    internal enum SettingsEditKind { Changed, Unchanged, Refused }

    internal readonly record struct SettingsEdit(SettingsEditKind Kind, string? Text, string? Reason);

    // CB-267 (stub): rules for "would Claude Code hold a peer message".
    internal static class PeerInbound
    {
        internal const string SettingKey = "crossSessionInbound";
        internal const string AcceptValue = "accept";

        internal static readonly IReadOnlySet<string> HoldingModes =
            new HashSet<string>(StringComparer.Ordinal) { "bypassPermissions" };

        internal static string ButtonLabel => "Let orbs message sessions directly";
        internal static string ConfirmLabel => "Turn it on";
        internal static string DoneText => "Done — new messages go straight in.";
        internal static string TradeOff =>
            "Any Claude session on this computer will then be able to message sessions that run without permission prompts, and those messages go straight in without your approval.";
        internal static string OfferText(string name) =>
            $"{name} runs without permission prompts, so Claude Code holds messages from {Brand.DisplayName} until someone approves them in that session.";
        internal static string HeldNote(string name) =>
            $"Sent to {name}, but Claude Code is holding it until someone approves it in that session.";

        internal static string? LastPermissionMode(IEnumerable<string> lines) => throw new NotImplementedException();
        internal static string? DefaultModeIn(string? settingsText) => throw new NotImplementedException();
        internal static InboundSetting ReadSetting(string? settingsText) => throw new NotImplementedException();
        internal static bool WouldHold(string? sessionMode, string? settingsText) => throw new NotImplementedException();
        internal static SettingsEdit Enable(string? text) => throw new NotImplementedException();
        internal static SettingsEdit Disable(string? text) => throw new NotImplementedException();
        internal static string DisplayPath(string path, string home) => throw new NotImplementedException();
    }
}
